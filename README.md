# NamedPipeRpc

基于 Windows 命名管道的双向 RPC 库（命名空间 `Xanvil.NamedPipeRpc`），面向同机进程间 IPC。

目标框架：`net472` / `net8.0`。载荷序列化使用 `System.Text.Json`。

完整 API、心智模型与协议说明见 **[NamedPipeRpc/README.md](NamedPipeRpc/README.md)**。契约的调用写在下面的「契约用法」。

## 仓库结构

| 路径 | 说明 |
|------|------|
| `NamedPipeRpc/` | 主库（含 Attribute；NuGet 打包时附带 Generator） |
| `NamedPipeRpc.Generators/` | `[RpcRequest]` / `[RpcNotify]` Source Generator |
| `NamedPipeRpc.Test/` | 单元测试（含跨程序集契约端到端） |
| `NamedPipeRpc.Test.ClientContracts/` | 测试用客户端契约程序集 |
| `NamedPipeRpc.Test.ServerContracts/` | 测试用服务端契约程序集 |
| `NamedPipeRpc.Benchmark/` | BenchmarkDotNet 基准 |
| `NamedPipeRpc.PackageSmoke/` | 纯 PackageReference 冒烟：成功路径 + 运行时错误处理 |
| `NamedPipeRpc.PackageSmoke.Diagnostics/` | 非法契约编译冒烟（期望 NPRC001–007） |
| `scripts/Verify-NamedPipeRpcNupkg.ps1` | pack + 断言包内 Generator + Diagnostics/运行时冒烟 |
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

验证 NuGet 包是否含 Source Generator，并以纯 `PackageReference` 消费：

```powershell
pwsh -File scripts/Verify-NamedPipeRpcNupkg.ps1
```

## 契约用法

`[RpcRequest]` 是请求服务，`[RpcNotify]` 是通知通道。同一个接口可以出现在两端。`Add*Handler` 是本端实现，`peer.Get*Proxy` 返回源生成器生成的代理，只发到这一条连接。

```csharp
[RpcRequest]
public interface IBilling
{
    Task<int> PayAsync(PayReq req, CancellationToken ct = default);
}

[RpcNotify]
public interface IOrderEvents
{
    Task UpdatedAsync(string msg, CancellationToken ct = default);
}

// 请求：工厂为每条连接创建一个实例。方法名马上进入目录
serverRouter.AddRequestHandler<IBilling>(peer => new BillingImpl(peer));
IBilling billing = peer.GetRequestProxy<IBilling>();
int n = await billing.PayAsync(req);

// 通知：AddNotifyHandler 表示本端可以接收，不会因此自动订阅
serverRouter.AddNotifyHandler<IOrderEvents>(peer => new OrderEventsImpl(peer));

// 要在连接后自动订阅，再调用 AddNotifyProxy
clientRouter.AddNotifyHandler<IOrderEvents>(peer => new OrderEventsSink(peer));
clientRouter.AddNotifyProxy<IOrderEvents>();

// GetNotifyProxy 返回生成的代理，只向这一条连接发送
IOrderEvents outgoing = serverPeer.GetNotifyProxy<IOrderEvents>();
await outgoing.UpdatedAsync("hello");
```

载荷按业务参数个数区分，`CancellationToken` 不进入载荷：

| 业务参数 | 载荷 |
|---|---|
| 没有 | 不写载荷 |
| 1 个 | 就是这个参数。`PayAsync(PayReq req)` 的 JSON 是 `PayReq` 本身，两端参数名不必相同 |
| 2 个及以上 | 参数包，字段名等于参数名，例如 `{ "id": 1, "tag": "x" }`。缺少的字段使用该类型的默认值 |

可以省略的成员放进自定义类型，作为那一个参数发送。类型内部的缺字段由 JSON 反序列化决定。

每条连接 `Start` 时调用工厂一次。实例实现了 `IDisposable` 时，连接释放会调用 `Dispose`。服务端要调用某一条连接，从那个 `NamedPipePeer` 上取代理。按方法名的 `AddNotifyHandler(string, ...)` 仍然是一个委托共用全部连接，并在登记后自动订阅。

升级目标框架或 NuGet 包版本时，请改仓库根目录的 `Directory.Build.props` / `Directory.Packages.props`，不要在各 `.csproj` 中重复写版本号。
