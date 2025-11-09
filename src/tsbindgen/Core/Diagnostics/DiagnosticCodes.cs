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

    // PhaseGate hardening - Identifier validation
    public const string PG_ID_001 = "PG_ID_001"; // Reserved identifier not sanitized

    // PhaseGate hardening - Name collisions
    public const string PG_NAME_003 = "PG_NAME_003"; // View member collision within view scope
    public const string PG_NAME_004 = "PG_NAME_004"; // View member name equals class surface name
    public const string PG_NAME_005 = "PG_NAME_005"; // Duplicate property name on class surface (post-deduplication)

    // PhaseGate hardening - Overload collisions
    public const string PG_OV_001 = "PG_OV_001"; // Duplicate erased signature in surface

    // PhaseGate hardening - View integrity
    public const string PG_VIEW_001 = "PG_VIEW_001"; // Empty view (no members)
    public const string PG_VIEW_002 = "PG_VIEW_002"; // Duplicate view for same interface
    public const string PG_VIEW_003 = "PG_VIEW_003"; // Invalid/unsanitized view property name

    // PhaseGate hardening - Constraint mismatches
    public const string PG_CT_001 = "PG_CT_001"; // Non-benign constraint loss (ERROR)
    public const string PG_CT_002 = "PG_CT_002"; // Constructor constraint loss (WARNING with override flag)

    // PhaseGate hardening - Interface conformance
    public const string PG_IFC_001 = "PG_IFC_001"; // Interface method not assignable (erased)

    // PhaseGate hardening - EmitScope integrity
    public const string PG_INT_002 = "PG_INT_002"; // Member appears in both ClassSurface and ViewOnly
    public const string PG_INT_003 = "PG_INT_003"; // ClassSurface member has SourceInterface set

    // PhaseGate hardening - Finalization sweep (comprehensive pre-emit validation)
    public const string PG_FIN_001 = "PG_FIN_001"; // Member has no final placement (EmitScope null/unspecified) or illegal combo (ClassSurface + ViewOnly)
    public const string PG_FIN_002 = "PG_FIN_002"; // Member marked ViewOnly but not found in exactly one ExplicitView
    public const string PG_FIN_003 = "PG_FIN_003"; // Emitting member missing final name in the correct scope
    public const string PG_FIN_004 = "PG_FIN_004"; // Emitting type missing final name in namespace scope
    public const string PG_FIN_005 = "PG_FIN_005"; // Empty/invalid view (zero members, or members not belonging to this type)
    public const string PG_FIN_006 = "PG_FIN_006"; // Duplicate membership (member appears in >1 view of the same type)
    public const string PG_FIN_007 = "PG_FIN_007"; // Class/View dual-role clash (same StableId ends up on both surfaces)
    public const string PG_FIN_008 = "PG_FIN_008"; // Interface requires a view (per conformance audit) but the type has no matching view
    public const string PG_FIN_009 = "PG_FIN_009"; // Param/TypeParam/Property/View property still unsanitized (post-sanitizer audit)

    // PhaseGate hardening - Scope validation (Step 6)
    public const string PG_SCOPE_003 = "PG_SCOPE_003"; // Empty/malformed scope key
    public const string PG_SCOPE_004 = "PG_SCOPE_004"; // Scope kind doesn't match EmitScope
}
