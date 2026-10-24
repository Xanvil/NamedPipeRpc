using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Xanvil.NamedPipeRpc.Generators;

/// <summary>
/// 从 Roslyn 符号构建契约模型，并产出诊断。
/// </summary>
internal static class ContractModelBuilder
{
    public const string RpcRequestAttributeMetadataName = "Xanvil.NamedPipeRpc.RpcRequestAttribute";
    public const string RpcMethodAttributeMetadataName = "Xanvil.NamedPipeRpc.RpcMethodAttribute";
    public const string RpcNotifyAttributeMetadataName = "Xanvil.NamedPipeRpc.RpcNotifyAttribute";

    /// <summary>
    /// 尝试从带 [RpcRequest] 或 [RpcNotify] 的类型符号构建模型；失败时上报诊断并返回 null。
    /// </summary>
    public static ContractTypeModel? TryBuildContract(
        INamedTypeSymbol type,
        AttributeData contractAttribute,
        ContractRole role,
        Action<Diagnostic> report)
    {
        if (HasAttribute(type, RpcRequestAttributeMetadataName)
            && HasAttribute(type, RpcNotifyAttributeMetadataName))
        {
            report(Diagnostic.Create(
                RpcContractDiagnostics.MixedRequestAndNotifyNotSupported,
                type.Locations.FirstOrDefault() ?? Location.None,
                type.Name));
            return null;
        }

        if (role == ContractRole.Notify && type.TypeKind != TypeKind.Interface)
        {
            report(Diagnostic.Create(
                RpcContractDiagnostics.NotifyContractMustBeInterface,
                type.Locations.FirstOrDefault() ?? Location.None,
                type.Name));
            return null;
        }

        if (type.TypeKind is not (TypeKind.Interface or TypeKind.Class))
            return null;

        if (type.IsStatic)
            return null;

        if (type.IsGenericType || type.TypeParameters.Length > 0)
        {
            report(Diagnostic.Create(
                RpcContractDiagnostics.GenericContractNotSupported,
                type.Locations.FirstOrDefault() ?? Location.None,
                type.ToDisplayString()));
            return null;
        }

        // 契约默认前缀（可为空字符串）
        string contractPrefix = GetContractDefaultPrefix(type, contractAttribute);
        string hintName = SanitizeHintName(type);
        string containingNamespace = GetContainingNamespace(type);
        var methods = ImmutableArray.CreateBuilder<ContractMethodModel>();
        bool hasError = false;

        foreach (var member in type.GetMembers())
        {
            // 接口/类上的属性、事件等：第一版不支持
            if (member is IPropertySymbol or IEventSymbol)
            {
                if (SymbolEqualityComparer.Default.Equals(member.ContainingType, type))
                {
                    report(Diagnostic.Create(
                        RpcContractDiagnostics.UnsupportedMember,
                        member.Locations.FirstOrDefault() ?? Location.None,
                        type.Name,
                        member.Name));
                    hasError = true;
                }
                continue;
            }

            if (member is not IMethodSymbol method)
                continue;

            if (method.MethodKind != MethodKind.Ordinary)
                continue;

            // 类上只暴露 public 实例方法；接口方法本身即契约
            if (type.TypeKind == TypeKind.Class)
            {
                if (method.IsStatic || method.DeclaredAccessibility != Accessibility.Public)
                    continue;
            }

            var built = TryBuildMethod(method, contractPrefix, hintName, role, report);
            if (built is null)
            {
                hasError = true;
                continue;
            }

            methods.Add(built);
        }

        if (hasError)
            return null;

        var kind = type.TypeKind == TypeKind.Interface ? ContractKind.Interface : ContractKind.Class;
        string? proxyTypeName = kind == ContractKind.Interface
            ? DeriveProxyTypeName(type.Name)
            : null;

        return new ContractTypeModel(
            Kind: kind,
            Role: role,
            TypeFullName: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", ""),
            TypeDisplay: type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            HintName: hintName,
            ContractName: contractPrefix,
            ContainingNamespace: containingNamespace,
            ProxyTypeName: proxyTypeName,
            Methods: methods.ToImmutable(),
            Accessibility: GetAccessibilityKeyword(type));
    }

