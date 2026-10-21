using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.Benchmark;

/// <summary>
/// 复杂业务模型：用于测量 JSON 序列化往返开销（Handler 内无人为 Delay）。
/// </summary>
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

/// <summary>
/// 单连接延迟基准：空载荷 Request/Notify、复杂 JSON（无 Delay）。
/// 两端使用独立 HandlerRouter，贴近生产用法。
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class IpcLatencyBenchmarks
{
    const string EmptyMethod = "bench.empty";
    const string ComplexMethod = "bench.complex";
    const string NotifyMethod = "bench.notify";

    RpcService _rpcService = null!;
    RpcClient _client = null!;
    NamedPipePeer _peer = null!;
    ComplexRequest _complexRequest = null!;

    /// <summary>建连并注册 Handler。</summary>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var serverRouter = new HandlerRouter();
        // 空载荷：立即返回，测量协议 + 管道往返下限
        serverRouter.AddRequestHandler(EmptyMethod, () => { });
        // 复杂 JSON：无 Task.Delay，仅序列化与拷贝字段
        serverRouter.AddRequestHandler<ComplexRequest, ComplexResponse>(ComplexMethod, (ctx, ct) =>
        {
            var req = ctx.Message;
            return Task.FromResult(new ComplexResponse
            {
                Success = true,
                Message = req.Name,
                ProcessedCount = req.Id,
            });
        });
        serverRouter.AddNotifyHandler<string>(NotifyMethod, _ => { });

        var pipe = "bench_latency_" + Guid.NewGuid().ToString("N");
        _rpcService = new RpcService(pipe, serverRouter);
        _rpcService.Start();

        var clientRouter = new HandlerRouter();
        clientRouter.AddNotifyHandler<string>(NotifyMethod, _ => { });
        _client = new RpcClient(pipe, clientRouter);
        _peer = await _client.ConnectAsync();

        _complexRequest = new ComplexRequest
        {
            Id = 12345,
            Name = "Benchmark Payload",
            Timestamp = DateTime.UtcNow,
            Tags = new List<string> { "ipc", "benchmark", "dotnet" },
            Details = new NestedObject
            {
                Score = 99.9,
                Metadata = new Dictionary<string, int> { { "cpu", 8 }, { "memory", 16 } },
            },
        };

        await _peer.Request(EmptyMethod);
        await _peer.Request<ComplexRequest, ComplexResponse>(ComplexMethod, _complexRequest);
        await _peer.Notify(NotifyMethod, "warmup");
    }

    /// <summary>关闭连接与服务端。</summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _peer?.Close();
        _client?.Dispose();
        _rpcService?.Close();
    }

    /// <summary>空载荷 Request 往返（基线延迟）。</summary>
    [Benchmark(Baseline = true)]
    public Task EmptyRequest() => _peer.Request(EmptyMethod);

    /// <summary>单向 Notify（无应答）；测量发送路径。</summary>
    [Benchmark]
    public Task EmptyNotify() => _peer.Notify(NotifyMethod, "x");

    /// <summary>复杂对象 JSON Request（Handler 无人为等待）。</summary>
    [Benchmark]
    public Task<ComplexResponse> ComplexJsonRequest()
        => _peer.Request<ComplexRequest, ComplexResponse>(ComplexMethod, _complexRequest);
}

/// <summary>
/// 载荷大小梯度：byte[] Echo 往返。与延迟类分开，避免 Params 污染空载荷用例。
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class IpcPayloadBenchmarks
{
    const string EchoMethod = "bench.echo";

    RpcService _rpcService = null!;
    RpcClient _client = null!;
    NamedPipePeer _peer = null!;
    byte[] _payload = null!;

    /// <summary>请求/响应载荷字节数。</summary>
    [Params(0, 1024, 16384, 65536)]
    public int PayloadBytes { get; set; }

    /// <summary>建连；按 PayloadBytes 构造缓冲区。</summary>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var serverRouter = new HandlerRouter();
        serverRouter.AddRequestHandler<byte[], byte[]>(EchoMethod, (ctx, ct) => Task.FromResult(ctx.Message));

        var pipe = "bench_payload_" + Guid.NewGuid().ToString("N");
        _rpcService = new RpcService(pipe, serverRouter);
        _rpcService.Start();

        _client = new RpcClient(pipe, new HandlerRouter());
        _peer = await _client.ConnectAsync();

        _payload = new byte[PayloadBytes];
        if (PayloadBytes > 0)
            new Random(42).NextBytes(_payload);

        await _peer.Request<byte[], byte[]>(EchoMethod, _payload);
    }

    /// <summary>释放资源。</summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _peer?.Close();
        _client?.Dispose();
        _rpcService?.Close();
    }

    /// <summary>指定大小的 byte[] Echo 往返。</summary>
    [Benchmark]
    public Task<byte[]> PayloadEcho()
        => _peer.Request<byte[], byte[]>(EchoMethod, _payload);
}

/// <summary>
/// 吞吐基准：串行批量与并发 Request。
/// Mean 为整批耗时；吞吐 ≈ BatchSize(或 ConcurrentCount) / Mean。
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class IpcThroughputBenchmarks
{
    const string EmptyMethod = "bench.empty";

    /// <summary>串行批量大小；换算 ops/s 时用 BatchSize / Mean。</summary>
    public const int BatchSize = 1000;

    /// <summary>并发请求数；对齐默认 MaxConcurrent=64。</summary>
    public const int ConcurrentCount = 64;

    RpcService _rpcService = null!;
    RpcClient _client = null!;
    NamedPipePeer _peer = null!;

    /// <summary>建连；服务端空 Handler。</summary>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var serverRouter = new HandlerRouter();
        serverRouter.AddRequestHandler(EmptyMethod, () => { });

        var pipe = "bench_throughput_" + Guid.NewGuid().ToString("N");
        _rpcService = new RpcService(pipe, serverRouter);
        _rpcService.Start();

        _client = new RpcClient(pipe, new HandlerRouter());
        _peer = await _client.ConnectAsync();

        for (int i = 0; i < 100; i++)
            await _peer.Request(EmptyMethod);
    }

    /// <summary>释放资源。</summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _peer?.Close();
        _client?.Dispose();
        _rpcService?.Close();
    }

    /// <summary>串行空 Request × <see cref="BatchSize"/>（Mean = 整批耗时）。</summary>
    [Benchmark(Baseline = true)]
    public async Task SerialEmptyRequests()
    {
        for (int i = 0; i < BatchSize; i++)
            await _peer.Request(EmptyMethod);
    }

    /// <summary>并发空 Request × <see cref="ConcurrentCount"/>（Mean = 整批完成耗时）。</summary>
    [Benchmark]
    public async Task ConcurrentEmptyRequests()
    {
        var tasks = new Task[ConcurrentCount];
        for (int i = 0; i < ConcurrentCount; i++)
            tasks[i] = _peer.Request(EmptyMethod);
        await Task.WhenAll(tasks);
    }
}
