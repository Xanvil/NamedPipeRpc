using System;

namespace Xanvil.NamedPipeRpc;

/// <summary>
/// 标记请求服务契约。本端用 <c>AddRequestHandler</c> 提供实现，用连接上的 <c>GetRequestProxy</c> 调用对方。
/// </summary>
/// <remarks>
/// 可标在接口或服务端类上。默认 RPC 名前缀为 <see cref="Name"/>（可为 <c>""</c>），未写则用类型短名。
/// 方法须返回 <c>Task</c> 或 <c>Task&lt;T&gt;</c>。与 <see cref="RpcNotifyAttribute"/> 不能标在同一类型上。
/// </remarks>
[AttributeUsage(AttributeTargets.Interface | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RpcRequestAttribute : Attribute
{
    /// <summary>使用类型短名作为默认 RPC 前缀。</summary>
    public RpcRequestAttribute()
    {
    }

    /// <summary>
    /// 使用显式默认前缀。传入 <c>""</c> 表示默认无前缀。
    /// </summary>
    /// <param name="name">默认前缀；与短方法名组合为 <c>{name}.{短名}</c>。</param>
    public RpcRequestAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>显式默认前缀；为 null 时生成器使用类型短名。</summary>
    public string? Name { get; }
}
