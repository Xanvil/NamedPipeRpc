using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.Test.ServerContracts;

/// <summary>
/// 服务端程序集中的计费载荷（与客户端同名类型无共享，仅 JSON 形状对齐）。
/// </summary>
public sealed class PayReq
{
    public int Amount { get; set; }
}

/// <summary>
/// 服务端退款请求载荷。
/// </summary>
public sealed class RefundReq
{
    public string Reason { get; set; } = "";
}

/// <summary>
/// 服务端库存查询载荷。
/// </summary>
public sealed class SkuReq
{
    public string Sku { get; set; } = "";
}

/// <summary>
/// 服务端自有计费契约：短名 <c>IBilling</c>，与客户端同名接口经 RPC 名 + JSON 形状互通。
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
/// 实现 <see cref="IBilling"/> 的服务类；可用 <c>AddRequestHandler(peer => new BillingImpl())</c> 按类注册。
/// </summary>
public sealed class BillingImpl : IBilling
{
    /// <inheritdoc />
    public Task<int> PayAsync(PayReq req, CancellationToken ct = default)
        => Task.FromResult(req.Amount + 1);

    /// <inheritdoc />
    public Task RefundAsync(RefundReq req, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string> DescribeAsync(int id, string tag, CancellationToken ct = default)
        => Task.FromResult($"{id}:{tag}");

    /// <inheritdoc />
    public Task PingAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<string> HealthAsync(CancellationToken ct = default)
        => Task.FromResult("ok");
}

/// <summary>
/// 类上直接标注 <see cref="RpcRequestAttribute"/> 的服务端契约（默认前缀 = 类短名 InventoryService）。
/// </summary>
[RpcRequest]
public sealed class InventoryService
{
    /// <summary>默认 RPC 名：InventoryService.GetStock。</summary>
    public Task<int> GetStockAsync(SkuReq req, CancellationToken ct = default)
        => Task.FromResult(req.Sku == "A1" ? 7 : 0);
}

/// <summary>
/// 服务端订单通知契约（Notify）；用于目录暴露与跨程序集互通。
/// </summary>
[RpcNotify]
public interface IOrderEvents
{
    /// <summary>默认 RPC 名：IOrderEvents.Updated。</summary>
    Task UpdatedAsync(string msg, CancellationToken ct = default);
}

