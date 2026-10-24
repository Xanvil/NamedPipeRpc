using Xanvil.NamedPipeRpc;

namespace NamedPipeRpc.Test.ClientContracts;

/// <summary>
/// 客户端报价载荷。故意不声明币种和重试，用来观察服务端自定义类型的缺字段。
/// </summary>
public sealed class QuoteReq
{
    /// <summary>金额。</summary>
    public int Amount { get; set; }
}

/// <summary>
/// 与服务端参数个数、名称一致。用作对照：管道能通，且客户端写出的值会原样到达。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteFull
{
    /// <summary>RPC 名 <c>opt.Quote</c>。三个业务参数打成字段包。</summary>
    Task<string> QuoteAsync(int amount, string currency, int retries, CancellationToken ct = default);

    /// <summary>RPC 名 <c>opt.Label</c>。单个字符串直接作为载荷。</summary>
    Task<string> LabelAsync(string name, CancellationToken ct = default);
}

/// <summary>
/// 只保留一个业务参数。载荷是这个数字本身，和服务端的三个参数不是同一种形状。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteAmountOnly
{
    /// <summary>RPC 名 <c>opt.Quote</c>。</summary>
    Task<string> QuoteAsync(int amount, CancellationToken ct = default);
}

/// <summary>
/// 少了可选的 <c>retries</c>。两端都是两个及以上业务参数，JSON 仍是对象，只是缺一个字段。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteMissingRetries
{
    /// <summary>RPC 名 <c>opt.Quote</c>。</summary>
    Task<string> QuoteAsync(int amount, string currency, CancellationToken ct = default);
}

/// <summary>
/// 少了中间的 <c>currency</c>，但 <c>retries</c> 仍在。用来区分“没传”和“传了 0”。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteMissingCurrency
{
    /// <summary>RPC 名 <c>opt.Quote</c>。</summary>
    Task<string> QuoteAsync(int amount, int retries, CancellationToken ct = default);
}

/// <summary>
/// 参数名与服务端不一致（<c>money</c> 对 <c>amount</c>）。个数相同，但 JSON 字段对不上。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteRenamedAmount
{
    /// <summary>RPC 名 <c>opt.Quote</c>。</summary>
    Task<string> QuoteAsync(int money, string currency, int retries, CancellationToken ct = default);
}

/// <summary>
/// 参数顺序与服务端不同，名称相同。JSON 按字段名绑定，不按位置。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteReordered
{
    /// <summary>RPC 名 <c>opt.Quote</c>。</summary>
    Task<string> QuoteAsync(string currency, int retries, int amount, CancellationToken ct = default);
}

/// <summary>
/// 比服务端多一个参数。多出来的字段应被忽略。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteExtra
{
    /// <summary>RPC 名 <c>opt.Quote</c>。</summary>
    Task<string> QuoteAsync(int amount, string currency, int retries, string note, CancellationToken ct = default);
}

/// <summary>
/// 没有任何业务参数，因此不写载荷。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteNoArgs
{
    /// <summary>RPC 名 <c>opt.Quote</c>。</summary>
    Task<string> QuoteAsync(CancellationToken ct = default);

    /// <summary>RPC 名 <c>opt.Label</c>。服务端期望一个字符串，这里不写载荷。</summary>
    Task<string> LabelAsync(CancellationToken ct = default);
}

/// <summary>
/// 客户端接口自己带可选参数。省略实参时由 C# 编译器在调用代理之前填上，再序列化出去。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteClientDefaults
{
    /// <summary>RPC 名 <c>opt.Quote</c>。</summary>
    Task<string> QuoteAsync(int amount, string currency = "USD", int retries = 9, CancellationToken ct = default);
}

/// <summary>
/// 只有一个业务参数，参数名与服务端 <c>name</c> 不同。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteLabelRenamed
{
    /// <summary>RPC 名 <c>opt.Label</c>。字段名是 <c>title</c>。</summary>
    Task<string> LabelAsync(string title, CancellationToken ct = default);
}

/// <summary>
/// <c>amount</c> 的 CLR 类型与服务端 <c>int</c> 不同。JSON 字段值会是字符串。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteAmountAsString
{
    /// <summary>RPC 名 <c>opt.Quote</c>。</summary>
    Task<string> QuoteAsync(string amount, string currency, int retries, CancellationToken ct = default);
}

/// <summary>
/// 用自定义类型承载可选字段。参数名故意写成 <c>body</c>，与服务端 <c>req</c> 不同。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteDto
{
    /// <summary>RPC 名 <c>opt.QuoteDto</c>。载荷就是 <c>QuoteReq</c>。</summary>
    Task<string> QuoteDtoAsync(QuoteReq body, CancellationToken ct = default);
}

/// <summary>
/// 没有业务参数。服务端那个唯一参数会得到 null。
/// </summary>
[RpcRequest("opt")]
public interface IQuoteDtoOmitted
{
    /// <summary>RPC 名 <c>opt.QuoteDto</c>。</summary>
    Task<string> QuoteDtoAsync(CancellationToken ct = default);
}
