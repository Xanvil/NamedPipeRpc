using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Xanvil.NamedPipeRpc;

public class NamedPipePeer : IDisposable
{
    static JsonSerializerOptions serilizerOptions = new JsonSerializerOptions
    {
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
    };

    public event Action<NamedPipePeer>? DisConnected;
    public event Action<string, Exception?>? OnError;

    /// <summary>
    /// 客户端：对端不存在期望的 Notify 方法时引发（仍保留期望集，便于稍后自动补订）。
    /// </summary>
    public event EventHandler<RpcMethodNameEventArgs>? NotifyNotFound;

    /// <summary>
    /// 客户端：对端不存在所请求的 Request 方法时引发（普通 <see cref="Request"/> 收到 UnK）。
    /// 与 <see cref="NotifyNotFound"/> 对称；异常仍会向上抛出。
    /// </summary>
    public event EventHandler<RpcMethodNameEventArgs>? RequestNotFound;

    /// <summary>客户端：对端 Request 方法已开启（Sys <see cref="RpcSystemMethods.RequestOpened"/>）。</summary>
    public event EventHandler<RpcRequestMethodEventArgs>? RequestOpened;

    /// <summary>客户端：对端 Request 方法已关闭（Sys <see cref="RpcSystemMethods.RequestClosed"/>）。</summary>
    public event EventHandler<RpcRequestMethodEventArgs>? RequestClosed;

    /// <summary>
    /// 客户端：对端 Notify 方法已开启。
    /// 若方法在期望集中，会先自动 <see cref="SubscribeAsync"/>，再引发本事件。
    /// </summary>
    public event EventHandler<RpcNotifyMethodEventArgs>? NotifyOpened;

    /// <summary>
    /// 客户端：对端 Notify 方法已关闭（期望集保留，便于稍后自动补订）。
    /// </summary>
    public event EventHandler<RpcNotifyMethodEventArgs>? NotifyClosed;

    public bool IsConnected { get => PipeStream.IsConnected; }

    public readonly PipeStream PipeStream;
    public readonly HandlerRouter Router;
    PeerOption _option;

    Channel<IpcInMsg> _recieveChannel;
    Channel<IpcOutMsg> _sendChannel;


    ConcurrentDictionary<long, TaskCompletionSource<object?>> _waitingRes;

    CancellationTokenSource? _cts;

    long _cid = 0;
    SemaphoreSlim _handlerSlim;

    /// <summary>服务端：本连接上客户端已订阅的 Notify 方法名（值占位）；断开即随 Peer 销毁。</summary>
    readonly ConcurrentDictionary<string, byte> _subscriptions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>客户端：期望订阅的 Notify 方法名（值占位；含当时对端尚不存在的）；Unsubscribe 后移出。</summary>
    readonly ConcurrentDictionary<string, byte> _desiredNotifies = new(StringComparer.OrdinalIgnoreCase);

    public NamedPipePeer(PipeStream service, HandlerRouter router, PeerOption option)
    {
        PipeStream = service;
        Router = router;
        _option = option;

        _recieveChannel = Channel.CreateBounded<IpcInMsg>(option.ReceiveBuffer);
        _sendChannel = Channel.CreateBounded<IpcOutMsg>(option.SendBuffer);
        _waitingRes = new();
        _handlerSlim = new SemaphoreSlim(option.MaxConcurrent, option.MaxConcurrent);
    }

    /// <summary>
    /// 启动收发与消息处理循环，并在后台对本端已注册的 Notify Handler 自动调用
    /// <see cref="SubscribeLocalNotifyHandlersAsync"/>（写入期望集并尝试向对端订阅）。
    /// </summary>
    public void Start()
    {
        _cts = new();
        var ct = _cts.Token;
        Task.Run(() => RecieveTask(ct));
        Task.Run(() => SendTask(ct));
        Task.Run(() => MsgHandleTask(ct));
        // 与收发并行：对本端 Notify 自动订阅（无本地 Notify 时为空操作）
        _ = Task.Run(() => AutoSubscribeLocalNotifyHandlersAsync(ct), ct);
    }

