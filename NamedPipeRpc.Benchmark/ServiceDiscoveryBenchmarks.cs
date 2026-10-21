using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

using System;
using System.Threading.Tasks;

using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.Benchmark;

/// <summary>
/// 服务发现 / 单条订阅 / 定向推送相关基准，与 NamedPipeRpc.Test.ServiceDiscoveryTests 场景对应。
/// 对外方法：ListRemoteRequests / ListRemoteNotifies / SubscribeExisting / NotifyBroadcast / NotifySubscribersDirected。
/// </summary>
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
public class ServiceDiscoveryBenchmarks
{
    const string NotifyMethod = "bench.order.updated";
    const string RequestMethod = "bench.billing.pay";

    RpcService _rpcService = null!;
    RpcClient _client = null!;
    NamedPipePeer _peer = null!;

    /// <summary>
    /// 建立已订阅连接：服务端暴露 Request + Notify；客户端 Start 自动订阅本端 Notify。
    /// </summary>
    [GlobalSetup]
    public async Task GlobalSetup()
    {
        var serverRouter = new HandlerRouter();
        serverRouter.AddRequestHandler<int, int>(RequestMethod, (ctx, ct) => Task.FromResult(ctx.Message));
        serverRouter.AddNotifyHandler<string>(NotifyMethod, _ => { });

        var pipe = "bench_discovery_" + Guid.NewGuid().ToString("N");
        _rpcService = new RpcService(pipe, serverRouter);
        _rpcService.Start();

        var clientRouter = new HandlerRouter();
        clientRouter.AddNotifyHandler<string>(NotifyMethod, _ => { });
        _client = new RpcClient(pipe, clientRouter);
        _peer = await _client.ConnectAsync();

        // Start 后台自动订阅；显式再订一次确保订阅表就绪
        await _peer.SubscribeAsync(NotifyMethod);

        await _peer.ListRemoteRequestMethodsAsync();
        await _peer.ListRemoteNotifyMethodsAsync();
        await _rpcService.Peers.Notify(NotifyMethod, "warmup");
        await _rpcService.Peers.NotifySubscribers(NotifyMethod, "warmup");
    }

    /// <summary>释放管道与连接。</summary>
    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _peer?.Close();
        _client?.Dispose();
        _rpcService?.Close();
    }

    /// <summary>远端 Request 列表查询（Sys listRequests）。</summary>
    [Benchmark(Baseline = true)]
    public Task<RpcRequestMethodInfo[]> ListRemoteRequests()
        => _peer.ListRemoteRequestMethodsAsync();

    /// <summary>远端 Notify 列表查询（Sys listNotifies）。</summary>
    [Benchmark]
    public Task<RpcNotifyMethodInfo[]> ListRemoteNotifies()
        => _peer.ListRemoteNotifyMethodsAsync();

    /// <summary>单条 Subscribe（方法已存在；含期望集写入）。</summary>
    [Benchmark]
    public async Task SubscribeExisting()
        => await _peer.SubscribeAsync(NotifyMethod);

    /// <summary>Peers.Notify 全量广播。</summary>
    [Benchmark]
    public Task NotifyBroadcast()
        => _rpcService.Peers.Notify(NotifyMethod, "bench");

    /// <summary>Peers.NotifySubscribers 定向推送（仅已订阅者）。</summary>
    [Benchmark]
    public Task NotifySubscribersDirected()
        => _rpcService.Peers.NotifySubscribers(NotifyMethod, "bench");
}
