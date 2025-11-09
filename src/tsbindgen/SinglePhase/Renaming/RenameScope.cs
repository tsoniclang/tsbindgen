namespace tsbindgen.SinglePhase.Renaming;

/// <summary>
/// Represents a naming scope where identifiers must be unique.
/// Scopes prevent unrelated symbols from colliding.
/// </summary>
public abstract record RenameScope
{
    /// <summary>
    /// Human-readable scope identifier for debugging.
    /// </summary>
    public required string ScopeKey { get; init; }
}

/// <summary>
/// Scope for top-level types in a namespace.
/// </summary>
public sealed record NamespaceScope : RenameScope
{
    /// <summary>
    /// Full namespace name (e.g., "System.Collections.Generic").
    /// </summary>
    public required string Namespace { get; init; }

    /// <summary>
    /// True for internal scope, false for facade scope.
    /// Internal and facade are treated as separate scopes to allow clean facade names.
    /// </summary>
    public required bool IsInternal { get; init; }

    /// <summary>
    /// Internal constructor - use ScopeFactory.NamespacePublic/Internal instead.
    /// </summary>
    internal NamespaceScope() { }
}

/// <summary>
/// Scope for members within a type.
/// Static and instance members use separate sub-scopes.
/// </summary>
public sealed record TypeScope : RenameScope
{
    /// <summary>
    /// Full CLR type name (e.g., "System.Collections.Generic.List`1").
    /// </summary>
    public required string TypeFullName { get; init; }

    /// <summary>
    /// True for static member sub-scope, false for instance member sub-scope.
    /// Separating these prevents false collision detection between static and instance members.
    /// </summary>
    public required bool IsStatic { get; init; }

    /// <summary>
    /// Internal constructor - use ScopeFactory.ClassBase/ClassSurface/ViewBase/ViewSurface instead.
    /// </summary>
    internal TypeScope() { }
}

/// <summary>
/// Scope for import aliases (optional, used when aliases are exported).
/// </summary>
public sealed record ImportAliasScope : RenameScope
{
    /// <summary>
    /// Target namespace being imported.
    /// </summary>
    public required string TargetNamespace { get; init; }

    /// <summary>
    /// Internal constructor - add factory method to ScopeFactory if needed.
    /// </summary>
    internal ImportAliasScope() { }
}
