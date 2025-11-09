using tsbindgen.Core.Canon;
using tsbindgen.Core.Diagnostics;
using tsbindgen.Core.Intern;
using tsbindgen.Core.Naming;
using tsbindgen.Core.Policy;
using tsbindgen.SinglePhase.Renaming;

namespace tsbindgen.SinglePhase;

/// <summary>
/// Central context for the entire build process.
/// Provides access to all shared services: policy, renamer, diagnostics, etc.
/// Immutable after construction.
/// </summary>
public sealed class BuildContext
{
    /// <summary>
    /// Generation policy controlling all behavior.
    /// </summary>
    public required GenerationPolicy Policy { get; init; }

    /// <summary>
    /// Central naming authority - all identifiers flow through this.
    /// </summary>
    public required SymbolRenamer Renamer { get; init; }

    /// <summary>
    /// String interning for reducing allocations.
    /// </summary>
    public required StringInterner Interner { get; init; }

    /// <summary>
    /// Diagnostic collection for errors/warnings.
    /// </summary>
    public required DiagnosticBag Diagnostics { get; init; }

    /// <summary>
    /// Optional logger for progress/debugging.
    /// </summary>
    public Action<string>? Logger { get; init; }

    /// <summary>
    /// Enable verbose logging (all categories).
    /// </summary>
    public bool VerboseLogging { get; init; }

    /// <summary>
    /// Specific log categories to enable (if VerboseLogging is false).
    /// Null means no category-based filtering.
    /// </summary>
    public HashSet<string>? LogCategories { get; init; }

    /// <summary>
    /// Create a new BuildContext with default services.
    /// </summary>
    public static BuildContext Create(
        GenerationPolicy? policy = null,
        Action<string>? logger = null,
        bool verboseLogging = false,
        HashSet<string>? logCategories = null)
    {
        policy ??= PolicyDefaults.Create();

        var renamer = new SymbolRenamer();
        var interner = new StringInterner();
        var diagnostics = new DiagnosticBag();

        // Apply explicit overrides from policy
        renamer.ApplyExplicitOverrides(policy.Renaming.ExplicitMap);

        // Adopt style transforms from policy (types and members can have different casing)
        var typeTransform = policy.Emission.TypeNameTransform != NameTransformStrategy.None
            ? new Func<string, string>(name => NameTransform.Apply(name, policy.Emission.TypeNameTransform))
            : static name => name; // Identity

        var memberTransform = policy.Emission.MemberNameTransform != NameTransformStrategy.None
            ? new Func<string, string>(name => NameTransform.Apply(name, policy.Emission.MemberNameTransform))
            : static name => name; // Identity

        renamer.AdoptTypeStyleTransform(typeTransform);
        renamer.AdoptMemberStyleTransform(memberTransform);

        return new BuildContext
        {
            Policy = policy,
            Renamer = renamer,
            Interner = interner,
            Diagnostics = diagnostics,
            Logger = logger,
            VerboseLogging = verboseLogging,
            LogCategories = logCategories
        };
    }

    /// <summary>
    /// Log a message with category (if logger is configured and category is enabled).
    /// </summary>
    /// <param name="category">Log category (e.g., "ViewPlanner", "PhaseGate")</param>
    /// <param name="message">Log message</param>
    public void Log(string category, string message)
    {
        // Only log if:
        // 1. VerboseLogging is enabled (log everything), OR
        // 2. LogCategories contains this category
        if (Logger != null && (VerboseLogging || (LogCategories?.Contains(category) ?? false)))
        {
            Logger($"[{category}] {message}");
        }
    }

    /// <summary>
    /// Create a canonical signature for a method.
    /// </summary>
    public string CanonicalizeMethod(
        string methodName,
        IReadOnlyList<string> parameterTypes,
        string returnType)
    {
        return SignatureCanonicalizer.CanonicalizeMethod(methodName, parameterTypes, returnType);
    }

    /// <summary>
    /// Create a canonical signature for a property.
    /// </summary>
    public string CanonicalizeProperty(
        string propertyName,
        IReadOnlyList<string> indexParameterTypes,
        string propertyType)
    {
        return SignatureCanonicalizer.CanonicalizeProperty(
            propertyName,
            indexParameterTypes,
            propertyType);
    }

    /// <summary>
    /// Intern a string.
    /// </summary>
    public string Intern(string value) => Interner.Intern(value);
}
