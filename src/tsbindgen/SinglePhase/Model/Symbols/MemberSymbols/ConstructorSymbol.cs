using System.Collections.Immutable;
using tsbindgen.SinglePhase.Renaming;

namespace tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

/// <summary>
/// Represents a constructor.
/// IMMUTABLE record.
/// </summary>
public sealed record ConstructorSymbol
{
    /// <summary>
    /// Stable identifier for this constructor.
    /// </summary>
    public required MemberStableId StableId { get; init; }

    /// <summary>
    /// Constructor parameters.
    /// </summary>
    public required ImmutableArray<ParameterSymbol> Parameters { get; init; }

    /// <summary>
    /// True if this is a static constructor (type initializer).
    /// </summary>
    public bool IsStatic { get; init; }

    /// <summary>
    /// Visibility.
    /// </summary>
    public required Visibility Visibility { get; init; }

    /// <summary>
    /// Documentation.
    /// </summary>
    public string? Documentation { get; init; }
}