    static ContractMethodModel? TryBuildMethod(
        IMethodSymbol method,
        string contractPrefix,
        string hintName,
        ContractRole role,
        Action<Diagnostic> report)
    {
        Location loc = method.Locations.FirstOrDefault() ?? Location.None;

        if (!TryGetTaskResponseType(method.ReturnType, out var responseTypeDisplay))
        {
            report(Diagnostic.Create(
                RpcContractDiagnostics.MethodMustReturnTask,
                loc,
                method.Name,
                method.ReturnType.ToDisplayString()));
            return null;
        }

        if (role == ContractRole.Notify && responseTypeDisplay is not null)
        {
            report(Diagnostic.Create(
                RpcContractDiagnostics.NotifyMustReturnNonGenericTask,
                loc,
                method.Name,
                method.ReturnType.ToDisplayString()));
            return null;
        }

        var businessParams = ImmutableArray.CreateBuilder<ContractParameterModel>();
        bool hasCt = false;
        string? ctName = null;

        foreach (var p in method.Parameters)
        {
            if (p.RefKind != RefKind.None)
            {
                report(Diagnostic.Create(
                    RpcContractDiagnostics.RefLikeParameterNotSupported,
                    p.Locations.FirstOrDefault() ?? loc,
                    method.Name));
                return null;
            }

            if (IsCancellationToken(p.Type))
            {
                hasCt = true;
                ctName = p.Name;
                continue;
            }

            businessParams.Add(new ContractParameterModel(
                p.Name,
                p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)));
        }

        string rpcName = ResolveRpcName(method, contractPrefix);

        if (Encoding.UTF8.GetByteCount(rpcName) > 255)
        {
            report(Diagnostic.Create(
                RpcContractDiagnostics.RpcMethodNameTooLong,
                loc,
                method.Name,
                rpcName));
            return null;
        }

        // 一个业务参数时载荷就是该参数本身，不看参数名。
        // 两个及以上才生成参数包，字段名等于参数名。缺少的字段使用类型默认值。
        string? argsTypeName = null;
        if (businessParams.Count >= 2)
        {
            argsTypeName = $"{hintName}_{SanitizeIdentifier(method.Name)}_Args";
        }

