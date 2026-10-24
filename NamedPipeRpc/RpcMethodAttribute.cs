using System;

namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 控制契约方法在管道上的 RPC 名：可覆盖整段名，或仅覆盖前缀（与去 <c>Async</c> 后的短名拼接）。
/// </summary>
/// <remarks>
/// <para>
/// 解析优先级：
/// </para>
/// <list type="number">
/// <item><description>构造函数指定的 <see cref="Name"/>（整段名）最高优先，忽略前缀拼接；</description></item>
/// <item><description>否则短名 = CLR 方法名去掉尾缀 <c>Async</c>；前缀 = 本特性的 <see cref="Prefix"/>（若已设置，含空字符串）否则契约默认前缀；</description></item>
/// <item><description>前缀为空则 RPC 名为短名，非空则为 <c>{前缀}.{短名}</c>。</description></item>
/// </list>
/// <para>
/// 跨程序集互通时，两端对该方法解析出的 RPC 名必须一致（OrdinalIgnoreCase），
/// 且载荷 / 响应 JSON 形状兼容；不要求 CLR 方法或参数类型同一。
/// 一个业务参数时载荷就是该参数，不要求参数名一致。
/// 两个及以上业务参数时载荷是参数包，字段名必须一致；缺少的字段使用该类型的默认值。
/// 可选成员写在自定义类型里，再作为那一个参数传递。没有业务参数时不写载荷。
/// </para>
/// <para>
/// UTF-8 总长须 ≤ 255 字节。可用于 <see cref="RpcRequestAttribute"/> 契约
/// （返回 <see cref="System.Threading.Tasks.Task"/> / <see cref="System.Threading.Tasks.Task{TResult}"/>）
/// 或 <see cref="RpcNotifyAttribute"/> 契约（须返回非泛型 <see cref="System.Threading.Tasks.Task"/>）。
/// <see cref="System.Threading.CancellationToken"/> 不进载荷。
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [RpcRequest("sample")]
/// public interface ISample
/// {
///     // 默认：sample.Add
///     Task&lt;int&gt; AddAsync(int a, int b, CancellationToken ct = default);
///
///     // 无前缀：Foo
///     [RpcMethod(Prefix = "")]
///     Task FooAsync(CancellationToken ct = default);
///
///     // 方法级前缀：billing.Pay
///     [RpcMethod(Prefix = "billing")]
///     Task PayAsync(int m, CancellationToken ct = default);
///
///     // 整段覆盖：pay
///     [RpcMethod("pay")]
///     Task LegacyPayAsync(int m, CancellationToken ct = default);
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RpcMethodAttribute : Attribute
{
    /// <summary>
    /// 不指定整段名；可通过 <see cref="Prefix"/> 覆盖本方法前缀。
    /// </summary>
    public RpcMethodAttribute()
    {
    }

    /// <summary>
    /// 指定管道上使用的完整 RPC 方法名（不再与前缀拼接）。
    /// </summary>
    /// <param name="name">完整方法名（如 <c>billing.refund</c> 或 <c>pay</c>）。</param>
    public RpcMethodAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>
    /// 管道上使用的完整 RPC 方法名；非 <see langword="null"/> 时忽略 <see cref="Prefix"/> 与契约默认前缀。
    /// </summary>
    public string? Name { get; }

    /// <summary>
    /// 本方法的 RPC 前缀覆盖。未设置时使用契约默认前缀；设为 <c>""</c> 表示无前缀（仅短名）。
    /// 与构造函数 <see cref="Name"/> 同时存在时以整段名为准。
    /// </summary>
    public string? Prefix { get; set; }
}

