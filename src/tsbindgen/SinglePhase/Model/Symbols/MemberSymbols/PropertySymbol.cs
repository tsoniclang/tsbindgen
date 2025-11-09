using System.Collections.Immutable;
using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

/// <summary>
/// Represents a property member.
/// IMMUTABLE record.
/// </summary>
public sealed record PropertySymbol
{
    /// <summary>
    /// Stable identifier for this property.
    /// </summary>
    public required MemberStableId StableId { get; init; }

    /// <summary>
    /// CLR property name.
    /// </summary>
    public required string ClrName { get; init; }

    /// <summary>
    /// TypeScript emit name (set by NameApplication after reservation).
    /// </summary>
    public string TsEmitName { get; init; } = "";

    /// <summary>
    /// Property type.
    /// </summary>
    public required TypeReference PropertyType { get; init; }

    /// <summary>
    /// Index parameters (for indexers).
    /// Empty for normal properties.
    /// </summary>
    public required ImmutableArray<ParameterSymbol> IndexParameters { get; init; }

    /// <summary>
    /// True if this is an indexer.
    /// </summary>
    public bool IsIndexer => IndexParameters.Length > 0;

    /// <summary>
    /// True if this property has a getter.
    /// </summary>
    public required bool HasGetter { get; init; }

    /// <summary>
    /// True if this property has a setter.
    /// </summary>
    public required bool HasSetter { get; init; }

    /// <summary>
    /// True if this is a static property.
    /// </summary>
    public required bool IsStatic { get; init; }

    /// <summary>
    /// True if this is virtual.
    /// </summary>
    public bool IsVirtual { get; init; }

    /// <summary>
    /// True if this overrides a base property.
    /// </summary>
    public bool IsOverride { get; init; }

    /// <summary>
    /// True if this is abstract.
    /// </summary>
    public bool IsAbstract { get; init; }

    /// <summary>
    /// Visibility.
    /// </summary>
    public required Visibility Visibility { get; init; }

    /// <summary>
    /// Provenance.
    /// </summary>
    public required MemberProvenance Provenance { get; init; }

    /// <summary>
    /// Emit scope.
    /// </summary>
    public EmitScope EmitScope { get; init; } = EmitScope.ClassSurface;

    /// <summary>
    /// Documentation.
    /// </summary>
    public string? Documentation { get; init; }

    /// <summary>
    /// Source interface (for interface-sourced properties).
    /// </summary>
    public TypeReference? SourceInterface { get; init; }

    /// <summary>
    /// Create a new PropertySymbol with updated SourceInterface.
    /// Wither method for immutability.
    /// </summary>
    public PropertySymbol WithSourceInterface(TypeReference? sourceInterface)
    {
        return this with { SourceInterface = sourceInterface };
    }
}
