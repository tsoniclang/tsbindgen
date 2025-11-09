namespace tsbindgen.Core.Policy;

/// <summary>
/// Central configuration controlling all generation behavior.
/// Immutable policy object passed throughout the pipeline.
/// </summary>
public sealed record GenerationPolicy
{
    public required InterfacePolicy Interfaces { get; init; }
    public required ClassPolicy Classes { get; init; }
    public required IndexerPolicy Indexers { get; init; }
    public required ConstraintPolicy Constraints { get; init; }
    public required EmissionPolicy Emission { get; init; }
    public required DiagnosticPolicy Diagnostics { get; init; }
    public required RenamingPolicy Renaming { get; init; }
    public required ModulesPolicy Modules { get; init; }
    public required StaticSidePolicy StaticSide { get; init; }
}

public sealed record InterfacePolicy
{
    /// <summary>
    /// If true, inline all ancestor interfaces (no extends chains).
    /// </summary>
    public required bool InlineAll { get; init; }

    /// <summary>
    /// How to handle diamond inheritance.
    /// </summary>
    public required DiamondResolutionStrategy DiamondResolution { get; init; }
}

public enum DiamondResolutionStrategy
{
    /// <summary>
    /// Emit all overloads from all paths.
    /// </summary>
    OverloadAll,

    /// <summary>
    /// Prefer the most derived version.
    /// </summary>
    PreferDerived,

    /// <summary>
    /// Error on diamonds.
    /// </summary>
    Error
}

public sealed record ClassPolicy
{
    /// <summary>
    /// Keep extends chains for classes (true) or flatten (false).
    /// </summary>
    public required bool KeepExtends { get; init; }

    /// <summary>
    /// Suffix for C# 'new' hidden members (default "_new").
    /// </summary>
    public required string HiddenMemberSuffix { get; init; }

    /// <summary>
    /// How to handle explicit interface implementations.
    /// </summary>
    public required ExplicitImplStrategy SynthesizeExplicitImpl { get; init; }
}

public enum ExplicitImplStrategy
{
    /// <summary>
    /// Synthesize members with view suffixes.
    /// </summary>
    SynthesizeWithSuffix,

    /// <summary>
    /// Emit As_IInterface properties for explicit views.
    /// </summary>
    EmitExplicitViews,

    /// <summary>
    /// Skip explicit implementations.
    /// </summary>
    Skip
}

public sealed record IndexerPolicy
{
    /// <summary>
    /// If true, emit single indexers as properties.
    /// </summary>
    public required bool EmitPropertyWhenSingle { get; init; }

    /// <summary>
    /// If true, emit multiple indexers as methods.
    /// </summary>
    public required bool EmitMethodsWhenMultiple { get; init; }

    /// <summary>
    /// Method name for indexer methods (default "Item").
    /// </summary>
    public required string MethodName { get; init; }
}

public sealed record ConstraintPolicy
{
    /// <summary>
    /// If true, enforce strict constraint closure (fail on unsatisfiable).
    /// </summary>
    public required bool StrictClosure { get; init; }

    /// <summary>
    /// How to merge multiple constraints on a generic parameter.
    /// </summary>
    public required ConstraintMergeStrategy MergeStrategy { get; init; }

    /// <summary>
    /// If true, allow constructor constraint (new()) loss and downgrade to WARNING (PG_CT_002).
    /// If false (default), constructor constraint loss is ERROR (PG_CT_001).
    /// Enables explicit opt-in for unsound TypeScript bindings where new() cannot be represented.
    /// </summary>
    public bool AllowConstructorConstraintLoss { get; init; } = false;
}

public enum ConstraintMergeStrategy
{
    /// <summary>
    /// Use intersection types (T & U).
    /// </summary>
    Intersection,

    /// <summary>
    /// Use union types (T | U).
    /// </summary>
    Union,

    /// <summary>
    /// Prefer leftmost constraint.
    /// </summary>
    PreferLeft
}

