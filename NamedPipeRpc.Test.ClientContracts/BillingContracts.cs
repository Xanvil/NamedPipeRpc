using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.Test.ClientContracts;

/// <summary>
/// 客户端程序集中的计费载荷（与服务端同名类型无共享，仅 JSON 形状对齐）。
/// </summary>
public sealed class PayReq
{
    public int Amount { get; set; }
}

/// <summary>
/// 客户端退款请求载荷。
/// </summary>
public sealed class RefundReq
{
    public string Reason { get; set; } = "";
}

/// <summary>
/// 客户端库存查询载荷。
/// </summary>
public sealed class SkuReq
{
    public string Sku { get; set; } = "";
}

/// <summary>
/// 客户端自有计费契约：短名 <c>IBilling</c>，与服务端同名接口经 RPC 名 + JSON 形状互通。
/// </summary>
[RpcRequest]
public interface IBilling
{
    /// <summary>默认 RPC 名：IBilling.Pay。</summary>
    Task<int> PayAsync(PayReq req, CancellationToken ct = default);

    /// <summary>整段 RPC 名由 <see cref="RpcMethodAttribute"/> 覆盖为 billing.refund。</summary>
    [RpcMethod("billing.refund")]
    Task RefundAsync(RefundReq req, CancellationToken ct = default);

    /// <summary>两个业务参数，生成字段包，字段名等于参数名。缺少的字段使用类型默认值。</summary>
    Task<string> DescribeAsync(int id, string tag, CancellationToken ct = default);

    /// <summary>方法级空前缀 → Ping。</summary>
    [RpcMethod(Prefix = "")]
    Task PingAsync(CancellationToken ct = default);

    /// <summary>方法级其它前缀 → ops.Health。</summary>
    [RpcMethod(Prefix = "ops")]
    Task<string> HealthAsync(CancellationToken ct = default);
}

/// <summary>
/// 客户端用显式契约前缀对齐服务端类契约 <c>InventoryService</c>。
/// </summary>
[RpcRequest("InventoryService")]
public interface IInventory
{
    /// <summary>默认 RPC 名：InventoryService.GetStock。</summary>
    Task<int> GetStockAsync(SkuReq req, CancellationToken ct = default);
}

/// <summary>
/// 客户端订单通知契约（Notify）；RPC 名与服务端同名接口对齐。
/// </summary>
[RpcNotify]
public interface IOrderEvents
{
    /// <summary>默认 RPC 名：IOrderEvents.Updated。</summary>
    Task UpdatedAsync(string msg, CancellationToken ct = default);
}

