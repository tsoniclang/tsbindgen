using System.Collections.Immutable;
using tsbindgen.SinglePhase.Renaming;

namespace tsbindgen.SinglePhase.Model.Symbols;

/// <summary>
/// Represents a namespace containing types.
/// Created during aggregation phase.
/// IMMUTABLE - use 'with' expressions to create modified copies.
/// </summary>
public sealed record NamespaceSymbol
{
    /// <summary>
    /// Namespace name (e.g., "System.Collections.Generic").
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// All types in this namespace.
    /// </summary>
    public required ImmutableArray<TypeSymbol> Types { get; init; }

    /// <summary>
    /// Stable identifier for this namespace.
    /// </summary>
    public required StableId StableId { get; init; }

    /// <summary>
    /// Assembly names contributing to this namespace.
    /// Multiple assemblies can contribute to the same namespace.
    /// </summary>
    public required ImmutableHashSet<string> ContributingAssemblies { get; init; }
}