    /// <summary>
    /// Start 后后台自动订阅：异常经 <see cref="OnError"/> 报告，不向外抛出。
    /// </summary>
    async Task AutoSubscribeLocalNotifyHandlersAsync(CancellationToken ct)
    {
        try
        {
            await SubscribeLocalNotifyHandlersAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            OnError?.Invoke("自动订阅本端 Notify 异常", ex);
        }
    }
    public void Close()
    {
        Dispose();
    }

    public Task Notify(string method, CancellationToken ct = default)
    {
        return Notify(method, Unit.Value, ct);
    }

    public Task Notify<TMsg>(string method, TMsg msg, CancellationToken ct = default)
    {
        return SendOneWayAsync(method, msg, MessageType.Ntf, ct);
    }

    public Task Request(string method, CancellationToken ct = default)
    {
        return Request<Unit, Unit>(method, Unit.Value, ct);
    }
    public Task Request<TRequest>(string method, TRequest req, CancellationToken ct = default)
    {
        return Request<TRequest, Unit>(method, req, ct);
    }

    public Task<TResponse> Request<TRequest, TResponse>(string method, TRequest req, CancellationToken ct = default)
    {
        return SendAndWaitAsync<TRequest, TResponse>(method, req, MessageType.Req, ct);
    }

    /// <summary>
    /// 包内：无应答 Sys 推送（Opened/Closed 等）。发送形态与 <see cref="Notify{TMsg}"/> 相同，仅 MessageType 为 Sys。
    /// </summary>
    internal Task SendSysAsync<TMsg>(string method, TMsg msg, CancellationToken ct = default)
    {
        // 与 Notify 同一套写出；不走等待表
        return SendOneWayAsync(method, msg, MessageType.Sys, ct);
    }

    /// <summary>
    /// 包内：带应答 Sys。发送/等待与 <see cref="Request{TRequest,TResponse}"/> 完全相同，仅 MessageType 为 Sys。
    /// </summary>
    internal Task<TResponse> SendSysAsync<TRequest, TResponse>(string method, TRequest req, CancellationToken ct = default)
    {
        return SendAndWaitAsync<TRequest, TResponse>(method, req, MessageType.Sys, ct);
    }

    /// <summary>查询对端已暴露的 Request 方法列表。</summary>
    public Task<RpcRequestMethodInfo[]> ListRemoteRequestMethodsAsync(CancellationToken ct = default)
    {
        return SendSysAsync<Unit, RpcRequestMethodInfo[]>(RpcSystemMethods.ListRequests, Unit.Value, ct);
    }

    /// <summary>查询对端已暴露的 Notify 方法列表。</summary>
    public Task<RpcNotifyMethodInfo[]> ListRemoteNotifyMethodsAsync(CancellationToken ct = default)
    {
        return SendSysAsync<Unit, RpcNotifyMethodInfo[]>(RpcSystemMethods.ListNotifies, Unit.Value, ct);
    }

    /// <summary>
    /// 将方法加入客户端期望集，并向对端发起单条 Notify 订阅。
    /// 对端不存在时引发 <see cref="NotifyNotFound"/>，仍保留期望集。
    /// </summary>
    /// <returns>成功时返回对端方法元数据；对端不存在时返回 <c>null</c>。</returns>
    public async Task<RpcNotifyMethodInfo?> SubscribeAsync(string method, CancellationToken ct = default)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("method 不能为空", nameof(method));

        _desiredNotifies.TryAdd(method, 0);

        try
        {
            return await SendSysAsync<string, RpcNotifyMethodInfo>(RpcSystemMethods.Subscribe, method, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMethodMissingException(ex))
        {
            NotifyNotFound?.Invoke(this, new RpcMethodNameEventArgs(method));
            return null;
        }
    }

