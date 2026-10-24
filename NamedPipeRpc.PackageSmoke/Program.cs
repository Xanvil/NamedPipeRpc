using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.PackageSmoke;

/// <summary>
/// NuGet 包冒烟入口：仅 PackageReference 引用 NamedPipeRpc。
/// 验证生成器启用、成功路径，以及生成扩展 / 管道 RPC 的典型错误处理。
/// </summary>
internal static class Program
{
    /// <summary>
    /// 依次跑成功路径与错误场景；任一断言失败则非 0 退出。
    /// </summary>
    private static async Task<int> Main()
    {
        var failures = new List<string>();

        await RunCaseAsync(failures, "happy-path", RunHappyPathAsync).ConfigureAwait(false);
        await RunCaseAsync(failures, "notify-path", RunNotifyPathAsync).ConfigureAwait(false);
        RunCase(failures, "AddNotifyProxy-unknown-type", TestAddNotifyProxy_UnknownType_Throws);
        RunCase(failures, "AddRequestHandler-unknown-type", TestAddRequestHandler_UnknownType_Throws);
        await RunCaseAsync(failures, "GetRequestProxy-unknown-type", TestGetRequestProxy_UnknownType_ThrowsAsync)
            .ConfigureAwait(false);
        await RunCaseAsync(failures, "Request-method-missing", RunRequest_MethodMissing_ThrowsKeyNotFoundAsync)
            .ConfigureAwait(false);
        await RunCaseAsync(failures, "Request-handler-throws", RunRequest_HandlerThrows_PropagatesAsync)
            .ConfigureAwait(false);
        await RunCaseAsync(failures, "Subscribe-empty-method", RunSubscribe_EmptyMethod_ThrowsAsync)
            .ConfigureAwait(false);
        await RunCaseAsync(failures, "Request-method-too-long", RunRequest_MethodTooLong_ThrowsAsync)
            .ConfigureAwait(false);

        if (failures.Count > 0)
        {
            Console.Error.WriteLine("PackageSmoke 失败用例：");
            foreach (var f in failures)
                Console.Error.WriteLine("  - " + f);
            return 1;
        }

        Console.WriteLine("NamedPipeRpc NuGet smoke OK: happy path + error handling via packaged generator.");
        return 0;
    }

    /// <summary>同步用例包装：捕获异常记入 failures。</summary>
    static void RunCase(List<string> failures, string name, Action action)
    {
        try
        {
            action();
            Console.WriteLine($"  OK {name}");
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
    }

    /// <summary>异步用例包装：捕获异常记入 failures。</summary>
    static async Task RunCaseAsync(List<string> failures, string name, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            Console.WriteLine($"  OK {name}");
        }
        catch (Exception ex)
        {
            failures.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine($"  FAIL {name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 成功路径：AddRequestHandler，再从客户端连接上取 GetRequestProxy。
    /// </summary>
    static async Task RunHappyPathAsync()
    {
        await using var host = await SmokeHost.StartAsync(new SmokeService()).ConfigureAwait(false);
        ISmoke proxy = host.Peer.GetRequestProxy<ISmoke>();

        int sum = await proxy.AddAsync(40, 2).ConfigureAwait(false);
        Expect(sum == 42, $"AddAsync 期望 42，实际 {sum}");

        int echo = await proxy.EchoAsync(7).ConfigureAwait(false);
        Expect(echo == 7, $"EchoAsync 期望 7，实际 {echo}");

        string desc = await proxy.DescribeAsync(3, "x", 1.5).ConfigureAwait(false);
        Expect(desc == "3:x:1.5", $"DescribeAsync 期望 3:x:1.5，实际 {desc}");
    }

    /// <summary>
    /// Notify：服务端 AddNotifyHandler 可接收；客户端 AddNotifyProxy 连接后自动订阅；GetNotifyProxy 发送。
    /// </summary>
    static async Task RunNotifyPathAsync()
    {
        string pipe = "nprpc-nuget-smoke-notify-" + Guid.NewGuid().ToString("N");
        var serverRouter = new HandlerRouter();
        serverRouter.AddNotifyHandler<ISmokeNotify>(_ => new SmokeNotifyStub());

        using var service = new RpcService(pipe, serverRouter);
        service.Start();

        string? got = null;
        var clientRouter = new HandlerRouter();
        clientRouter.AddNotifyHandler<ISmokeNotify>(_ => new SmokeNotifySink(msg => got = msg));
        clientRouter.AddNotifyProxy<ISmokeNotify>();

        using var client = new RpcClient(pipe, clientRouter);
        var peer = await client.ConnectAsync().ConfigureAwait(false);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline
                   && (service.Peers.Count == 0
                       || !service.Peers.ToArray()[0].IsSubscribed("smoke.Ping")))
            {
                await Task.Delay(20).ConfigureAwait(false);
            }

            Expect(service.Peers.Count == 1 && service.Peers.ToArray()[0].IsSubscribed("smoke.Ping"),
                "客户端未能自动订阅 smoke.Ping");

            var outgoing = service.Peers.ToArray()[0].GetNotifyProxy<ISmokeNotify>();
            await outgoing.PingAsync("ping-via-proxy").ConfigureAwait(false);

            deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && got is null)
                await Task.Delay(20).ConfigureAwait(false);

            Expect(got == "ping-via-proxy", $"期望 ping-via-proxy，实际 {got}");
        }
        finally
        {
            peer.Close();
            service.Close();
        }
    }

