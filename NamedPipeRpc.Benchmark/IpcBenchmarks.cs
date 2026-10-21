using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.Test;


// 简单 Handler（用于基础性能测试）
public class TestHandler : IRequestHandler
{
    public string MethodName => "test.add";

    //public Task<int> Handle(int request, CancellationToken ct)
    //{
    //    return Task.FromResult(request + request);
    //}

    public Task<Unit> Handle(RpcContext<Unit> context, CancellationToken ct)
    {
        return Unit.Task;
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


// [MemoryDiagnoser] 会自动统计每次调用的内存分配字节数和 GC 回收次数
[MemoryDiagnoser]
// 确保测试按指定顺序执行
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class IpcBenchmarks
{
    private RpcService _rpcService;
    private RpcClient _client;
    private NamedPipePeer _peer;
    private ComplexRequest _complexRequest;

    // ⚡ 全局初始化：在每次基准测试开始前，只执行一次（避免把连接建立的耗时算进通信里）
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        HandlerRouter router = new HandlerRouter();
        router.AddRequestHandler(new TestHandler());
        router.AddRequestHandler(new ComplexHandler());

        _rpcService = new RpcService("bench_pipe", router);
        _rpcService.Start();

        _client = new RpcClient("bench_pipe", router);
        _peer = await _client.ConnectAsync();

        // 构造复杂请求
        _complexRequest = new ComplexRequest
        {
            Id = 12345,
            Name = "Benchmark AllPayload",
            Timestamp = DateTime.UtcNow,
            Tags = new List<string> { "ipc", "benchmark", "dotnet" },
            Details = new NestedObject
            {
                Score = 99.9,
                Metadata = new Dictionary<string, int> { { "cpu", 8 }, { "memory", 16 } }
            }
        };

        // ⚡ 预热：BenchmarkDotNet 内部也会预热，但显式预热一次能确保 ArrayPool 和 JIT 完全就绪
        await _peer.Request("test.add");
        await _peer.Request<ComplexRequest, ComplexResponse>("test_complex.process", _complexRequest);
    }

    // ⚡ 全局清理：测试结束后关闭连接
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _rpcService?.Close();
    }

    /// <summary>
    /// 基准测试 1：简单 int 类型的极限吞吐
    /// </summary>
    [Benchmark(Baseline = true)] // 设为基线，方便对比其他方法的倍数
    public  async Task SimpleInt_Request()
    {
        await _peer.Request("test.add");
    }

    /// <summary>
    /// 基准测试 2：复杂对象 + 模拟 1ms 业务耗时
    /// </summary>
    [Benchmark]
    public async Task<ComplexResponse> ComplexObject_Request()
    {
        return await _peer.Request<ComplexRequest, ComplexResponse>("test_complex.process", _complexRequest);
    }
}