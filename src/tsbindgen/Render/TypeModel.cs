using tsbindgen.Config;
using tsbindgen.Snapshot;

namespace tsbindgen.Render;

/// <summary>
/// Type model after normalization and analysis.
/// TypeScript identifier computed on-demand via AnalysisContext.GetTypeIdentifier().
/// </summary>
public sealed record TypeModel(
    string ClrName,
    TypeKind Kind,
    bool IsStatic,
    bool IsSealed,
    bool IsAbstract,
    string Visibility,
    IReadOnlyList<GenericParameterModel> GenericParameters,
    TypeReference? BaseType,
    IReadOnlyList<TypeReference> Implements,
    MemberCollectionModel Members,
    BindingInfo Binding,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<HelperDeclaration> Helpers,
    // Explicit interface views (for TS2416 covariance conflicts)
    IReadOnlyList<TypeReference>? ConflictingInterfaces = null,
    // Enum-specific
    string? UnderlyingType = null,
    IReadOnlyList<EnumMember>? EnumMembers = null,
    // Delegate-specific
    IReadOnlyList<ParameterModel>? DelegateParameters = null,
    TypeReference? DelegateReturnType = null)
{
    private string? _tsEmitName;

    /// <summary>
    /// TypeScript emit name for .d.ts declarations (uses dollar for nesting).
    /// Computed from TypeReference structure - no heuristics.
    /// Example: "Console$Error_1"
    /// </summary>
    public string TsEmitName => _tsEmitName ??= TsNaming.ForEmit(Binding.Type);

    /// <summary>
    /// Returns true if this type is a .NET value type (struct or enum).
    /// Value types should be branded with ValueType and struct interfaces.
    /// </summary>
    public bool IsValueType => Kind == TypeKind.Struct || Kind == TypeKind.Enum;
};

/// <summary>
/// Generic parameter with constraints.
/// Name used as-is in TypeScript (T, U, TKey, etc. - no transformation).
/// </summary>
public sealed record GenericParameterModel(
    string Name,
    IReadOnlyList<TypeReference> Constraints,
    Variance Variance);

/// <summary>
/// Collection of all members for a type.
/// </summary>
public sealed record MemberCollectionModel(
    IReadOnlyList<ConstructorModel> Constructors,
    IReadOnlyList<MethodModel> Methods,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<FieldModel> Fields,
    IReadOnlyList<EventModel> Events);
