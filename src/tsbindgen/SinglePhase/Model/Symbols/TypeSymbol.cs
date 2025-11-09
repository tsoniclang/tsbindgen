using System.Collections.Immutable;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Model.Symbols;

/// <summary>
/// Represents a type (class, struct, interface, enum, delegate).
/// Loaded during reflection, transformed during shaping.
/// IMMUTABLE - use wither helpers to create modified copies.
/// </summary>
public sealed record TypeSymbol
{
    /// <summary>
    /// Stable identifier for this type.
    /// </summary>
    public required TypeStableId StableId { get; init; }

    /// <summary>
    /// CLR full name (e.g., "System.Collections.Generic.List`1").
    /// </summary>
    public required string ClrFullName { get; init; }

    /// <summary>
    /// Simple CLR name without namespace (e.g., "List`1").
    /// </summary>
    public required string ClrName { get; init; }

    /// <summary>
    /// TypeScript emit name (set by NameApplication after reservation).
    /// Example: "List_1" for List`1, "Console$Error" for nested types.
    /// </summary>
    public string TsEmitName { get; init; } = "";

    /// <summary>
    /// Accessibility level.
    /// </summary>
    public Accessibility Accessibility { get; init; } = Accessibility.Public;

    /// <summary>
    /// Namespace (e.g., "System.Collections.Generic").
    /// </summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// Kind of type.
    /// </summary>
    public required TypeKind Kind { get; init; }

    /// <summary>
    /// Generic arity (0 for non-generic).
    /// </summary>
    public required int Arity { get; init; }

    /// <summary>
    /// Generic parameters declared by this type.
    /// </summary>
    public required ImmutableArray<GenericParameterSymbol> GenericParameters { get; init; }

    /// <summary>
    /// Base type (null for interfaces, System.Object, System.ValueType).
    /// </summary>
    public TypeReference? BaseType { get; init; }

    /// <summary>
    /// Implemented interfaces.
    /// </summary>
    public required ImmutableArray<TypeReference> Interfaces { get; init; }

    /// <summary>
    /// All members (methods, properties, fields, events, constructors).
    /// </summary>
    public required TypeMembers Members { get; init; }

    /// <summary>
    /// Nested types.
    /// </summary>
    public required ImmutableArray<TypeSymbol> NestedTypes { get; init; }

    /// <summary>
    /// True if this is a value type (struct, enum).
    /// </summary>
    public required bool IsValueType { get; init; }

    /// <summary>
    /// True if this is abstract.
    /// </summary>
    public bool IsAbstract { get; init; }

    /// <summary>
    /// True if this is sealed.
    /// </summary>
    public bool IsSealed { get; init; }

    /// <summary>
    /// True if this is static (C# static class).
    /// </summary>
    public bool IsStatic { get; init; }

    /// <summary>
    /// Declaring type (for nested types).
    /// </summary>
    public TypeSymbol? DeclaringType { get; init; }

    /// <summary>
    /// XML documentation comment (if available).
    /// </summary>
    public string? Documentation { get; init; }

    /// <summary>
    /// Explicit interface views planned for this type.
    /// Populated by ViewPlanner in Shape phase.
    /// Empty for interfaces and static classes.
    /// </summary>
    public ImmutableArray<Shape.ViewPlanner.ExplicitView> ExplicitViews { get; init; } = ImmutableArray<Shape.ViewPlanner.ExplicitView>.Empty;

    // Wither helpers for pure transformations

    public TypeSymbol WithMembers(TypeMembers members) => this with { Members = members };

    public TypeSymbol WithAddedMethods(IEnumerable<MethodSymbol> methods) =>
        this with { Members = Members with { Methods = Members.Methods.AddRange(methods) } };

    public TypeSymbol WithRemovedProperties(Func<PropertySymbol, bool> predicate) =>
        this with { Members = Members with { Properties = Members.Properties.RemoveAll(new Predicate<PropertySymbol>(predicate)) } };

    public TypeSymbol WithAddedProperties(IEnumerable<PropertySymbol> properties) =>
        this with { Members = Members with { Properties = Members.Properties.AddRange(properties) } };

    public TypeSymbol WithRemovedMethods(Func<MethodSymbol, bool> predicate) =>
        this with { Members = Members with { Methods = Members.Methods.RemoveAll(new Predicate<MethodSymbol>(predicate)) } };

    public TypeSymbol WithAddedFields(IEnumerable<FieldSymbol> fields) =>
        this with { Members = Members with { Fields = Members.Fields.AddRange(fields) } };

    public TypeSymbol WithTsEmitName(string tsEmitName) => this with { TsEmitName = tsEmitName };

    public TypeSymbol WithExplicitViews(ImmutableArray<Shape.ViewPlanner.ExplicitView> views) =>
        this with { ExplicitViews = views };
}

public enum TypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    StaticNamespace // For static classes
}

/// <summary>
/// Generic parameter declared by a type or method.
/// IMMUTABLE record.
/// </summary>
public sealed record GenericParameterSymbol
{
    /// <summary>
    /// Unique identifier for this parameter.
    /// </summary>
    public required GenericParameterId Id { get; init; }

    /// <summary>
    /// Parameter name (e.g., "T", "TKey").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Position in the parameter list.
    /// </summary>
    public required int Position { get; init; }

    /// <summary>
    /// Constraints on this parameter (resolved by ConstraintCloser).
    /// </summary>
    public required ImmutableArray<TypeReference> Constraints { get; init; }

    /// <summary>
    /// Raw CLR constraint types from reflection (resolved in Shape phase).
    /// Populated during Load, resolved by ConstraintCloser.
    /// </summary>
    public System.Type[]? RawConstraintTypes { get; init; }

    /// <summary>
    /// Variance (Covariant, Contravariant, None).
    /// </summary>
    public Variance Variance { get; init; }

    /// <summary>
    /// Special constraints (struct, class, new()).
    /// </summary>
    public GenericParameterConstraints SpecialConstraints { get; init; }
}

public enum Variance
{
    None,
    Covariant,       // out T
    Contravariant    // in T
}

[Flags]
public enum GenericParameterConstraints
{
    None = 0,
    ReferenceType = 1,      // class
    ValueType = 2,          // struct
    DefaultConstructor = 4, // new()
    NotNullable = 8         // notnull
}

/// <summary>
/// Container for all members of a type.
/// IMMUTABLE - use 'with' expressions to create modified copies.
/// </summary>
public sealed record TypeMembers
{
    public required ImmutableArray<MethodSymbol> Methods { get; init; }
    public required ImmutableArray<PropertySymbol> Properties { get; init; }
    public required ImmutableArray<FieldSymbol> Fields { get; init; }
    public required ImmutableArray<EventSymbol> Events { get; init; }
    public required ImmutableArray<ConstructorSymbol> Constructors { get; init; }

    public static readonly TypeMembers Empty = new()
    {
        Methods = ImmutableArray<MethodSymbol>.Empty,
        Properties = ImmutableArray<PropertySymbol>.Empty,
        Fields = ImmutableArray<FieldSymbol>.Empty,
        Events = ImmutableArray<EventSymbol>.Empty,
        Constructors = ImmutableArray<ConstructorSymbol>.Empty
    };
}

/// <summary>
/// Accessibility level.
/// </summary>
public enum Accessibility
{
    Public,
    Protected,
    Internal,
    ProtectedInternal,
    Private,
    PrivateProtected
}