        return new ContractMethodModel(
            MethodName: method.Name,
            RpcName: rpcName,
            Parameters: businessParams.ToImmutable(),
            HasCancellationToken: hasCt,
            CancellationTokenParameterName: ctName,
            ResponseTypeDisplay: role == ContractRole.Notify ? null : responseTypeDisplay,
            ArgsTypeName: argsTypeName);
    }

    /// <summary>
    /// 解析管道 RPC 名：整段 Name &gt; Prefix 覆盖 + 短名 &gt; 契约默认前缀 + 短名。
    /// </summary>
    static string ResolveRpcName(IMethodSymbol method, string contractPrefix)
    {
        AttributeData? rpcMethodAttr = null;
        foreach (var attr in method.GetAttributes())
        {
            if (IsAttribute(attr, RpcMethodAttributeMetadataName))
            {
                rpcMethodAttr = attr;
                break;
            }
        }

        if (rpcMethodAttr is not null)
        {
            // 构造函数整段名优先
            if (rpcMethodAttr.ConstructorArguments.Length == 1
                && rpcMethodAttr.ConstructorArguments[0].Value is string fullName)
            {
                return fullName;
            }
        }

        string shortName = StripAsyncSuffix(method.Name);
        string prefix = contractPrefix;

        if (rpcMethodAttr is not null && TryGetExplicitPrefix(rpcMethodAttr, out string? methodPrefix))
            prefix = methodPrefix!;

        if (string.IsNullOrEmpty(prefix))
            return shortName;

        return prefix + "." + shortName;
    }

    /// <summary>
    /// 判断 [RpcMethod] 是否通过命名参数设置了 Prefix（含空字符串）。
    /// </summary>
    static bool TryGetExplicitPrefix(AttributeData attr, out string? prefix)
    {
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key != "Prefix")
                continue;

            // Prefix = null 极少见；按空前缀处理
            if (named.Value.IsNull)
            {
                prefix = "";
                return true;
            }

            if (named.Value.Value is string s)
            {
                prefix = s;
                return true;
            }

            prefix = "";
            return true;
        }

        prefix = null;
        return false;
    }

    /// <summary>
    /// 去掉尾缀 Async（仅当名称以 Async 结尾且长度大于 5）。
    /// </summary>
    internal static string StripAsyncSuffix(string methodName)
    {
        const string suffix = "Async";
        if (methodName.Length > suffix.Length
            && methodName.EndsWith(suffix, StringComparison.Ordinal))
        {
            return methodName.Substring(0, methodName.Length - suffix.Length);
        }

        return methodName;
    }

    /// <summary>
    /// 契约默认前缀：显式 Name（可为 ""）或类型短名。
    /// </summary>
    static string GetContractDefaultPrefix(INamedTypeSymbol type, AttributeData attr)
    {
        // [RpcRequest("Name")] / [RpcNotify("")]：构造函数参数存在即采用（含空字符串）。
        // Name 只有 getter，不能写成命名属性赋值。
        if (attr.ConstructorArguments.Length == 1
            && attr.ConstructorArguments[0].Value is string explicitName)
        {
            return explicitName;
        }

        return type.Name;
    }

    /// <summary>
    /// ISample → Sample；非 I+大写 则 {Name}Proxy。
    /// </summary>
    internal static string DeriveProxyTypeName(string typeName)
    {
        if (typeName.Length >= 2
            && typeName[0] == 'I'
            && char.IsUpper(typeName[1]))
        {
            return typeName.Substring(1);
        }

        return typeName + "Proxy";
    }

    static string GetContainingNamespace(INamedTypeSymbol type)
    {
        if (type.ContainingNamespace is null || type.ContainingNamespace.IsGlobalNamespace)
            return "";
        return type.ContainingNamespace.ToDisplayString();
    }

    internal static bool HasAttribute(ISymbol symbol, string metadataName)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (IsAttribute(attr, metadataName))
                return true;
        }
        return false;
    }

    internal static bool IsAttribute(AttributeData attr, string metadataName)
    {
        if (attr.AttributeClass is null)
            return false;
        var full = attr.AttributeClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
            .Replace("global::", "");
        return full == metadataName;
    }

    static bool TryGetTaskResponseType(ITypeSymbol returnType, out string? responseTypeDisplay)
    {
        responseTypeDisplay = null;

        if (returnType is not INamedTypeSymbol named)
            return false;

        // Task
        if (named.Name == "Task"
            && named.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks"
            && named.TypeArguments.Length == 0)
        {
            responseTypeDisplay = null; // Unit
            return true;
        }

        // Task<T>
        if (named.Name == "Task"
            && named.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks"
            && named.TypeArguments.Length == 1)
        {
            responseTypeDisplay = named.TypeArguments[0]
                .ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return true;
        }

        return false;
    }

    static bool IsCancellationToken(ITypeSymbol type)
    {
        return type.Name == "CancellationToken"
            && type.ContainingNamespace?.ToDisplayString() == "System.Threading";
    }

    static string GetAccessibilityKeyword(INamedTypeSymbol type)
    {
        return type.DeclaredAccessibility switch
        {
            Accessibility.Public => "public",
            Accessibility.Internal => "internal",
            Accessibility.ProtectedAndInternal => "private protected",
            Accessibility.ProtectedOrInternal => "protected internal",
            Accessibility.Protected => "protected",
            Accessibility.Private => "private",
            _ => "internal",
        };
    }

    static string SanitizeHintName(INamedTypeSymbol type)
    {
        var sb = new StringBuilder();
        foreach (var part in type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                     .Replace("global::", "")
                     .Split(new[] { '.', '+' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (sb.Length > 0)
                sb.Append('_');
            sb.Append(SanitizeIdentifier(part));
        }
        return sb.ToString();
    }

    static string SanitizeIdentifier(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.Length == 0 ? "Type" : sb.ToString();
    }
}

