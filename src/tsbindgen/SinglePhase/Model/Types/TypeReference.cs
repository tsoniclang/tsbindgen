namespace tsbindgen.SinglePhase.Model.Types;

/// <summary>
/// Represents a reference to a type in the CLR type system.
/// Immutable, structural equality.
/// Converted from System.Type during reflection.
/// </summary>
public abstract record TypeReference
{
    /// <summary>
    /// Kind of type reference.
    /// </summary>
    public abstract TypeReferenceKind Kind { get; }
}

public enum TypeReferenceKind
{
    /// <summary>
    /// Named type (class, struct, interface, enum, delegate).
    /// </summary>
    Named,

    /// <summary>
    /// Generic type parameter (T, TKey, TValue, etc.).
    /// </summary>
    GenericParameter,

    /// <summary>
    /// Array type (T[], T[,], etc.).
    /// </summary>
    Array,

    /// <summary>
    /// Pointer type (T*, T**, etc.).
    /// </summary>
    Pointer,

    /// <summary>
    /// ByRef type (ref T, out T).
    /// </summary>
    ByRef,

    /// <summary>
    /// Nested type (Outer.Inner).
    /// </summary>
    Nested,

    /// <summary>
    /// Placeholder type (internal - used to break recursion cycles).
    /// Should never appear in final output; emits 'any' with diagnostic.
    /// </summary>
    Placeholder
}

/// <summary>
/// Reference to a named type (class, struct, interface, enum, delegate).
/// </summary>
public sealed record NamedTypeReference : TypeReference
{
    public override TypeReferenceKind Kind => TypeReferenceKind.Named;

    /// <summary>
    /// Assembly name where the type is defined.
    /// </summary>
    public required string AssemblyName { get; init; }

    /// <summary>
    /// Full CLR type name (e.g., "System.Collections.Generic.List`1").
    /// </summary>
    public required string FullName { get; init; }

    /// <summary>
    /// Namespace (e.g., "System.Collections.Generic").
    /// </summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// Simple type name without namespace (e.g., "List`1").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Generic arity (0 for non-generic types).
    /// </summary>
    public required int Arity { get; init; }

    /// <summary>
    /// Type arguments for constructed generic types.
    /// Empty for non-generic or open generic types.
    /// </summary>
    public required IReadOnlyList<TypeReference> TypeArguments { get; init; }

    /// <summary>
    /// True if this is a value type (struct, enum).
    /// </summary>
    public required bool IsValueType { get; init; }

    /// <summary>
    /// Pre-computed StableId for interface types (format: AssemblyName:FullName).
    /// Set at load time for interfaces to eliminate repeated computation.
    /// Null for non-interface types.
    /// </summary>
    public string? InterfaceStableId { get; init; }
}

/// <summary>
/// Reference to a generic type parameter.
/// </summary>
public sealed record GenericParameterReference : TypeReference
{
    public override TypeReferenceKind Kind => TypeReferenceKind.GenericParameter;

    /// <summary>
    /// Identifier for this generic parameter (includes declaring type and position).
    /// </summary>
    public required GenericParameterId Id { get; init; }

    /// <summary>
    /// Parameter name (e.g., "T", "TKey").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Position in the declaring type's generic parameter list.
    /// </summary>
    public required int Position { get; init; }

    /// <summary>
    /// Constraints on this parameter.
    /// </summary>
    public required IReadOnlyList<TypeReference> Constraints { get; init; }
}

/// <summary>
/// Reference to an array type.
/// </summary>
public sealed record ArrayTypeReference : TypeReference
{
    public override TypeReferenceKind Kind => TypeReferenceKind.Array;

    /// <summary>
    /// Element type.
    /// </summary>
    public required TypeReference ElementType { get; init; }

    /// <summary>
    /// Array rank (1 for T[], 2 for T[,], etc.).
    /// </summary>
    public required int Rank { get; init; }
}

/// <summary>
/// Reference to a pointer type.
/// </summary>
public sealed record PointerTypeReference : TypeReference
{
    public override TypeReferenceKind Kind => TypeReferenceKind.Pointer;

    /// <summary>
    /// Pointee type.
    /// </summary>
    public required TypeReference PointeeType { get; init; }

    /// <summary>
    /// Pointer depth (1 for T*, 2 for T**, etc.).
    /// </summary>
    public required int Depth { get; init; }
}

/// <summary>
/// Reference to a ByRef type (ref/out parameter).
/// </summary>
public sealed record ByRefTypeReference : TypeReference
{
    public override TypeReferenceKind Kind => TypeReferenceKind.ByRef;

    /// <summary>
    /// Referenced type.
    /// </summary>
    public required TypeReference ReferencedType { get; init; }
}

/// <summary>
/// Reference to a nested type.
/// </summary>
public sealed record NestedTypeReference : TypeReference
{
    public override TypeReferenceKind Kind => TypeReferenceKind.Nested;

    /// <summary>
    /// Declaring (outer) type.
    /// </summary>
    public required TypeReference DeclaringType { get; init; }

    /// <summary>
    /// Nested type name.
    /// </summary>
    public required string NestedName { get; init; }

    /// <summary>
    /// Full reference including all nesting levels.
    /// </summary>
    public required NamedTypeReference FullReference { get; init; }
}

/// <summary>
/// Placeholder type reference used to break recursion cycles during type graph construction.
/// Internal only - should never appear in final emitted output.
/// If it does appear, printers emit 'any' and a diagnostic warning.
/// </summary>
public sealed record PlaceholderTypeReference : TypeReference
{
    public override TypeReferenceKind Kind => TypeReferenceKind.Placeholder;

    /// <summary>
    /// Debug name for the type that would have caused infinite recursion.
    /// </summary>
    public required string DebugName { get; init; }
}
