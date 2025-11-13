namespace tsbindgen.Core.Diagnostics;

/// <summary>
/// Well-known diagnostic codes for categorization and filtering.
/// One scheme: TBG + 3 digits. Severity is not encoded in the code.
/// </summary>
public static class DiagnosticCodes
{
    // 0xx — Resolution / Binding
    public const string UnresolvedType                 = "TBG001";
    public const string UnresolvedGenericParameter     = "TBG002";
    public const string UnresolvedConstraint           = "TBG003";

    // 1xx — Naming / Conflicts
    public const string NameConflictUnresolved         = "TBG100";
    public const string AmbiguousOverload              = "TBG101";
    public const string DuplicateMember                = "TBG102";
    public const string ViewMemberCollisionInViewScope = "TBG103"; // PG_NAME_003
    public const string ViewMemberEqualsClassSurface   = "TBG104"; // PG_NAME_004
    public const string DuplicatePropertyNamePostDedup = "TBG105"; // PG_NAME_005
    public const string ReservedWordUnsanitized        = "TBG120";

    // 2xx — Overload & Hierarchy
    public const string DiamondInheritance             = "TBG200"; // single concept; use severity to distinguish "detected" vs "conflict"
    public const string CircularInheritance            = "TBG201";
    public const string InterfaceNotFound              = "TBG202";
    public const string StructuralConformanceFailure   = "TBG203";
    public const string StaticSideInheritanceIssue     = "TBG204";
    public const string InterfaceMethodNotAssignable   = "TBG205"; // PG_IFC_001
    public const string OverloadUnified                = "TBG211"; // info
    public const string OverloadUnresolvable           = "TBG212"; // warn
    public const string DuplicateErasedSurfaceSignature= "TBG213"; // PG_OV_001

    // 3xx — TS Compatibility
    public const string PropertyCovarianceUnsupported  = "TBG300";
    public const string StaticSideVariance             = "TBG301";
    public const string IndexerConflict                = "TBG302";
    public const string CovarianceSummary              = "TBG310"; // info

    // 4xx — Policy / Constraints
    public const string PolicyViolation                = "TBG400";
    public const string UnsatisfiableConstraint        = "TBG401";
    public const string UnsupportedConstraintMerge     = "TBG402";
    public const string IncompatibleConstraints        = "TBG403";
    public const string UnrepresentableConstraint      = "TBG404";
    public const string ValidationFailed               = "TBG405";
    public const string NonBenignConstraintLoss        = "TBG406"; // PG_CT_001
    public const string ConstructorConstraintLoss      = "TBG407"; // PG_CT_002 (often warn)
    public const string ConstraintNarrowing            = "TBG410"; // warn

    // 5xx — Renaming & Views
    public const string RenameConflict                 = "TBG500";
    public const string ExplicitOverrideNotApplied     = "TBG501";
    public const string ViewCoverageMismatch           = "TBG510";
    public const string EmptyView                      = "TBG511"; // PG_VIEW_001
    public const string DuplicateViewForInterface      = "TBG512"; // PG_VIEW_002
    public const string InvalidViewPropertyName        = "TBG513"; // PG_VIEW_003
    public const string TypeNamePrinterRenamerMismatch = "TBG530"; // PG_PRINT_001

    // 6xx — Metadata / Binding
    public const string MissingMetadataToken           = "TBG600";
    public const string BindingAmbiguity               = "TBG601";

    // 7xx — PhaseGate Core (scopes/finalization/scope-keys)
    public const string MemberInBothClassAndView       = "TBG702"; // PG_INT_002
    public const string ClassSurfaceMemberHasSourceInterface = "TBG703"; // PG_INT_003

    public const string MissingEmitScopeOrIllegalCombo = "TBG710"; // PG_FIN_001
    public const string ViewOnlyWithoutExactlyOneExplicitView = "TBG711"; // PG_FIN_002
    public const string EmittingMemberMissingFinalName = "TBG712"; // PG_FIN_003
    public const string EmittingTypeMissingFinalName   = "TBG713"; // PG_FIN_004
    public const string InvalidOrEmptyViewMembership   = "TBG714"; // PG_FIN_005
    public const string DuplicateViewMembership        = "TBG715"; // PG_FIN_006
    public const string ClassViewDualRoleClash         = "TBG716"; // PG_FIN_007
    public const string RequiredViewMissingForInterface= "TBG717"; // PG_FIN_008
    public const string PostSanitizerUnsanitizedIdentifier = "TBG718"; // PG_FIN_009
    public const string PostSanitizerUnsanitizedReservedIdentifier = "TBG719"; // PG_ID_001

    public const string MalformedScopeKey              = "TBG720"; // PG_SCOPE_003
    public const string ScopeKindMismatchWithEmitScope = "TBG721"; // PG_SCOPE_004

    // 8xx — Emission / Modules / TypeMap
    public const string MissingImportForForeignType    = "TBG850"; // PG_IMPORT_001
    public const string ImportedTypeNotExported        = "TBG851"; // PG_EXPORT_001
    public const string InvalidImportModulePath        = "TBG852"; // PG_MODULE_001
    public const string FacadeImportsMustUseInternalIndex = "TBG853"; // PG_FACADE_001
    public const string HeritageTypeOnlyImport         = "TBG854"; // PG_IMPORT_002 - Base class/interface imported as type-only (needs value import)
    public const string QualifiedExportPathInvalid     = "TBG855"; // PG_EXPORT_002 - Qualified name path doesn't exist in export structure

    public const string PublicApiReferencesNonEmittedType = "TBG860"; // PG_API_001
    public const string GenericConstraintReferencesNonEmittedType = "TBG861"; // PG_API_002
    public const string PublicApiReferencesNonPublicType = "TBG862"; // (your PG_API_004 variant)

    public const string UnsupportedClrSpecialForm      = "TBG870"; // PG_TYPEMAP_001 (pointers/byref/function-pointer)

    // 8Ax — Surface Naming Policy (CLR-name contract)
    public const string SurfaceNamePolicyMismatch      = "TBG8A1"; // PG_NAME_SURF_001 - Class member doesn't match interface member using CLR-name policy
    public const string NumericSuffixOnSurface         = "TBG8A2"; // PG_NAME_SURF_002 - Surface or view member ends with numeric suffix (equals2, etc.)

    // 8Px — Primitive Lifting / CLROf
    public const string PrimitiveGenericLiftMismatch   = "TBG8P1"; // PG_GENERIC_PRIM_LIFT_001 - Primitive type argument not covered by CLROf lifting rules

    // 9xx — Assembly Load
    public const string UnresolvedExternalType         = "TBG880"; // PG_LOAD_001
    public const string MixedPublicKeyTokenForSameName = "TBG881"; // PG_LOAD_002
    public const string VersionDriftForSameIdentity    = "TBG882"; // PG_LOAD_003
    public const string RetargetableOrContentTypeAssemblyRef = "TBG883"; // PG_LOAD_004
}
