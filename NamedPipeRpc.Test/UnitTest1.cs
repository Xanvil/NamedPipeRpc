using System.Diagnostics;

using Xanvil.NamedPipeRpc;

using Xunit;
using Xunit.Abstractions;

namespace NamedPipeRpc.Test;

#region 1. 测试用的 Handler 定义

// 简单 Handler（用于基础性能测试）
public class TestHandler : IRequestHandler
{
    public string MethodName => "test.add";

    public Task<Unit> Handle(RpcContext<Unit> context, CancellationToken ct)
    {
        return Unit.Task;
    }
}

public class NotifyHandler : INotifyHandler<string>
{
    public string MethodName => "test.notify";

    public Task Handle(RpcContext<string> context, CancellationToken ct)
    {
        return Task.CompletedTask;
    }
}

// 复杂业务模型
public class ComplexRequest
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public List<string> Tags { get; set; } = new();
    public NestedObject Details { get; set; } = new();
}

public class NestedObject
{
    public double Score { get; set; }
    public Dictionary<string, int> Metadata { get; set; } = new();
}

public class ComplexResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int ProcessedCount { get; set; }
}

// 复杂 Handler（模拟真实业务耗时和 CPU 计算）
public class ComplexHandler : IRequestHandler<ComplexRequest, ComplexResponse>
{
    public string MethodName => "test_complex.process";

    public async Task<ComplexResponse> Handle(RpcContext<ComplexRequest> context, CancellationToken ct)
    {
        // 模拟 1ms 的 I/O 或业务等待
        await Task.Delay(1, ct);

        // 模拟 CPU 密集型计算
        int sum = 0;
        for (int i = 0; i < 1000; i++) sum += i;

        return new ComplexResponse
        {
            Success = true,
            Message = $"Processed {context.Message.Name} with {context.Message.Tags.Count} tags",
            ProcessedCount = context.Message.Id
        };
    }
}

#endregion

#region 2. 单元测试类

public class UnitTest1
{
    private readonly ITestOutputHelper _output;

    public UnitTest1(ITestOutputHelper output)
    {
        _output = output;
    }

    /// <summary>
    /// 基础性能测试：简单 int 类型的极限吞吐
    /// </summary>
    [Fact]
    public async Task Test_Performance_Benchmark()
    {
        // 1. 初始化服务与客户端
        HandlerRouter router = new HandlerRouter();
        router.AddRequestHandler(new TestHandler());
        router.AddNotifyHandler(new NotifyHandler());
        RpcService rpcService = new RpcService("test", router);
        rpcService.Start();

        RpcClient client = new RpcClient("test", router);
        var peer = await client.ConnectAsync();

        await Task.Delay(100);
        peer.Close();

        peer = await client.ConnectAsync();

        await Task.Delay(500);


        // 2. ⚡ 预热阶段（Warm-up）：触发 JIT、池化内存分配等
        for (int i = 0; i < 100; i++)
        {
            await peer.Request("test.add");
            await peer.Notify("test.notify","hello");
        }

        // 3. 正式测试阶段
        int c = 1000;
        var stopwatch = Stopwatch.StartNew(); // 紧贴着循环开始计时

        while (c-- > 0)
        {
            await peer.Request("test.add");

            await peer.Notify("test.notify", "hello");
            //System.Diagnostics.Debug.Assert(s == Unit.Empty, "IPC Response mismatch!");
        }

        stopwatch.Stop();

        // 4. 输出高精度结果
        double avgMicroseconds = (stopwatch.Elapsed.TotalMilliseconds / 1000.0) * 1000.0;
        _output.WriteLine($"=== Simple Int Performance ===");
        _output.WriteLine($"Total: {stopwatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"Avg Latency: {avgMicroseconds:F2} μs");
    }

    /// <summary>
    /// 复杂逻辑测试：大对象序列化 + 模拟业务耗时 + 并发吞吐
    /// </summary>
    [Fact]
    public async Task Test_Complex_Logic_And_Concurrency()
    {
        // 1. 初始化
        HandlerRouter router = new HandlerRouter();
        router.AddRequestHandler(new ComplexHandler());
        RpcService rpcService = new RpcService("test_complex", router);
        rpcService.Start();

        RpcClient client = new RpcClient("test_complex", router);
        var peer = await client.ConnectAsync();

        // 2. 构造复杂请求数据
        var request = new ComplexRequest
        {
            Id = 12345,
            Name = "Performance Test AllPayload",
            Timestamp = DateTime.UtcNow,
            Tags = new List<string> { "ipc", "benchmark", "dotnet", "named-pipe" },
            Details = new NestedObject
            {
                Score = 99.9,
                Metadata = new Dictionary<string, int> { { "cpu", 8 }, { "memory", 16 } }
            }
        };

        // 3. 预热
        for (int i = 0; i < 50; i++)
        {
            await peer.Request<ComplexRequest, ComplexResponse>("test_complex.process", request);
        }

        // 4. 串行测试：1000 次复杂请求
        int count = 1000;
        var stopwatch = Stopwatch.StartNew();

        while (count-- > 0)
        {
            var response = await peer.Request<ComplexRequest, ComplexResponse>("test_complex.process", request);
            System.Diagnostics.Debug.Assert(response.Success);
        }

        stopwatch.Stop();
        double avgUs = (stopwatch.Elapsed.TotalMilliseconds / 1000.0) * 1000.0;

        _output.WriteLine($"=== Complex Logic Serial Test ===");
        _output.WriteLine($"Total: {stopwatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"Avg Latency: {avgUs:F2} μs");

        // 5. 并发测试：同时发起 100 个复杂请求
        var concurrentStopwatch = Stopwatch.StartNew();
        var tasks = new Task<ComplexResponse>[100];
        for (int i = 0; i < 100; i++)
        {
            tasks[i] = peer.Request<ComplexRequest, ComplexResponse>("test_complex.process", request);
        }
        await Task.WhenAll(tasks);
        concurrentStopwatch.Stop();

        _output.WriteLine($"=== Concurrent Test (100 requests) ===");
        _output.WriteLine($"Total Wall Time: {concurrentStopwatch.ElapsedMilliseconds} ms");
        _output.WriteLine($"Avg Per Request: {(concurrentStopwatch.Elapsed.TotalMilliseconds / 100.0 * 1000.0):F2} μs");
    }
}

#endregion