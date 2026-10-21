using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.Test;

/// <summary>
/// RPC 服务发现、单条订阅、Start 自动订阅、定向推送与 NotifyOpened 自动补订用例。
/// </summary>
public class ServiceDiscoveryTests
{
    /// <summary>生成互不冲突的管道名，避免并行测试互相干扰。</summary>
    static string NewPipeName() => "nprpc-discovery-" + Guid.NewGuid().ToString("N");

    /// <summary>等待条件成立或超时失败。</summary>
    static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failMessage)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > timeout)
                throw new TimeoutException(failMessage);
            await Task.Delay(20);
        }
    }

    /// <summary>等待 TaskCompletionSource 完成（兼容 net472，无 WaitAsync）。</summary>
    static async Task<T> WaitForAsync<T>(TaskCompletionSource<T> tcs, TimeSpan timeout, string failMessage)
    {
        var delay = Task.Delay(timeout);
        var completed = await Task.WhenAny(tcs.Task, delay);
        if (completed != tcs.Task)
            throw new TimeoutException(failMessage);
        return await tcs.Task;
    }

    /// <summary>
    /// ListRemote*：分别查询对端 Request / Notify 列表（不混在一个列表）。
    /// </summary>
    [Fact]
    public async Task ListRemote_ReturnsSeparateRequestAndNotifyCatalogs()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        serverRouter.AddRequestHandler<int, int>("billing.pay", (ctx, ct) => Task.FromResult(ctx.Message + 1));
        serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();

        try
        {
            var requests = await peer.ListRemoteRequestMethodsAsync();
            var notifies = await peer.ListRemoteNotifyMethodsAsync();

            Assert.Contains(requests, r => r.MethodName == "billing.pay");
            Assert.DoesNotContain(requests, r => r.MethodName == "order.updated");
            Assert.Contains(notifies, n => n.MethodName == "order.updated");
            Assert.DoesNotContain(notifies, n => n.MethodName == "billing.pay");
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// 单条 Subscribe / Unsubscribe：成功订阅后服务端 IsSubscribed 为 true；取消后为 false。
    /// </summary>
    [Fact]
    public async Task SubscribeAndUnsubscribe_SingleMethod_UpdatesServerSubscriptionTable()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var service = new RpcService(pipe, serverRouter);
        NamedPipePeer? serverPeer = null;
        service.Connected += p => serverPeer = p;
        service.Start();

        // 客户端不预注册 Notify，避免 Start 自动订阅干扰本用例
        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();

        try
        {
            await WaitUntilAsync(() => serverPeer != null, TimeSpan.FromSeconds(5), "服务端未收到连接");

            var info = await peer.SubscribeAsync("order.updated");
            Assert.NotNull(info);
            Assert.Equal("order.updated", info!.MethodName);
            Assert.True(serverPeer!.IsSubscribed("order.updated"));

            await peer.UnsubscribeAsync("order.updated");
            Assert.False(serverPeer.IsSubscribed("order.updated"));
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// Start（ConnectAsync 内部调用）后自动对本端已注册 Notify Handler 发起订阅。
    /// </summary>
    [Fact]
    public async Task Start_AutoSubscribesLocalNotifyHandlers()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var service = new RpcService(pipe, serverRouter);
        NamedPipePeer? serverPeer = null;
        service.Connected += p => serverPeer = p;
        service.Start();

        // 客户端先注册本端 Notify，再 Connect → Start 自动 SubscribeLocalNotifyHandlersAsync
        var clientRouter = new HandlerRouter();
        clientRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();

        try
        {
            await WaitUntilAsync(
                () => serverPeer != null && serverPeer.IsSubscribed("order.updated"),
                TimeSpan.FromSeconds(5),
                "Start 后未能自动订阅本端 Notify");
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// NotifySubscribers 仅推已订阅者；Notify 仍全量广播。
    /// </summary>
    [Fact]
    public async Task NotifySubscribers_OnlyDeliversToSubscribedPeers_NotifyStillBroadcasts()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        // 服务端需注册同名 Notify，供 Subscribe 校验存在性
        serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var service = new RpcService(pipe, serverRouter, new RpcServiceOption { MaxConnect = 2 });
        service.Start();

        var receivedA = new List<string>();
        var receivedB = new List<string>();
        var lockObj = new object();

        // 连接前不注册 Notify，避免 Start 自动把两端都订上
        var routerA = new HandlerRouter();
        var routerB = new HandlerRouter();

        using var clientA = new RpcClient(pipe, routerA);
        using var clientB = new RpcClient(pipe, routerB);
        var peerA = await clientA.ConnectAsync();
        var peerB = await clientB.ConnectAsync();

        try
        {
            await WaitUntilAsync(() => service.Peers.Count == 2, TimeSpan.FromSeconds(5), "未等到两个连接");

            // 连接后再挂接收 Handler；仅 A 显式订阅
            routerA.AddNotifyHandler<string>("order.updated", msg =>
            {
                lock (lockObj) receivedA.Add(msg);
            });
            routerB.AddNotifyHandler<string>("order.updated", msg =>
            {
                lock (lockObj) receivedB.Add(msg);
            });

            var sub = await peerA.SubscribeAsync("order.updated");
            Assert.NotNull(sub);

            await service.Peers.NotifySubscribers("order.updated", "to-subscribers");
            await WaitUntilAsync(
                () => { lock (lockObj) return receivedA.Count >= 1; },
                TimeSpan.FromSeconds(5),
                "订阅者未收到定向推送");

            lock (lockObj)
            {
                Assert.Equal(new[] { "to-subscribers" }, receivedA);
                Assert.Empty(receivedB);
            }

            // 全量广播：A、B 都应收到
            await service.Peers.Notify("order.updated", "to-all");
            await WaitUntilAsync(
                () =>
                {
                    lock (lockObj)
                        return receivedA.Count >= 2 && receivedB.Count >= 1;
                },
                TimeSpan.FromSeconds(5),
                "全量广播未同时送达两端");

            lock (lockObj)
            {
                Assert.Contains("to-all", receivedA);
                Assert.Contains("to-all", receivedB);
            }
        }
        finally
        {
            peerA.Close();
            peerB.Close();
            service.Close();
        }
    }

    /// <summary>
    /// Start 自动订阅时对端缺失 → 未订上但期望集保留；稍后 NotifyOpened → 自动补订后再引发事件。
    /// </summary>
    [Fact]
    public async Task Start_MissingThenNotifyOpened_AutoResubscribesBeforeRaisingEvent()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        // 故意不先注册 order.updated

        using var service = new RpcService(pipe, serverRouter);
        NamedPipePeer? serverPeer = null;
        service.Connected += p => serverPeer = p;
        service.Start();

        var clientRouter = new HandlerRouter();
        clientRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();

        // Opened 在稍后 Add 时才推送，可在 Connect 后挂接
        var openedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotifyOpened += (_, e) => openedTcs.TrySetResult(e.Method.MethodName);

        try
        {
            await WaitUntilAsync(() => serverPeer != null, TimeSpan.FromSeconds(5), "服务端未收到连接");

            // 给 Start 后台自动订阅一点时间完成「缺失」路径
            await Task.Delay(300);
            Assert.False(serverPeer!.IsSubscribed("order.updated"));

            // 服务端稍后 Open → 推 Sys → 客户端自动补订 → 再引发 NotifyOpened
            serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

            var openedMethod = await WaitForAsync(openedTcs, TimeSpan.FromSeconds(5), "未收到 NotifyOpened");
            Assert.Equal("order.updated", openedMethod);

            await WaitUntilAsync(
                () => serverPeer.IsSubscribed("order.updated"),
                TimeSpan.FromSeconds(5),
                "NotifyOpened 后未能自动补订");
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// 手动 Subscribe 缺失方法报告 NotifyNotFound，且可在 Opened 后补订。
    /// </summary>
    [Fact]
    public async Task ManualSubscribe_Missing_ReportsNotFound_ThenOpened_AutoResubscribes()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();

        using var service = new RpcService(pipe, serverRouter);
        NamedPipePeer? serverPeer = null;
        service.Connected += p => serverPeer = p;
        service.Start();

        // 客户端不预注册 Notify，避免 Start 自动订阅干扰
        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();

        var notFoundTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var openedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotifyNotFound += (_, e) => notFoundTcs.TrySetResult(e.MethodName);
        peer.NotifyOpened += (_, e) => openedTcs.TrySetResult(e.Method.MethodName);

        try
        {
            await WaitUntilAsync(() => serverPeer != null, TimeSpan.FromSeconds(5), "服务端未收到连接");

            var result = await peer.SubscribeAsync("order.updated");
            Assert.Null(result);

            var missing = await WaitForAsync(notFoundTcs, TimeSpan.FromSeconds(5), "未收到 NotifyNotFound");
            Assert.Equal("order.updated", missing);
            Assert.False(serverPeer!.IsSubscribed("order.updated"));

            serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

            var opened = await WaitForAsync(openedTcs, TimeSpan.FromSeconds(5), "未收到 NotifyOpened");
            Assert.Equal("order.updated", opened);
            await WaitUntilAsync(
                () => serverPeer.IsSubscribed("order.updated"),
                TimeSpan.FromSeconds(5),
                "自动补订失败");
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// 服务端 Add/Remove Request → 客户端收到 RequestOpened / RequestClosed（无自动连接）。
    /// </summary>
    [Fact]
    public async Task RequestOpenedAndClosed_RaiseRemoteEvents_WithoutAutoConnect()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();

        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();

        var openedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.RequestOpened += (_, e) => openedTcs.TrySetResult(e.Method.MethodName);
        peer.RequestClosed += (_, e) => closedTcs.TrySetResult(e.Method.MethodName);

        try
        {
            serverRouter.AddRequestHandler<int, int>("billing.pay", (ctx, ct) => Task.FromResult(ctx.Message));

            var opened = await WaitForAsync(openedTcs, TimeSpan.FromSeconds(5), "未收到 RequestOpened");
            Assert.Equal("billing.pay", opened);

            Assert.True(serverRouter.RemoveRequestHandler("billing.pay"));
            var closed = await WaitForAsync(closedTcs, TimeSpan.FromSeconds(5), "未收到 RequestClosed");
            Assert.Equal("billing.pay", closed);
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// NotifyClosed：服务端清订阅；客户端保留期望集，再次 Opened 时自动补订。
    /// </summary>
    [Fact]
    public async Task NotifyClosed_ClearsServerSubscription_KeepsDesired_ThenReopenResubscribes()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var service = new RpcService(pipe, serverRouter);
        NamedPipePeer? serverPeer = null;
        service.Connected += p => serverPeer = p;
        service.Start();

        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();

        var closedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var openedTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        peer.NotifyClosed += (_, e) => closedTcs.TrySetResult(e.Method.MethodName);
        peer.NotifyOpened += (_, e) => openedTcs.TrySetResult(e.Method.MethodName);

        try
        {
            await WaitUntilAsync(() => serverPeer != null, TimeSpan.FromSeconds(5), "服务端未收到连接");
            Assert.NotNull(await peer.SubscribeAsync("order.updated"));
            Assert.True(serverPeer!.IsSubscribed("order.updated"));

            Assert.True(serverRouter.RemoveNotifyHandler("order.updated"));
            var closed = await WaitForAsync(closedTcs, TimeSpan.FromSeconds(5), "未收到 NotifyClosed");
            Assert.Equal("order.updated", closed);
            Assert.False(serverPeer.IsSubscribed("order.updated"));

            // 再次 Open：期望集仍在 → 自动补订
            serverRouter.AddNotifyHandler<string>("order.updated", _ => { });
            var opened = await WaitForAsync(openedTcs, TimeSpan.FromSeconds(5), "未收到再次 NotifyOpened");
            Assert.Equal("order.updated", opened);
            await WaitUntilAsync(
                () => serverPeer.IsSubscribed("order.updated"),
                TimeSpan.FromSeconds(5),
                "Closed 后再 Open 未能自动补订");
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// Unsubscribe 移出期望集后，对端再 Opened 不再自动补订。
    /// </summary>
    [Fact]
    public async Task Unsubscribe_RemovesDesired_NoAutoResubscribeOnOpened()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var service = new RpcService(pipe, serverRouter);
        NamedPipePeer? serverPeer = null;
        service.Connected += p => serverPeer = p;
        service.Start();

        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();

        var openedCount = 0;
        peer.NotifyOpened += (_, _) => Interlocked.Increment(ref openedCount);

        try
        {
            await WaitUntilAsync(() => serverPeer != null, TimeSpan.FromSeconds(5), "服务端未收到连接");
            Assert.NotNull(await peer.SubscribeAsync("order.updated"));
            await peer.UnsubscribeAsync("order.updated");
            Assert.False(serverPeer!.IsSubscribed("order.updated"));

            // 关闭再开启：客户端已 Unsubscribe，不应自动补订
            Assert.True(serverRouter.RemoveNotifyHandler("order.updated"));
            await Task.Delay(200);
            serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

            await WaitUntilAsync(() => Volatile.Read(ref openedCount) >= 1, TimeSpan.FromSeconds(5), "未收到 NotifyOpened");
            await Task.Delay(300);
            Assert.False(serverPeer.IsSubscribed("order.updated"));
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// Start 之后新增本端 Notify Handler，可手动 SubscribeLocalNotifyHandlersAsync 补订。
    /// </summary>
    [Fact]
    public async Task SubscribeLocalNotifyHandlersAsync_AfterStart_SubscribesNewlyAddedLocalHandlers()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var service = new RpcService(pipe, serverRouter);
        NamedPipePeer? serverPeer = null;
        service.Connected += p => serverPeer = p;
        service.Start();

        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();

        try
        {
            await WaitUntilAsync(() => serverPeer != null, TimeSpan.FromSeconds(5), "服务端未收到连接");
            await Task.Delay(200);
            Assert.False(serverPeer!.IsSubscribed("order.updated"));

            clientRouter.AddNotifyHandler<string>("order.updated", _ => { });
            await peer.SubscribeLocalNotifyHandlersAsync();

            Assert.True(serverPeer.IsSubscribed("order.updated"));
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>
    /// 无订阅者时 NotifySubscribers 立即完成，且不会误投递给未订阅连接。
    /// </summary>
    [Fact]
    public async Task NotifySubscribers_WithNoSubscribers_CompletesWithoutDelivery()
    {
        var pipe = NewPipeName();
        var serverRouter = new HandlerRouter();
        serverRouter.AddNotifyHandler<string>("order.updated", _ => { });

        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        var received = 0;
        // 连接前注册 Handler 会触发 Start 自动订阅；此处先空 Router 再挂 Handler，保证未订阅
        var clientRouter = new HandlerRouter();
        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync();
        clientRouter.AddNotifyHandler<string>("order.updated", _ => Interlocked.Increment(ref received));

        try
        {
            await WaitUntilAsync(() => service.Peers.Count == 1, TimeSpan.FromSeconds(5), "未连接");
            await service.Peers.NotifySubscribers("order.updated", "nobody");
            await Task.Delay(300);
            Assert.Equal(0, Volatile.Read(ref received));
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }
}