    /// <summary>非通知契约不能登记自动订阅。</summary>
    static void TestAddNotifyProxy_UnknownType_Throws()
    {
        var router = new HandlerRouter();
        ExpectThrows<InvalidOperationException>(() => router.AddNotifyProxy<INotAContract>());
    }

    /// <summary>非契约类型不能作为 Handler 注册。</summary>
    static void TestAddRequestHandler_UnknownType_Throws()
    {
        var router = new HandlerRouter();
        ExpectThrows<InvalidOperationException>(() => router.AddRequestHandler<object>(_ => new object()));
    }

    /// <summary>非契约类型不能从连接上取出请求代理。</summary>
    static async Task TestGetRequestProxy_UnknownType_ThrowsAsync()
    {
        await using var host = await SmokeHost.StartAsync(new SmokeService()).ConfigureAwait(false);
        ExpectThrows<InvalidOperationException>(() => host.Peer.GetRequestProxy<INotAContract>());
    }

    /// <summary>
    /// 请求不存在的方法 → KeyNotFoundException，并触发 RequestNotFound。
    /// </summary>
    static async Task RunRequest_MethodMissing_ThrowsKeyNotFoundAsync()
    {
        await using var host = await SmokeHost.StartAsync(new SmokeService()).ConfigureAwait(false);

        string? notFoundMethod = null;
        host.Peer.RequestNotFound += (_, e) => notFoundMethod = e.MethodName;

        try
        {
            await host.Peer.Request<int, int>("smoke.NoSuchMethod", 1).ConfigureAwait(false);
            throw new InvalidOperationException("期望抛出 KeyNotFoundException");
        }
        catch (KeyNotFoundException)
        {
            // 预期
        }

        Expect(notFoundMethod == "smoke.NoSuchMethod",
            $"RequestNotFound 期望 smoke.NoSuchMethod，实际 {notFoundMethod}");
    }

    /// <summary>
    /// 服务端 Handler 抛错 → 客户端收到 Exception（Err 载荷为消息文本）。
    /// </summary>
    static async Task RunRequest_HandlerThrows_PropagatesAsync()
    {
        await using var host = await SmokeHost.StartAsync(new SmokeService()).ConfigureAwait(false);
        ISmoke proxy = host.Peer.GetRequestProxy<ISmoke>();

        try
        {
            await proxy.BoomAsync().ConfigureAwait(false);
            throw new InvalidOperationException("期望 BoomAsync 抛出 Exception");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Expect(ex.Message.Contains("boom-from-server", StringComparison.Ordinal),
                $"Err 消息应含 boom-from-server，实际：{ex.Message}");
        }
    }

    /// <summary>Subscribe 空 / 空白 / null method → ArgumentException / ArgumentNullException。</summary>
    static async Task RunSubscribe_EmptyMethod_ThrowsAsync()
    {
        await using var host = await SmokeHost.StartAsync(new SmokeService()).ConfigureAwait(false);
        await ExpectThrowsAsync<ArgumentException>(() => host.Peer.SubscribeAsync("   ")).ConfigureAwait(false);
        await ExpectThrowsAsync<ArgumentNullException>(() => host.Peer.SubscribeAsync(null!)).ConfigureAwait(false);
    }

    /// <summary>
    /// Request method 过长 → 写出阶段失败（当前实现为 Encoding 缓冲区 ArgumentException；
    /// 若缓冲足够大则可能为 ArgumentOutOfRangeException）。
    /// </summary>
    static async Task RunRequest_MethodTooLong_ThrowsAsync()
    {
        await using var host = await SmokeHost.StartAsync(new SmokeService()).ConfigureAwait(false);
        string tooLong = new string('a', 300);
        try
        {
            await host.Peer.Request<int, int>(tooLong, 1).ConfigureAwait(false);
            throw new InvalidOperationException("期望超长 method 抛出异常");
        }
        catch (ArgumentException)
        {
            // 预期：GetBytes 缓冲不足或 ml>255 的 ArgumentOutOfRangeException（后者亦为 ArgumentException 子类）
        }
    }

