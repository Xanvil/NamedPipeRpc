using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.Test.ServerContracts;

/// <summary>
/// 服务端报价契约。方法带多个参数，其中后两个在 C# 里是可选参数。
/// RPC 名固定为 <c>opt.Quote</c> / <c>opt.Label</c>，便于和参数更少的客户端接口对齐。
/// </summary>
[RpcRequest("opt")]
public interface IQuote
{
    /// <summary>
    /// 三个业务参数。管道上缺少的字段是类型默认值；本地直接调用时仍可省略后两个参数。
    /// </summary>
    Task<string> QuoteAsync(int amount, string currency = "CNY", int retries = 3, CancellationToken ct = default);

    /// <summary>一个业务参数。载荷就是这个字符串，参数名不参与匹配。</summary>
    Task<string> LabelAsync(string name = "anon", CancellationToken ct = default);

    /// <summary>
    /// 一个业务参数，载荷就是 <see cref="QuoteReq"/>。类型内部的成员可以缺。
    /// </summary>
    Task<string> QuoteDtoAsync(QuoteReq req, CancellationToken ct = default);
}

/// <summary>
/// 可选数据放在自定义类型里。缺字段由 JSON 反序列化变成 null，不由方法参数默认值决定。
/// </summary>
public sealed class QuoteReq
{
    /// <summary>金额。缺字段时为 0。</summary>
    public int Amount { get; set; }

    /// <summary>币种。缺字段时为 null。</summary>
    public string? Currency { get; set; }

    /// <summary>重试次数。缺字段时为 null，从而和显式传入 0 区分开。</summary>
    public int? Retries { get; set; }
}

/// <summary>
/// <see cref="IQuote"/> 的实现。返回值把实际收到的参数拼出来，方便断言默认值有没有生效。
/// </summary>
public sealed class QuoteServer : IQuote
{
    /// <inheritdoc />
    public Task<string> QuoteAsync(int amount, string currency = "CNY", int retries = 3, CancellationToken ct = default)
        => Task.FromResult($"{amount}|{currency ?? "<null>"}|{retries}");

    /// <inheritdoc />
    public Task<string> LabelAsync(string name = "anon", CancellationToken ct = default)
        => Task.FromResult(name ?? "<null>");

    /// <inheritdoc />
    public Task<string> QuoteDtoAsync(QuoteReq req, CancellationToken ct = default)
    {
        if (req is null)
            return Task.FromResult("<null>");
        string currency = req.Currency ?? "CNY";
        int retries = req.Retries ?? 3;
        return Task.FromResult($"{req.Amount}|{currency}|{retries}");
    }
}
