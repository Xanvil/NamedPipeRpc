using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Xanvil.NamedPipeRpc.Generators;

/// <summary>
/// 契约转换结果：模型（可空）与诊断列表。
/// </summary>
internal sealed record ContractBuildResult(
    ContractTypeModel? Model,
    ImmutableArray<Diagnostic> Diagnostics);