public sealed record EmissionPolicy
{
    /// <summary>
    /// Name transformation strategy for TYPE names (classes, interfaces, enums).
    /// Default: None (preserve PascalCase from C#).
    /// </summary>
    public NameTransformStrategy TypeNameTransform { get; init; } = NameTransformStrategy.None;

    /// <summary>
    /// Name transformation strategy for MEMBER names (methods, properties, fields).
    /// Default: CamelCase (TypeScript convention).
    /// </summary>
    public NameTransformStrategy MemberNameTransform { get; init; } = NameTransformStrategy.CamelCase;

    /// <summary>
    /// Sorting order for types and members.
    /// </summary>
    public required SortOrderStrategy SortOrder { get; init; }

    /// <summary>
    /// If true, emit XML doc comments as TSDoc.
    /// </summary>
    public required bool EmitDocComments { get; init; }
}

public enum NameTransformStrategy
{
    None,
    CamelCase,
    PascalCase
}

public enum SortOrderStrategy
{
    /// <summary>
    /// Alphabetical by name.
    /// </summary>
    Alphabetical,

    /// <summary>
    /// By kind, then name.
    /// </summary>
    ByKindThenName,

    /// <summary>
    /// Preserve CLR declaration order.
    /// </summary>
    DeclarationOrder
}

public sealed record DiagnosticPolicy
{
    /// <summary>
    /// Diagnostic codes that cause build failure.
    /// </summary>
    public required IReadOnlySet<string> FailOn { get; init; }

    /// <summary>
    /// Diagnostic codes that emit warnings.
    /// </summary>
    public required IReadOnlySet<string> WarnOn { get; init; }
}

public sealed record RenamingPolicy
{
    /// <summary>
    /// How to handle static member name collisions.
    /// </summary>
    public required ConflictStrategy StaticConflict { get; init; }

    /// <summary>
    /// How to handle C# 'new' hidden members.
    /// </summary>
    public required ConflictStrategy HiddenNew { get; init; }

    /// <summary>
    /// Explicit CLI-provided renames (CLRPath -> TargetName).
    /// </summary>
    public required IReadOnlyDictionary<string, string> ExplicitMap { get; init; }

    /// <summary>
    /// If true, allow renaming static members to resolve conflicts.
    /// This is a capability toggle - bindings will track static flags regardless.
    /// </summary>
    public required bool AllowStaticMemberRename { get; init; }
}

public enum ConflictStrategy
{
    /// <summary>
    /// Add numeric suffix (name2, name3, etc.).
    /// </summary>
    NumericSuffix,

    /// <summary>
    /// Add fixed disambiguating suffix.
    /// </summary>
    DisambiguatingSuffix,

    /// <summary>
    /// Fail build on conflict.
    /// </summary>
    Error
}

public sealed record ModulesPolicy
{
    /// <summary>
    /// If true, emit namespace outputs to subdirectories (System/Collections/Generic/).
    /// If false, use flat structure (System.Collections.Generic/).
    /// </summary>
    public required bool UseNamespaceDirectories { get; init; }

    /// <summary>
    /// If true, always generate import aliases to avoid name collisions.
    /// If false, use bare imports when safe.
    /// </summary>
    public required bool AlwaysAliasImports { get; init; }
}

public sealed record StaticSidePolicy
{
    /// <summary>
    /// Action to take when static-side inheritance issues are detected.
    /// Default: Analyze (just emit diagnostics, no renames).
    /// </summary>
    public required StaticSideAction Action { get; init; }
}

public enum StaticSideAction
{
    /// <summary>
    /// Analyze and emit diagnostics only (default).
    /// No behavior change - just warns about potential TS2417 errors.
    /// </summary>
    Analyze,

    /// <summary>
    /// Automatically rename conflicting static members.
    /// Uses Renamer to add suffixes to derived static members that conflict with base.
    /// Bindings track the renames for runtime correlation.
    /// </summary>
    AutoRename,

    /// <summary>
    /// Fail build when static-side conflicts are detected.
    /// </summary>
    Error
}
