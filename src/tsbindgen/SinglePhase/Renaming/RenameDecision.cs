namespace tsbindgen.SinglePhase.Renaming;

/// <summary>
/// Records a single rename decision with full provenance.
/// Captures: what changed, why, how, and who decided.
/// </summary>
public sealed record RenameDecision
{
    /// <summary>
    /// The stable identifier for the symbol being renamed.
    /// </summary>
    public required StableId Id { get; init; }

    /// <summary>
    /// What the caller requested (post-style transform).
    /// Null if this is the original name with no transformation.
    /// </summary>
    public string? Requested { get; init; }

    /// <summary>
    /// The final resolved TypeScript identifier.
    /// </summary>
    public required string Final { get; init; }

    /// <summary>
    /// Original CLR logical name (pre-style transform, for traceability).
    /// </summary>
    public required string From { get; init; }

    /// <summary>
    /// Why this rename was needed.
    /// Examples: "NameTransform(CamelCase)", "HiddenNewConflict",
    /// "StaticSideNameCollision", "ExplicitUserOverride", "InterfaceSynthesis",
    /// "StructuralConformanceView", "ReturnTypeConflictNormalization"
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Which component made this decision.
    /// Examples: "HiddenMemberPlanner", "InterfaceSynthesis", "ImportPlanner",
    /// "StructuralConformance", "CLI"
    /// </summary>
    public required string DecisionSource { get; init; }

    /// <summary>
    /// How conflicts were resolved.
    /// Values: "None", "NumericSuffix", "FixedSuffix", "Error"
    /// </summary>
    public required string Strategy { get; init; }

    /// <summary>
    /// When NumericSuffix strategy applies, the numeric index (2, 3, 4...).
    /// </summary>
    public int? SuffixIndex { get; init; }

    /// <summary>
    /// Textual scope identifier for debugging.
    /// Examples: "System.Numerics.Vector`1", "System.Collections.Generic"
    /// </summary>
    public required string ScopeKey { get; init; }

    /// <summary>
    /// True if this member is static (important for static-side tracking).
    /// Null for type-level renames.
    /// </summary>
    public bool? IsStatic { get; init; }

    /// <summary>
    /// Optional human-readable note for complex decisions.
    /// </summary>
    public string? Note { get; init; }
}