    static void Expect(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    static void ExpectThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"期望 {typeof(TException).Name}，实际 {ex.GetType().Name}: {ex.Message}");
        }

        throw new InvalidOperationException($"期望 {typeof(TException).Name}，但未抛出");
    }

    static async Task ExpectThrowsAsync<TException>(Func<Task> action) where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException)
        {
            return;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"期望 {typeof(TException).Name}，实际 {ex.GetType().Name}: {ex.Message}");
        }

        throw new InvalidOperationException($"期望 {typeof(TException).Name}，但未抛出");
    }
}

/// <summary>
/// 本进程内 RpcService + RpcClient，供冒烟用例复用。
/// </summary>
internal sealed class SmokeHost : IAsyncDisposable
{
    public required RpcService Service { get; init; }
    public required RpcClient Client { get; init; }
    public required NamedPipePeer Peer { get; init; }

    /// <summary>启动服务端、注册实现并连接客户端。</summary>
    public static async Task<SmokeHost> StartAsync(ISmoke impl)
    {
        string pipe = "nprpc-nuget-smoke-" + Guid.NewGuid().ToString("N");
        var serverRouter = new HandlerRouter();
        serverRouter.AddRequestHandler<ISmoke>(_ => impl);

        var service = new RpcService(pipe, serverRouter);
        service.Start();

        var client = new RpcClient(pipe);
        NamedPipePeer peer = await client.ConnectAsync().ConfigureAwait(false);
        return new SmokeHost
        {
            Service = service,
            Client = client,
            Peer = peer,
        };
    }

    public async ValueTask DisposeAsync()
    {
        Peer.Close();
        Client.Dispose();
        Service.Close();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>冒烟 Notify 契约。</summary>
[RpcNotify("smoke")]
public interface ISmokeNotify
{
    /// <summary>RPC 名：smoke.Ping。</summary>
    Task PingAsync(string msg, CancellationToken ct = default);
}

/// <summary>服务端可接收的通知实现。不调用 AddNotifyProxy，因此不会自动订阅。</summary>
sealed class SmokeNotifyStub : ISmokeNotify
{
    public Task PingAsync(string msg, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>客户端 Notify 接收。</summary>
sealed class SmokeNotifySink : ISmokeNotify
{
    readonly Action<string> _onMsg;

    public SmokeNotifySink(Action<string> onMsg) => _onMsg = onMsg;

    public Task PingAsync(string msg, CancellationToken ct = default)
    {
        _onMsg(msg);
        return Task.CompletedTask;
    }
}

/// <summary>冒烟契约：成功路径 + Handler 抛错方法。</summary>
[RpcRequest("smoke")]
public interface ISmoke
{
    /// <summary>默认 RPC 名：smoke.Add。</summary>
    Task<int> AddAsync(int a, int b, CancellationToken ct = default);

    /// <summary>一个业务参数，载荷就是这个 <c>int</c>，不看参数名。</summary>
    Task<int> EchoAsync(int a, CancellationToken ct = default);

    /// <summary>多个业务参数，生成字段包，按参数名匹配。缺少的字段使用类型默认值。</summary>
    Task<string> DescribeAsync(int id, string tag, double weight, CancellationToken ct = default);

    /// <summary>服务端故意抛错，用于验证 Err 传播。</summary>
    Task BoomAsync(CancellationToken ct = default);
}

/// <summary>无契约特性，用于未知类型错误。</summary>
public interface INotAContract
{
}

/// <summary>冒烟服务实现。</summary>
public sealed class SmokeService : ISmoke
{
    /// <inheritdoc />
    public Task<int> AddAsync(int a, int b, CancellationToken ct = default) =>
        Task.FromResult(a + b);

    /// <inheritdoc />
    public Task<int> EchoAsync(int a, CancellationToken ct = default) =>
        Task.FromResult(a);

    /// <inheritdoc />
    public Task<string> DescribeAsync(int id, string tag, double weight, CancellationToken ct = default) =>
        Task.FromResult($"{id}:{tag}:{weight}");

    /// <inheritdoc />
    public Task BoomAsync(CancellationToken ct = default) =>
        Task.FromException(new InvalidOperationException("boom-from-server"));
}
