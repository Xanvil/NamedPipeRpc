using System.Collections.Immutable;

namespace Xanvil.NamedPipeRpc.Generators;

/// <summary>
/// 契约类型种类：接口生成代理；类只用于 AddRequestHandler。
/// </summary>
internal enum ContractKind
{
    Interface,
    Class,
}

/// <summary>
/// 契约角色。由类型上的特性决定，同一个接口在两端都只表示这一种契约。
/// </summary>
internal enum ContractRole
{
    Request,
    Notify,
}

/// <summary>
/// 单个业务参数（不含 CancellationToken）。
/// </summary>
/// <param name="Name">参数名（多参时作为 JSON 字段名）。</param>
/// <param name="TypeDisplay">全局类型显示名（含 global::）。</param>
internal sealed record ContractParameterModel(string Name, string TypeDisplay);

/// <summary>
/// 契约上的一个方法。角色由所属类型决定。
/// </summary>
/// <param name="MethodName">CLR 方法名。</param>
/// <param name="RpcName">管道 RPC 方法名。</param>
/// <param name="Parameters">业务参数列表。</param>
/// <param name="HasCancellationToken">是否含 CancellationToken。</param>
/// <param name="CancellationTokenParameterName">CT 参数名（若有）。</param>
/// <param name="ResponseTypeDisplay">Request 且 Task&lt;T&gt; 时为 T；纯 Task 时为 null。</param>
/// <param name="ArgsTypeName">两个及以上业务参数时的参数包短名。零个或一个业务参数时为 null。</param>
internal sealed record ContractMethodModel(
    string MethodName,
    string RpcName,
    ImmutableArray<ContractParameterModel> Parameters,
    bool HasCancellationToken,
    string? CancellationTokenParameterName,
    string? ResponseTypeDisplay,
    string? ArgsTypeName);

/// <summary>
/// 带 [RpcRequest] 或 [RpcNotify] 的接口或类。
/// </summary>
/// <param name="Kind">接口或类。</param>
/// <param name="Role">请求服务或通知通道。</param>
/// <param name="TypeFullName">含命名空间的完整名（不含 global::）。</param>
/// <param name="TypeDisplay">global:: 限定显示名。</param>
/// <param name="HintName">生成文件提示名（安全标识符）。</param>
/// <param name="ContractName">契约默认前缀（可为空字符串表示无前缀）。</param>
/// <param name="ContainingNamespace">类型所在命名空间；全局命名空间时为空。</param>
/// <param name="ProxyTypeName">接口契约生成的代理类短名；类契约为空。</param>
/// <param name="Methods">合法契约方法。</param>
/// <param name="Accessibility">public / internal 等修饰。</param>
internal sealed record ContractTypeModel(
    ContractKind Kind,
    ContractRole Role,
    string TypeFullName,
    string TypeDisplay,
    string HintName,
    string ContractName,
    string ContainingNamespace,
    string? ProxyTypeName,
    ImmutableArray<ContractMethodModel> Methods,
    string Accessibility);
