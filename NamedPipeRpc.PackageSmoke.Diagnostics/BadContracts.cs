using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.PackageSmoke.Diagnostics;

/// <summary>
/// 故意非法的契约集合：用于验证 NuGet 包内 Source Generator 诊断（NPRC001–007）。
/// 本项目应编译失败；成功编译即冒烟失败。
/// </summary>
public static class BadContracts
{
}

/// <summary>NPRC001：返回类型不是 Task / Task&lt;T&gt;。</summary>
[RpcRequest("bad")]
public interface IBadReturnType
{
    int NotTask(int x);
}

/// <summary>NPRC002：泛型契约不受支持。</summary>
[RpcRequest]
public interface IBadGeneric<T>
{
    Task<T> GetAsync(CancellationToken ct = default);
}

/// <summary>NPRC004：含非方法成员（属性）。</summary>
[RpcRequest("badprops")]
public interface IBadProperty
{
    Task PingAsync(CancellationToken ct = default);
    int Count { get; }
}

/// <summary>NPRC005：含 ref 参数。</summary>
[RpcRequest("badref")]
public interface IBadRefParam
{
    Task MutateAsync(ref int value, CancellationToken ct = default);
}

/// <summary>NPRC003：RPC 名 UTF-8 超过 255 字节（256 个 'a'）。</summary>
[RpcRequest]
public interface IBadMethodNameTooLong
{
    [RpcMethod("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    Task TooLongAsync(CancellationToken ct = default);
}

/// <summary>NPRC006：[RpcNotify] 契约不能返回 Task&lt;T&gt;。</summary>
[RpcNotify("badnotify")]
public interface IBadNotifyReturnType
{
    Task<int> UpdatedAsync(string msg, CancellationToken ct = default);
}

/// <summary>NPRC007：同一类型不能同时是请求服务和通知通道。</summary>
[RpcRequest("badmix")]
[RpcNotify("badmix")]
public interface IBadMixedKind
{
    Task PingAsync(CancellationToken ct = default);
}
