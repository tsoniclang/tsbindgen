using tsbindgen.Snapshot;

namespace tsbindgen.Render;

/// <summary>
/// Constructor model.
/// </summary>
public sealed record ConstructorModel(
    string Visibility,
    IReadOnlyList<ParameterModel> Parameters);

/// <summary>
/// Method model (Phase 3).
/// TypeScript name computed on-demand from ClrName + config via AnalysisContext.
/// </summary>
public sealed record MethodModel(
    string ClrName,
    bool IsStatic,
    bool IsVirtual,
    bool IsOverride,
    bool IsAbstract,
    string Visibility,
    IReadOnlyList<GenericParameterModel> GenericParameters,
    IReadOnlyList<ParameterModel> Parameters,
    TypeReference ReturnType,
    MemberBinding Binding)
{
    /// <summary>
    /// If not null, indicates this is a synthetic method added by analysis passes.
    /// </summary>
    public SyntheticOverloadInfo? SyntheticOverload { get; init; }
}

/// <summary>
/// Property model (Phase 3).
/// TypeScript name computed on-demand from ClrName + config via AnalysisContext.
/// </summary>
public sealed record PropertyModel(
    string ClrName,
    TypeReference Type,
    bool IsReadonly,
    bool IsStatic,
    bool IsVirtual,
    bool IsOverride,
    string Visibility,
    MemberBinding Binding,
    TypeReference? ContractType)  // If not null, property has covariant return type
{
    public bool SyntheticMember { get; init; }  // If true, added by analysis pass

    /// <summary>
    /// True if this property is a C# indexer (Item property with index parameters).
    /// When true, should be emitted as method-pair with index parameters.
    /// </summary>
    public bool IsIndexer { get; init; }

    /// <summary>
    /// Index parameters for indexers. Empty for non-indexer properties.
    /// Example: Item[int index] → [(name: "index", type: System.Int32)]
    /// </summary>
    public IReadOnlyList<ParameterModel> IndexerParameters { get; init; } = Array.Empty<ParameterModel>();
}

/// <summary>
/// Field model (Phase 3).
/// TypeScript name computed on-demand from ClrName + config via AnalysisContext.
/// </summary>
public sealed record FieldModel(
    string ClrName,
    TypeReference Type,
    bool IsReadonly,
    bool IsStatic,
    string Visibility,
    MemberBinding Binding);

/// <summary>
/// Event model (Phase 3).
/// TypeScript name computed on-demand from ClrName + config via AnalysisContext.
/// </summary>
public sealed record EventModel(
    string ClrName,
    TypeReference Type,
    bool IsStatic,
    string Visibility,
    MemberBinding Binding)
{
    public bool SyntheticMember { get; init; }  // If true, added by analysis pass
}

/// <summary>
/// Parameter model.
/// </summary>
public sealed record ParameterModel(
    string Name,
    TypeReference Type,
    ParameterKind Kind,
    bool IsOptional,
    string? DefaultValue,
    bool IsParams);
