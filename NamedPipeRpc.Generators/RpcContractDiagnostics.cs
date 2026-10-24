using Microsoft.CodeAnalysis;

namespace Xanvil.NamedPipeRpc.Generators;

/// <summary>
/// 契约 Source Generator 诊断描述符。
/// </summary>
internal static class RpcContractDiagnostics
{
    public static readonly DiagnosticDescriptor MethodMustReturnTask = new(
        id: "NPRC001",
        title: "RPC 契约方法必须返回 Task 或 Task<T>",
        messageFormat: "方法 '{0}' 的返回类型必须是 Task 或 Task<T>（当前为 '{1}'）",
        category: "NamedPipeRpc.Contract",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor GenericContractNotSupported = new(
        id: "NPRC002",
        title: "不支持泛型 RPC 契约",
        messageFormat: "类型 '{0}' 带有类型参数，当前版本不支持泛型 [RpcRequest] / [RpcNotify]",
        category: "NamedPipeRpc.Contract",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RpcMethodNameTooLong = new(
        id: "NPRC003",
        title: "RPC 方法名过长",
        messageFormat: "方法 '{0}' 的 RPC 名 '{1}' UTF-8 长度超过 255 字节（协议限制）",
        category: "NamedPipeRpc.Contract",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedMember = new(
        id: "NPRC004",
        title: "RPC 契约不支持该成员",
        messageFormat: "类型 '{0}' 含有非方法契约成员 '{1}'（属性/事件/索引器等）；仅支持返回 Task/Task<T> 的方法（Notify 须为非泛型 Task）",
        category: "NamedPipeRpc.Contract",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RefLikeParameterNotSupported = new(
        id: "NPRC005",
        title: "不支持 ref/in/out 参数",
        messageFormat: "方法 '{0}' 含有 ref/in/out 参数，无法作为 RPC 载荷",
        category: "NamedPipeRpc.Contract",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotifyMustReturnNonGenericTask = new(
        id: "NPRC006",
        title: "Notify 方法必须返回非泛型 Task",
        messageFormat: "[RpcNotify] 契约的方法 '{0}' 必须返回非泛型 Task（当前为 '{1}'）",
        category: "NamedPipeRpc.Contract",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MixedRequestAndNotifyNotSupported = new(
        id: "NPRC007",
        title: "不能同时标记为请求服务和通知通道",
        messageFormat: "类型 '{0}' 同时标了 [RpcRequest] 与 [RpcNotify]；请拆成两个接口",
        category: "NamedPipeRpc.Contract",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NotifyContractMustBeInterface = new(
        id: "NPRC008",
        title: "通知契约只能是接口",
        messageFormat: "类型 '{0}' 标了 [RpcNotify]，通知通道只能定义在接口上",
        category: "NamedPipeRpc.Contract",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
