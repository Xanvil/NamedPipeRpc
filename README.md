# NamedPipeRpc

基于 Windows 命名管道的双向 RPC 库（命名空间 `Xanvil.NamedPipeRpc`），面向同机进程间 IPC。

目标框架：`net472` / `net8.0`。载荷序列化使用 `System.Text.Json`。

完整 API、心智模型与协议说明见 **[NamedPipeRpc/README.md](NamedPipeRpc/README.md)**；架构关系图见 [NamedPipeRpc/NamedPipeRpc.struct.md](NamedPipeRpc/NamedPipeRpc.struct.md)。

## 仓库结构

| 路径 | 说明 |
|------|------|
| `NamedPipeRpc/` | 主库 |
| `NamedPipeRpc.Test/` | 单元测试 |
| `NamedPipeRpc.Benchmark/` | BenchmarkDotNet 基准 |
| `Directory.Build.props` | 统一 `TargetFrameworks`、语言版本、程序集版本等 |
| `Directory.Packages.props` | NuGet 中央包版本管理（CPM） |
| `NamedPipeRpc.slnx` | 解决方案 |

## 快速上手

```csharp
using Xanvil.NamedPipeRpc;

var pipe = "demo-pipe";

var serverRouter = new HandlerRouter();
serverRouter.AddRequestHandler<int, int>("billing.pay", (ctx, ct) =>
    Task.FromResult(ctx.Message + 1));

using var service = new RpcService(pipe, serverRouter);
service.Start();

using var client = new RpcClient(pipe);
var peer = await client.ConnectAsync();

try
{
    int result = await peer.Request<int, int>("billing.pay", 41); // 42
}
finally
{
    peer.Close();
    service.Close();
}
```

## 构建与测试

```bash
dotnet build NamedPipeRpc.slnx
dotnet test NamedPipeRpc.Test/NamedPipeRpc.Test.csproj
```

升级目标框架或 NuGet 包版本时，请改仓库根目录的 `Directory.Build.props` / `Directory.Packages.props`，不要在各 `.csproj` 中重复写版本号。
