using System.Text.Json.Serialization;

namespace tsbindgen.Snapshot;

/// <summary>
/// Root snapshot model for an assembly.
/// Contains complete reflection IR after all transforms.
/// </summary>
public sealed record AssemblySnapshot(
    string AssemblyName,
    string AssemblyPath,
    string Timestamp,
    IReadOnlyList<NamespaceSnapshot> Namespaces);

/// <summary>
/// Snapshot of a single namespace within an assembly.
/// </summary>
public sealed record NamespaceSnapshot(
    string ClrName,
    IReadOnlyList<TypeSnapshot> Types,
    IReadOnlyList<DependencyRef> Imports,
    IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Snapshot of a type (class, interface, enum, delegate, struct).
/// </summary>
public sealed record TypeSnapshot(
    string ClrName,
    string FullName,
    TypeKind Kind,
    bool IsStatic,
    bool IsSealed,
    bool IsAbstract,
    string Visibility,
    IReadOnlyList<GenericParameter> GenericParameters,
    TypeReference? BaseType,
    IReadOnlyList<TypeReference> Implements,
    MemberCollection Members,
    BindingInfo Binding)
{
    // Enum-specific properties
    public string? UnderlyingType { get; init; }
    public IReadOnlyList<EnumMember>? EnumMembers { get; init; }

    // Delegate-specific properties
    public IReadOnlyList<ParameterSnapshot>? DelegateParameters { get; init; }
    public TypeReference? DelegateReturnType { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    StaticNamespace
}

/// <summary>
/// Collection of all members grouped by kind.
/// </summary>
public sealed record MemberCollection(
    IReadOnlyList<ConstructorSnapshot> Constructors,
    IReadOnlyList<MethodSnapshot> Methods,
    IReadOnlyList<PropertySnapshot> Properties,
    IReadOnlyList<FieldSnapshot> Fields,
    IReadOnlyList<EventSnapshot> Events);

/// <summary>
/// Snapshot of a method.
/// </summary>
public sealed record MethodSnapshot(
    string ClrName,
    bool IsStatic,
    bool IsVirtual,
    bool IsOverride,
    bool IsAbstract,
    string Visibility,
    IReadOnlyList<GenericParameter> GenericParameters,
    IReadOnlyList<ParameterSnapshot> Parameters,
    TypeReference ReturnType,
    MemberBinding Binding);

/// <summary>
/// Snapshot of a property.
/// </summary>
public sealed record PropertySnapshot(
    string ClrName,
    TypeReference Type,
    bool IsReadOnly,
    bool IsStatic,
    bool IsVirtual,
    bool IsOverride,
    string Visibility,
    MemberBinding Binding)
{
    /// <summary>
    /// Contract type if this property has covariant return type (more specific than base/interface).
    /// If not null, the property type should be wrapped with Covariant&lt;Type, ContractType&gt;.
    /// </summary>
    public TypeReference? ContractType { get; init; }
};

/// <summary>
/// Snapshot of a constructor.
/// </summary>
public sealed record ConstructorSnapshot(
    string Visibility,
    IReadOnlyList<ParameterSnapshot> Parameters);

/// <summary>
/// Snapshot of a field.
/// </summary>
public sealed record FieldSnapshot(
    string ClrName,
    TypeReference Type,
    bool IsReadOnly,
    bool IsStatic,
    string Visibility,
    MemberBinding Binding);

/// <summary>
/// Snapshot of an event.
/// </summary>
public sealed record EventSnapshot(
    string ClrName,
    string ClrType,
    bool IsStatic,
    string Visibility,
    MemberBinding Binding);

/// <summary>
/// Type reference - recursive structure for CLR types.
/// Fully parsed with namespace, type name, generic arguments, arrays, and pointers.
/// </summary>
public sealed record TypeReference(
    string? Namespace,                           // "System" (null if no namespace or primitive)
    string TypeName,                             // "Nullable_1", "Int32", "T" (generic param)
    IReadOnlyList<TypeReference> GenericArgs,    // Recursive: generic type arguments
    int ArrayRank,                                // 0 = not array, 1 = [], 2 = [][], etc.
    int PointerDepth,                            // 0 = not pointer, 1 = *, 2 = **, etc.
    string? Assembly = null)                     // Assembly alias for cross-assembly refs
{
    /// <summary>
    /// Gets the full CLR type string representation.
    /// Reconstructs the original type string from parsed components.
    /// </summary>
    public string ClrType
    {
        get
        {
            var sb = new System.Text.StringBuilder();

            // Namespace.TypeName
            if (Namespace != null)
            {
                sb.Append(Namespace);
                sb.Append('.');
            }
            sb.Append(TypeName);

            // Generic arguments
            if (GenericArgs.Count > 0)
            {
                sb.Append('<');
                for (int i = 0; i < GenericArgs.Count; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(GenericArgs[i].ClrType);
                }
                sb.Append('>');
            }

            // Pointers
            for (int i = 0; i < PointerDepth; i++)
            {
                sb.Append('*');
            }

            // Arrays
            for (int i = 0; i < ArrayRank; i++)
            {
                sb.Append("[]");
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Creates a simple TypeReference (no generics, arrays, pointers).
    /// </summary>
    public static TypeReference CreateSimple(string? ns, string typeName, string? assembly = null)
    {
        return new TypeReference(ns, typeName, Array.Empty<TypeReference>(), 0, 0, assembly);
    }

    /// <summary>
    /// Creates a generic TypeReference.
    /// </summary>
    public static TypeReference CreateGeneric(string? ns, string typeName, IReadOnlyList<TypeReference> genericArgs, string? assembly = null)
    {
        return new TypeReference(ns, typeName, genericArgs, 0, 0, assembly);
    }

    /// <summary>
    /// Creates an array TypeReference from an element type.
    /// </summary>
    public static TypeReference CreateArray(TypeReference elementType, int rank)
    {
        return new TypeReference(elementType.Namespace, elementType.TypeName, elementType.GenericArgs, rank, elementType.PointerDepth, elementType.Assembly);
    }

    /// <summary>
    /// Creates a pointer TypeReference from an element type (increases pointer depth by 1).
    /// </summary>
    public static TypeReference CreatePointer(TypeReference elementType)
    {
        return new TypeReference(elementType.Namespace, elementType.TypeName, elementType.GenericArgs, elementType.ArrayRank, elementType.PointerDepth + 1, elementType.Assembly);
    }
};

/// <summary>
/// Generic parameter with constraints and variance.
/// </summary>
public sealed record GenericParameter(
    string Name,
    IReadOnlyList<string> Constraints,
    Variance Variance);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Variance
{
    None,
    In,  // Contravariant
    Out  // Covariant
}

/// <summary>
/// Method/constructor parameter.
/// </summary>
public sealed record ParameterSnapshot(
    string Name,
    TypeReference Type,
    ParameterKind Kind,
    bool IsOptional,
    string? DefaultValue,
    bool IsParams);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ParameterKind
{
    In,
    Ref,
    Out,
    Params
}

/// <summary>
/// Enum member (name + value).
/// </summary>
public sealed record EnumMember(
    string Name,
    long Value);

/// <summary>
/// Cross-namespace dependency reference.
/// </summary>
public sealed record DependencyRef(
    string Namespace,
    string Assembly);

/// <summary>
/// Diagnostic (warning or error).
/// </summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>
/// Binding info for a type.
/// </summary>
public sealed record BindingInfo(
    string Assembly,
    string Type);

/// <summary>
/// Binding info for a member.
/// </summary>
public sealed record MemberBinding(
    string Assembly,
    string Type,
    string Member);

/// <summary>
/// Assembly manifest listing all processed assemblies.
/// </summary>
public sealed record AssemblyManifest(
    IReadOnlyList<AssemblyManifestEntry> Assemblies);

/// <summary>
/// Entry in assembly manifest.
/// </summary>
public sealed record AssemblyManifestEntry(
    string Name,
    string Snapshot,
    int TypeCount,
    int NamespaceCount);
