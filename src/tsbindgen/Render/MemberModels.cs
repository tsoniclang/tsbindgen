using tsbindgen.Snapshot;

namespace tsbindgen.Render;

/// <summary>
/// Constructor model.
/// </summary>
public sealed record ConstructorModel(
    string Visibility,
    IReadOnlyList<ParameterModel> Parameters);

/// <summary>
/// Method model with both CLR and TS names.
/// </summary>
public sealed record MethodModel(
    string ClrName,
    string TsAlias,
    bool IsStatic,
    bool IsVirtual,
    bool IsOverride,
    bool IsAbstract,
    string Visibility,
    IReadOnlyList<GenericParameterModel> GenericParameters,
    IReadOnlyList<ParameterModel> Parameters,
    TypeReferenceModel ReturnType,
    MemberBinding Binding);

/// <summary>
/// Property model with both CLR and TS names.
/// </summary>
public sealed record PropertyModel(
    string ClrName,
    string TsAlias,
    string ClrType,
    string TsType,
    bool IsReadonly,
    bool IsStatic,
    bool IsVirtual,
    bool IsOverride,
    string Visibility,
    MemberBinding Binding,
    string? ContractTsType);  // If not null, wrap TsType with Covariant<TsType, ContractTsType>

/// <summary>
/// Field model with both CLR and TS names.
/// </summary>
public sealed record FieldModel(
    string ClrName,
    string TsAlias,
    string ClrType,
    string TsType,
    bool IsReadonly,
    bool IsStatic,
    string Visibility,
    MemberBinding Binding);

/// <summary>
/// Event model with both CLR and TS names.
/// </summary>
public sealed record EventModel(
    string ClrName,
    string TsAlias,
    string ClrType,
    string TsType,
    bool IsStatic,
    string Visibility,
    MemberBinding Binding);

/// <summary>
/// Parameter model with both CLR and TS types.
/// </summary>
public sealed record ParameterModel(
    string Name,
    string ClrType,
    string TsType,
    ParameterKind Kind,
    bool IsOptional,
    string? DefaultValue,
    bool IsParams);