    /// <summary>
    /// 从客户端期望集移除，并向对端取消单条 Notify 订阅（之后不再自动补订）。
    /// </summary>
    public async Task UnsubscribeAsync(string method, CancellationToken ct = default)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));

        _desiredNotifies.TryRemove(method, out _);

        await SendSysAsync<string, Unit>(RpcSystemMethods.Unsubscribe, method, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 对本端 Router 已注册的全部 Notify Handler，逐个调用 <see cref="SubscribeAsync"/>（写入期望集并尝试订阅）。
    /// <para><see cref="Start"/> 会自动调用；Start 之后若又新增本端 Notify Handler，可再手动调用一次。</para>
    /// </summary>
    public async Task SubscribeLocalNotifyHandlersAsync(CancellationToken ct = default)
    {
        var names = Router.SnapshotNotifyMethodNames();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            await SubscribeAsync(name, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 服务端：判断该连接是否已订阅指定 Notify 方法（供 <c>NotifySubscribers</c> 过滤）。
    /// </summary>
    public bool IsSubscribed(string method)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        return _subscriptions.ContainsKey(method);
    }

    /// <summary>服务端：清除指定 Notify 的订阅记录（目录 Closed 时由上层调用）。</summary>
    internal bool ClearSubscription(string method)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        return _subscriptions.TryRemove(method, out _);
    }

    /// <summary>客户端：方法是否仍在期望集中（自动补订时查询）。</summary>
    internal bool IsDesiredNotify(string method)
    {
        if (method is null) throw new ArgumentNullException(nameof(method));
        return _desiredNotifies.ContainsKey(method);
    }

    /// <summary>
    /// 无应答发送（Ntf / 无应答 Sys）：写出 Method + Payload + Header 后入发送队列。
    /// </summary>
    async Task SendOneWayAsync<TMsg>(string method, TMsg msg, MessageType messageType, CancellationToken ct)
    {
        var outMsg = new IpcOutMsg();
        try
        {
            outMsg.WriteMethod(method);
            outMsg.WritePayload(msg, typeof(TMsg), serilizerOptions);
            outMsg.WriteHeader(Interlocked.Increment(ref _cid), _option.V, messageType);
            await _sendChannel.Writer.WriteAsync(outMsg, ct);
        }
        catch (Exception)
        {
            outMsg.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 带应答发送（Req / Sys）：分配 Id、登记 <see cref="_waitingRes"/>，等待同 Id 的 Res / Err / UnK。
    /// <para>
    /// 普通 Req 收到 UnK（方法不存在）时先引发 <see cref="RequestNotFound"/>，再抛出
    /// <see cref="KeyNotFoundException"/>；Sys（如 Subscribe）由调用方自行映射为
    /// <see cref="NotifyNotFound"/> 等。
    /// </para>
    /// </summary>
    async Task<TResponse> SendAndWaitAsync<TRequest, TResponse>(
        string method, TRequest req, MessageType messageType, CancellationToken ct)
    {
        var outMsg = new IpcOutMsg();
        long id = Interlocked.Increment(ref _cid);
        var tcs = new TaskCompletionSource<object?>(typeof(TResponse));
        _waitingRes[id] = tcs;

        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            outMsg.WriteMethod(method);
            outMsg.WritePayload(req, typeof(TRequest), serilizerOptions);
            outMsg.WriteHeader(id, _option.V, messageType);

            await _sendChannel.Writer.WriteAsync(outMsg, _cts?.Token ?? default);
            linkedCts.CancelAfter(_option.RequestTimeout);
            linkedCts.Token.Register(() => tcs.TrySetCanceled());

            var robj = await tcs.Task;

            if (robj is null)
                return default!;

            if (robj is TResponse r)
                return r;

            // 避免「类型略有差异」时静默返回 default，导致调用方补空 DTO 且耗时为 0
            return (TResponse)robj;
        }
        catch (OperationCanceledException)
        {
            var cMsg = new IpcOutMsg();
            cMsg.WriteHeader(id, _option.V, MessageType.Can);
            await _sendChannel.Writer.WriteAsync(cMsg, _cts?.Token ?? default);
            return default!;
        }
        catch (Exception ex) when (messageType == MessageType.Req && IsMethodMissingException(ex))
        {
            // Request 期望校验：对端无此方法 → 远端事件，再把异常交给调用方
            RequestNotFound?.Invoke(this, new RpcMethodNameEventArgs(method));
            throw;
        }
        finally
        {
            _waitingRes.TryRemove(id, out _);
            linkedCts.Dispose();
        }
    }

    /// <summary>判断是否为「方法不存在」类异常（UnK 载荷或包装后的 KeyNotFoundException）。</summary>
    static bool IsMethodMissingException(Exception ex)
    {
        if (ex is KeyNotFoundException) return true;
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
            {
                if (inner is KeyNotFoundException) return true;
            }
        }
        return ex.InnerException is KeyNotFoundException;
    }
    async Task RecieveTask(CancellationToken ct)
    {
        byte[] tBuffer = new byte[256];
        try
        {

            while (!ct.IsCancellationRequested)
            {
                //读取消息头
                int readByteCount = 0;
                int headLen = MsgHeader.Fix_Length;
                while (readByteCount < headLen)
                {
                    int c = await PipeStream.ReadAsync(tBuffer, readByteCount, headLen - readByteCount, ct);
                    if (c == 0) throw new EndOfStreamException();
                    readByteCount += c;
                }
                var inMsg = new IpcInMsg(tBuffer);
                //var header = MsgHeader.DeserializeFromSpan(tBuffer);
                if (inMsg.Header.V != _option.V) throw new InvalidDataException("Version 不匹配");
                //读取method
                int methodLen = inMsg.Header.MethodLength;

                readByteCount = 0;
                if (methodLen > 0)
                {
                    while (readByteCount < methodLen)
                    {
                        int c = await PipeStream.ReadAsync(tBuffer, readByteCount, methodLen - readByteCount, ct);
                        if (c == 0) throw new EndOfStreamException();
                        readByteCount += c;
                    }
                    //mehtod = Encoding.UTF8.GetString(tBuffer, 0, methodLen);
                    inMsg.WriteMethod(tBuffer);
                }

                //读取载荷
                int payloadLen = inMsg.Header.PayloadLength;
                readByteCount = 0;
                if (payloadLen > 0)
                {
                    inMsg.EnsureCapacity(payloadLen);
                    while (readByteCount < payloadLen)
                    {
                        int c = await PipeStream.ReadAsync(inMsg.UnderlyingArray, readByteCount, payloadLen - readByteCount, ct);
                        if (c == 0) throw new EndOfStreamException();
                        readByteCount += c;
                    }
                    inMsg.Advance(readByteCount);
                }


                await _recieveChannel.Writer.WriteAsync(inMsg, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            OnError?.Invoke("接收异常", e);
        }
        finally
        {
            Dispose();
        }
    }
    async Task SendTask(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                var msg = await _sendChannel.Reader.ReadAsync();
                if (ct.IsCancellationRequested) continue;
                using (msg)
                {
                    //发送
                    await PipeStream.WriteAsync(msg.UnderlyingArray, 0, msg.WrittenCount);
                    await PipeStream.FlushAsync();
                }
            }

        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            OnError?.Invoke("接收异常", e);
        }
        finally
        {
            Dispose();
        }
    }
    ConcurrentDictionary<long, CancellationTokenSource> _taskCancels = new();
    async Task MsgHandleTask(CancellationToken ct)
    {
        try
        {

            while (true)
            {
                var msg = await _recieveChannel.Reader.ReadAsync();
                if (ct.IsCancellationRequested) continue;
                await _handlerSlim.WaitAsync();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        using (msg)
                        {
                            switch (msg.Header.Type)
                            {
                                case MessageType.None:
                                    break;
                                case MessageType.Sys:
                                    await HandleIncomingSysAsync(msg, ct);
                                    break;
                                case MessageType.Req:
                                    await HandleIncomingReqAsync(msg, ct);
                                    break;
                                case MessageType.Ntf:
                                    await HandleIncomingNtfAsync(msg, ct);
                                    break;
                                case MessageType.Res:
                                    HandleIncomingRes(msg);
                                    break;
                                case MessageType.Can:
                                    HandleIncomingCan(msg);
                                    break;
                                case MessageType.Err:
                                    HandleIncomingErr(msg);
                                    break;
                                case MessageType.UnK:
                                    HandleIncomingUnK(msg);
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke("消息处理异常", ex);
                    }
                    finally
                    {
                        _handlerSlim.Release();
                    }

                }, ct);

            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }
        catch (Exception e)
        {
            OnError?.Invoke("接收异常", e);
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>处理入站 Req：查找 Handler，成功 Res / 异常 Err / 未知 UnK。</summary>
    async Task HandleIncomingReqAsync(IpcInMsg msg, CancellationToken ct)
    {
        if (msg.Method != null && Router.GetRequestHandler(msg.Method) is IRequestHandlerWrapper handler)
        {
            var cts = _taskCancels.GetOrAdd(msg.Header.Id, _ => CancellationTokenSource.CreateLinkedTokenSource(ct));
            try
            {
                var req = msg.DeserializePayload(handler.RequestType, serilizerOptions);
                var res = await handler.Handle(this, msg.Method, msg.Header.Id, req!, cts.Token);
                await ReplyResAsync(msg.Header.Id, res, handler.ResponseType);
            }
            catch (Exception e)
            {
                await ReplyErrAsync(msg.Header.Id, e.Message);
            }
            finally
            {
                if (_taskCancels.TryRemove(msg.Header.Id, out cts))
                    cts.Dispose();
            }
        }
        else
        {
            await ReplyUnKAsync(msg.Header.Id);
        }
    }

    /// <summary>处理入站 Ntf：查找 Handler 并调用；未知方法回 UnK。</summary>
    async Task HandleIncomingNtfAsync(IpcInMsg msg, CancellationToken ct)
    {
        if (msg.Method != null && Router.GetNotifyHandler(msg.Method) is INotifyHandlerWrapper nhandler)
        {
            try
            {
                var payload = msg.DeserializePayload(nhandler.MsgType, serilizerOptions);
                await nhandler.Handle(this, msg.Method, msg.Header.Id, payload!, ct);
            }
            catch (Exception)
            {
            }
        }
        else
        {
            await ReplyUnKAsync(msg.Header.Id);
        }
    }

    /// <summary>处理入站 Res：完成对应等待中的请求。</summary>
    void HandleIncomingRes(IpcInMsg msg)
    {
        if (_waitingRes.TryGetValue(msg.Header.Id, out var task))
        {
            var res = msg.DeserializePayload((Type)task.Task.AsyncState!, serilizerOptions);
            task.SetResult(res);
        }
    }

    /// <summary>处理入站 Can：取消对应正在执行的 Handler。</summary>
    void HandleIncomingCan(IpcInMsg msg)
    {
        if (_taskCancels.TryGetValue(msg.Header.Id, out var cts))
            cts.Cancel();
    }

    /// <summary>处理入站 Err：令对应等待以 Exception 失败。</summary>
    void HandleIncomingErr(IpcInMsg msg)
    {
        if (_waitingRes.TryGetValue(msg.Header.Id, out var task))
        {
            string? s = msg.DeserializePayloadString();
            task.SetException(new Exception(s));
        }
    }

    /// <summary>处理入站 UnK：令对应等待以 KeyNotFoundException 失败。</summary>
    void HandleIncomingUnK(IpcInMsg msg)
    {
        if (_waitingRes.TryGetValue(msg.Header.Id, out var task))
        {
            string? s = msg.DeserializePayloadString();
            task.SetException(new KeyNotFoundException(s));
        }
    }

    /// <summary>
    /// 处理入站 Sys：带应答命令（list / subscribe / unsubscribe）的回复与 Req 共用
    /// <see cref="ReplyResAsync"/> / <see cref="ReplyErrAsync"/> / <see cref="ReplyUnKAsync"/>；
    /// 无应答开闭推送走 <see cref="HandleRemoteCatalogPushAsync"/>（远端事件与 NotifyOpened 自动补订）。
    /// </summary>
    async Task HandleIncomingSysAsync(IpcInMsg msg, CancellationToken ct)
    {
        var method = msg.Method;
        if (string.IsNullOrEmpty(method))
        {
            await ReplyUnKAsync(msg.Header.Id);
            return;
        }

        // 无应答开闭推送：解析载荷、（NotifyOpened 命中期望集时）自动补订，再引发远端事件
        if (method == RpcSystemMethods.RequestOpened
            || method == RpcSystemMethods.RequestClosed
            || method == RpcSystemMethods.NotifyOpened
            || method == RpcSystemMethods.NotifyClosed)
        {
            await HandleRemoteCatalogPushAsync(msg, method!, ct).ConfigureAwait(false);
            return;
        }

        // 与 Req 相同：成功 → Res，异常 → Err，未知方法 → UnK
        try
        {
            switch (method)
            {
                case RpcSystemMethods.ListRequests:
                {
                    var infos = Router.SnapshotRequestMethodInfos();
                    await ReplyResAsync(msg.Header.Id, infos, typeof(RpcRequestMethodInfo[]));
                    break;
                }
                case RpcSystemMethods.ListNotifies:
                {
                    var infos = Router.SnapshotNotifyMethodInfos();
                    await ReplyResAsync(msg.Header.Id, infos, typeof(RpcNotifyMethodInfo[]));
                    break;
                }
                case RpcSystemMethods.Subscribe:
                {
                    var name = msg.DeserializePayload(typeof(string), serilizerOptions) as string;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await ReplyErrAsync(msg.Header.Id, "subscribe 载荷必须为非空方法名");
                        break;
                    }

                    string methodName = name!;
                    var handler = Router.GetNotifyHandler(methodName);
                    if (handler is null)
                    {
                        await ReplyUnKAsync(msg.Header.Id);
                        break;
                    }

                    _subscriptions.TryAdd(handler.MethodName, 0);

                    var info = HandlerRouter.ToNotifyMethodInfo(handler);
                    await ReplyResAsync(msg.Header.Id, info, typeof(RpcNotifyMethodInfo));
                    break;
                }
                case RpcSystemMethods.Unsubscribe:
                {
                    var name = msg.DeserializePayload(typeof(string), serilizerOptions) as string;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        await ReplyErrAsync(msg.Header.Id, "unsubscribe 载荷必须为非空方法名");
                        break;
                    }

                    string methodName = name!;
                    _subscriptions.TryRemove(methodName, out _);

                    await ReplyResAsync(msg.Header.Id, Unit.Value, typeof(Unit));
                    break;
                }
                default:
                    await ReplyUnKAsync(msg.Header.Id);
                    break;
            }
        }
        catch (Exception ex)
        {
            await ReplyErrAsync(msg.Header.Id, ex.Message);
        }
    }

    /// <summary>
    /// 处理对端目录开闭推送（无应答 Sys）。
    /// <para>
    /// Id 仍在等待表时不当作推送；载荷无效经 <see cref="OnError"/> 报告。
    /// <see cref="RpcSystemMethods.NotifyOpened"/> 且方法在期望集中时，先
    /// <see cref="SubscribeAsync"/>（无论成败），再引发 <see cref="NotifyOpened"/>。
    /// <see cref="RpcSystemMethods.NotifyClosed"/> 不移出期望集。
    /// </para>
    /// </summary>
    async Task HandleRemoteCatalogPushAsync(IpcInMsg msg, string sysMethod, CancellationToken ct)
    {
        // 带应答 Sys 的 Id 若落在等待表中，则不当作开闭推送，避免混淆
        if (_waitingRes.ContainsKey(msg.Header.Id))
            return;

        try
        {
            switch (sysMethod)
            {
                case RpcSystemMethods.RequestOpened:
                case RpcSystemMethods.RequestClosed:
                {
                    var info = msg.DeserializePayload(typeof(RpcRequestMethodInfo), serilizerOptions) as RpcRequestMethodInfo;
                    if (info is null || string.IsNullOrWhiteSpace(info.MethodName))
                    {
                        OnError?.Invoke($"远端开闭推送载荷无效: {sysMethod}", null);
                        return;
                    }

                    var args = new RpcRequestMethodEventArgs(info);
                    if (sysMethod == RpcSystemMethods.RequestOpened)
                        RequestOpened?.Invoke(this, args);
                    else
                        RequestClosed?.Invoke(this, args);
                    break;
                }
                case RpcSystemMethods.NotifyOpened:
                case RpcSystemMethods.NotifyClosed:
                {
                    var info = msg.DeserializePayload(typeof(RpcNotifyMethodInfo), serilizerOptions) as RpcNotifyMethodInfo;
                    if (info is null || string.IsNullOrWhiteSpace(info.MethodName))
                    {
                        OnError?.Invoke($"远端开闭推送载荷无效: {sysMethod}", null);
                        return;
                    }

                    // 期望集命中：先自动补订，再引发 NotifyOpened（订阅失败仍会引发事件）
                    if (sysMethod == RpcSystemMethods.NotifyOpened
                        && IsDesiredNotify(info.MethodName))
                    {
                        await SubscribeAsync(info.MethodName, ct).ConfigureAwait(false);
                    }

                    var args = new RpcNotifyMethodEventArgs(info);
                    if (sysMethod == RpcSystemMethods.NotifyOpened)
                        NotifyOpened?.Invoke(this, args);
                    else
                        NotifyClosed?.Invoke(this, args); // Closed：保留期望集，不 TryRemove
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"处理远端开闭推送异常: {sysMethod}", ex);
        }
    }

    /// <summary>成功应答：与普通 Req 路径相同（Payload + Res Header，同 Id）。</summary>
    async Task ReplyResAsync(long id, object? payload, Type responseType)
    {
        var rmsg = new IpcOutMsg();
        rmsg.WritePayload(payload, responseType, serilizerOptions);
        rmsg.WriteHeader(id, _option.V, MessageType.Res);
        await _sendChannel.Writer.WriteAsync(rmsg);
    }

    /// <summary>错误应答：与普通 Req 路径相同（string 载荷 + Err Header）。</summary>
    async Task ReplyErrAsync(long id, string message)
    {
        var rmsg = new IpcOutMsg();
        rmsg.WritePayload(message, typeof(string));
        rmsg.WriteHeader(id, _option.V, MessageType.Err);
        await _sendChannel.Writer.WriteAsync(rmsg);
    }

    /// <summary>未知方法应答：与普通 Req 路径相同（仅 UnK Header）。</summary>
    async Task ReplyUnKAsync(long id)
    {
        var rmsg = new IpcOutMsg();
        rmsg.WriteHeader(id, _option.V, MessageType.UnK);
        await _sendChannel.Writer.WriteAsync(rmsg);
    }

    bool isDispose;
    public void Dispose()
    {
        if (isDispose) return;
        isDispose = true;
        _cts?.Cancel();
        PipeStream.Close();

        _recieveChannel.Writer.Complete();
        _sendChannel.Writer.Complete();
        DisConnected?.Invoke(this);
    }
}
/// <summary>
/// 空请求占位
/// </summary>
public readonly struct Unit
{
    public static Unit Value { get; } = new Unit();
    public static Task<Unit> Task { get; } = Task<Unit>.FromResult(Unit.Value);
}
