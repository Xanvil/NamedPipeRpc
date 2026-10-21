using System.IO.Pipes;

namespace Xanvil.NamedPipeRpc;


public class RpcService : IDisposable
{
    public string PipeName { get; }
    public HandlerRouter Router { get; }
    public PipePeers Peers { get; } = new();
    RpcServiceOption _option;
    PeerOption _peerOption;

    CancellationTokenSource? _cts;
    int _started; // 0/1，保证 Start 幂等

    public RpcService(string pipeName, HandlerRouter router, RpcServiceOption? option = null)
    {
        PipeName = pipeName;
        Router = router;
        _option = option ?? new();

        _peerOption = new PeerOption(_option.Version)
        {
            MaxConcurrent = _option.MaxConcurrent,
            SendBuffer = _option.SendBuffer,
            ReceiveBuffer = _option.ReceiveBuffer,
            RequestTimeout = _option.RequestTimeout,
        };

        // 挂钩本端目录变更 → 向已连接 Peers 推 Sys（非 Router 对外业务事件）
        WireRouterCatalogBroadcast();
    }

    /// <summary>
    /// 启动接受循环。可重复调用；仅首次生效，避免双 ConnectTask 抢同一管道。
    /// </summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectTask(_cts.Token));
    }

    public void Close()
    {
        Dispose();
    }

    public event Action<NamedPipePeer>? Connected;
    public event Action<string, Exception?>? OnError;

    /// <summary>
    /// 订阅 Router 包内开闭回调：Add（新方法）/ Remove 成功 → 推对应 Sys；
    /// NotifyClosed 时先清各 peer 订阅表再推送。
    /// </summary>
    void WireRouterCatalogBroadcast()
    {
        Router.OnRequestOpened = info =>
            _ = BroadcastCatalogSysAsync(RpcSystemMethods.RequestOpened, info);

        Router.OnRequestClosed = info =>
            _ = BroadcastCatalogSysAsync(RpcSystemMethods.RequestClosed, info);

        Router.OnNotifyOpened = info =>
            _ = BroadcastCatalogSysAsync(RpcSystemMethods.NotifyOpened, info);

        Router.OnNotifyClosed = info =>
        {
            // Closed：先清各连接上的订阅，再推 Sys，避免推送窗口内仍被 NotifySubscribers 命中
            ClearNotifySubscriptions(info.MethodName);
            _ = BroadcastCatalogSysAsync(RpcSystemMethods.NotifyClosed, info);
        };
    }

    /// <summary>解除 Router 包内开闭回调，避免 Service 释放后仍被触发。</summary>
    void UnwireRouterCatalogBroadcast()
    {
        Router.OnRequestOpened = null;
        Router.OnRequestClosed = null;
        Router.OnNotifyOpened = null;
        Router.OnNotifyClosed = null;
    }

    /// <summary>
    /// 向当前已连接 Peers 广播无应答目录 Sys（Opened / Closed）。
    /// 单 peer 失败经 <see cref="OnError"/> 报告，不影响其余连接。
    /// </summary>
    async Task BroadcastCatalogSysAsync<TMsg>(string sysMethod, TMsg payload)
    {
        foreach (var peer in Peers)
        {
            try
            {
                await peer.SendSysAsync(sysMethod, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                OnError?.Invoke($"推送目录 Sys 失败: {sysMethod}", ex);
            }
        }
    }

    /// <summary>Notify 目录关闭时，清除各 peer 上对该方法的订阅记录。</summary>
    void ClearNotifySubscriptions(string methodName)
    {
        foreach (var peer in Peers)
            peer.ClearSubscription(methodName);
    }

    /// <summary>
    /// 循环接受连接。单次失败只丢弃当前流并重试，不得关闭整个 <see cref="RpcService"/>，
    /// 也不得 Close 已交给 <see cref="NamedPipePeer"/> 的管道流。
    /// </summary>
    async Task ConnectTask(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pending = null;
            try
            {
                var maxInstances = _option.MaxConnect;
                if (maxInstances < 1)
                    maxInstances = NamedPipeServerStream.MaxAllowedServerInstances;

                pending = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    maxInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pending.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // 所有权移交 Peer；此后不得在本循环 Close pending
                var peer = new NamedPipePeer(pending, Router, _peerOption);
                pending = null;
                peer.Start();
                Connected?.Invoke(peer);
                Peers.Add(peer);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pending?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pending?.Dispose();
                OnError?.Invoke("等待连接异常，将重试", ex);
                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    bool _isDis;
    public void Dispose()
    {
        if (_isDis) return;
        _isDis = true;
        UnwireRouterCatalogBroadcast();
        _cts?.Cancel();
    }
}
