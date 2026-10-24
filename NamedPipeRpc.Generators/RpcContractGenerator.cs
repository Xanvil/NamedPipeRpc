using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Xanvil.NamedPipeRpc.Generators;

/// <summary>
/// 为 [RpcRequest] / [RpcNotify] 生成 Handler、Proxy 注册与出站代理。
/// </summary>
[Generator]
public sealed class RpcContractGenerator : IIncrementalGenerator
{
    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var requestResults = context.SyntaxProvider.ForAttributeWithMetadataName(
            ContractModelBuilder.RpcRequestAttributeMetadataName,
            predicate: static (node, _) => node is InterfaceDeclarationSyntax or ClassDeclarationSyntax,
            transform: static (ctx, _) => TransformContract(ctx, ContractRole.Request));

        var notifyResults = context.SyntaxProvider.ForAttributeWithMetadataName(
            ContractModelBuilder.RpcNotifyAttributeMetadataName,
            predicate: static (node, _) => node is InterfaceDeclarationSyntax or ClassDeclarationSyntax,
            transform: static (ctx, _) => TransformContract(ctx, ContractRole.Notify));

        var combined = requestResults.Collect().Combine(notifyResults.Collect());

        context.RegisterSourceOutput(combined, static (spc, data) =>
            Execute(spc, data.Left, data.Right));
    }

    static ContractBuildResult TransformContract(GeneratorAttributeSyntaxContext context, ContractRole role)
    {
        if (context.TargetSymbol is not INamedTypeSymbol type)
            return new ContractBuildResult(null, ImmutableArray<Diagnostic>.Empty);

        AttributeData? attr = context.Attributes.Length > 0 ? context.Attributes[0] : null;
        if (attr is null)
            return new ContractBuildResult(null, ImmutableArray<Diagnostic>.Empty);

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var model = ContractModelBuilder.TryBuildContract(type, attr, role, diagnostics.Add);
        return new ContractBuildResult(model, diagnostics.ToImmutable());
    }

    static void Execute(
        SourceProductionContext context,
        ImmutableArray<ContractBuildResult> requestResults,
        ImmutableArray<ContractBuildResult> notifyResults)
    {
        var results = requestResults.AddRange(notifyResults);
        foreach (var result in results)
        {
            foreach (var d in result.Diagnostics)
                context.ReportDiagnostic(d);
        }

        var contracts = results
            .Select(r => r.Model)
            .Where(m => m is not null)
            .Select(m => m!)
            .GroupBy(c => c.TypeFullName, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(c => c.TypeFullName, StringComparer.Ordinal)
            .ToImmutableArray();

        if (contracts.IsDefaultOrEmpty)
            return;

        foreach (var contract in contracts)
        {
            if (contract.Kind == ContractKind.Interface)
            {
                context.AddSource(
                    $"{contract.HintName}.RpcProxy.g.cs",
                    RpcContractEmitter.EmitInterfaceProxy(contract));
            }
            else
            {
                string argsSource = RpcContractEmitter.EmitClassArgsOnly(contract);
                if (!string.IsNullOrEmpty(argsSource))
                {
                    context.AddSource(
                        $"{contract.HintName}.RpcArgs.g.cs",
                        argsSource);
                }
            }
        }

        context.AddSource(
            "RpcGeneratedExtensions.g.cs",
            RpcContractEmitter.EmitExtensions(contracts));
    }
}
