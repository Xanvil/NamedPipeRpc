using System;

namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 标记通知通道契约。本端用 <c>AddNotifyHandler</c> 接收，用 <c>AddNotifyProxy</c> 在连接后自动订阅，用连接上的 <c>GetNotifyProxy</c> 发送。
/// </summary>
/// <remarks>
/// 只能标在接口上。该接口的方法必须全部返回非泛型 <see cref="System.Threading.Tasks.Task"/>。
/// <c>AddNotifyHandler</c> 只表示本端可以接收。<c>AddNotifyProxy</c> 才把方法放进自动订阅清单。
/// </remarks>
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class RpcNotifyAttribute : Attribute
{
    /// <summary>使用类型短名作为默认 RPC 前缀。</summary>
    public RpcNotifyAttribute()
    {
    }

    /// <summary>
    /// 使用显式默认前缀。传入 <c>""</c> 表示默认无前缀。
    /// </summary>
    /// <param name="name">默认前缀；与短方法名组合为 <c>{name}.{短名}</c>。</param>
    public RpcNotifyAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>显式默认前缀；为 null 时生成器使用类型短名。</summary>
    public string? Name { get; }
}
