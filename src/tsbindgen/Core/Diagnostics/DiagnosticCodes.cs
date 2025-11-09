namespace tsbindgen.Core.Diagnostics;

/// <summary>
/// Well-known diagnostic codes for categorization and filtering.
/// </summary>
public static class DiagnosticCodes
{
    // Resolution errors
    public const string UnresolvedType = "TBG001";
    public const string UnresolvedGenericParameter = "TBG002";
    public const string UnresolvedConstraint = "TBG003";

    // Naming conflicts
    public const string NameConflictUnresolved = "TBG100";
    public const string AmbiguousOverload = "TBG101";
    public const string DuplicateMember = "TBG102";
    public const string ReservedWordUnsanitized = "TBG120"; // Reserved word not sanitized

    // Overload unification
    public const string OverloadUnified = "TBG211"; // Info-only, counted
    public const string OverloadUnresolvable = "TBG212"; // Warning

    // Interface/inheritance issues
    public const string DiamondInheritanceDetected = "TBG200";
    public const string DiamondInheritanceConflict = "TBG200"; // Alias for compatibility
    public const string CircularInheritance = "TBG201";
    public const string InterfaceNotFound = "TBG202";
    public const string StructuralConformanceFailure = "TBG203";
    public const string StaticSideInheritanceIssue = "TBG204";

    // TypeScript compatibility
    public const string PropertyCovarianceUnsupported = "TBG300";
    public const string StaticSideVariance = "TBG301";
    public const string IndexerConflict = "TBG302";
    public const string CovarianceSummary = "TBG310"; // Info-only summary per type

    // Policy violations
    public const string PolicyViolation = "TBG400";
    public const string UnsatisfiableConstraint = "TBG401";
    public const string UnsupportedConstraintMerge = "TBG402";
    public const string IncompatibleConstraints = "TBG403";
    public const string UnrepresentableConstraint = "TBG404";
    public const string ValidationFailed = "TBG405";
    public const string ConstraintNarrowing = "TBG410"; // Warning when constraints are narrowed

    // Renaming issues
    public const string RenameConflict = "TBG500";
    public const string ExplicitOverrideNotApplied = "TBG501";
    public const string ViewCoverageMismatch = "TBG510"; // Error: view planning mismatch

    // Metadata issues
    public const string MissingMetadataToken = "TBG600";
    public const string BindingAmbiguity = "TBG601";
}
