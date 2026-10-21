using BenchmarkDotNet.Running;

namespace NamedPipeRpc.Benchmark;

/// <summary>
/// 仅运行 NamedPipeRpc 相关基准（延迟 / 载荷 / 吞吐 / 服务发现）。
/// </summary>
internal static class Program
{
    static void Main(string[] args)
    {
        BenchmarkRunner.Run<IpcLatencyBenchmarks>();
        BenchmarkRunner.Run<IpcPayloadBenchmarks>();
        BenchmarkRunner.Run<IpcThroughputBenchmarks>();
        BenchmarkRunner.Run<ServiceDiscoveryBenchmarks>();
    }
}
