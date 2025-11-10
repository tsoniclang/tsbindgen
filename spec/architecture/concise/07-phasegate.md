# Phase: PhaseGate - Final Validation Before Emit

**Location:** `src/tsbindgen/SinglePhase/Plan/PhaseGate.cs`

**Purpose:** Final validation gatekeeper. Comprehensive checks before emission. If validation fails (ErrorCount > 0), emit phase is SKIPPED.

**Input:** Fully transformed `SymbolGraph` (post-Shape, post-Plan)
**Output:** `ValidationContext` with error/warning/info counts

---

## Validation Execution Order

PhaseGate runs 20+ validation modules in strict order:

### 1. Core Validations (8 functions)
1. `ValidateTypeNames()` - TsEmitName presence, duplicates, reserved words
2. `ValidateMemberNames()` - TsEmitName presence, overload collisions
3. `ValidateGenericParameters()` - Names, constraints, narrowing
4. `ValidateInterfaceConformance()` - Structural conformance, signature assignability
5. `ValidateInheritance()` - Base class validity, no cycles
6. `ValidateEmitScopes()` - Count ClassSurface/ViewOnly (metrics only)
7. `ValidateImports()` - No circular dependencies
8. `ValidatePolicyCompliance()` - Policy constraints (extensible)

### 2. Hardening Validations (M1-M18)
- **M1:** `Views.Validate()` - ViewOnly orphans, SourceInterface matching
- **M2:** `Names.ValidateFinalNames()` - Renamer coverage
- **M3:** `Names.ValidateAliases()` - Import alias collisions
- **M4:** `Names.ValidateIdentifiers()` - Reserved word sanitization (PG_ID_001)
- **M5:** `Names.ValidateOverloadCollisions()` - Erased signature duplicates (PG_OV_001)
- **M6:** `Views.ValidateIntegrity()` - 3 hard rules (PG_VIEW_001-003)
- **M7:** `Constraints.EmitDiagnostics()` - Constraint losses (PG_CT_001-002)
- **M8:** `Views.ValidateMemberScoping()` - View collisions, shadowing (PG_NAME_003-004)
- **M9:** `Scopes.ValidateEmitScopeInvariants()` - No dual-role, no SourceInterface on ClassSurface (PG_INT_002-003)
- **M10:** `Scopes.ValidateScopeMismatches()` - Scope key format, scope/EmitScope matching (PG_SCOPE_003-004)
- **M11:** `Names.ValidateClassSurfaceUniqueness()` - Post-dedup duplicates (PG_NAME_005)
- **M12:** `Finalization.Validate()` - FINAL sweep: all symbols have placement/naming (PG_FIN_001-009)
- **M13:** `Types.ValidatePrinterNameConsistency()` - TypeNameResolver matches Renamer (PG_PRINT_001)
- **M14:** `Types.ValidateTypeMapCompliance()` - No unsupported special forms (PG_TYPEMAP_001)
- **M15:** `Types.ValidateExternalTypeResolution()` - All external types resolvable (PG_LOAD_001)
- **M16:** `ImportExport.ValidatePublicApiSurface()` - No internal types in public API (PG_API_001-002)
- **M17:** `ImportExport.ValidateImportCompleteness()` - All foreign types imported (PG_IMPORT_001)
- **M18:** `ImportExport.ValidateExportCompleteness()` - All imported types exported by source (PG_EXPORT_001)

### 3. Reporting
- Print diagnostic summary table (by code)
- Write `.tests/phasegate-diagnostics.txt` (human-readable)
- Write `.tests/phasegate-summary.json` (machine-readable, CI/trending)
- Fail build if `ErrorCount > 0`

---

## Validation Modules

### 1. Context.cs - Validation context and reporting
- `ValidationContext.RecordDiagnostic()` - Records diagnostic, updates counters
- `WriteSummaryJson()` - Writes `.tests/phasegate-summary.json`
- `WriteDiagnosticsFile()` - Writes `.tests/phasegate-diagnostics.txt`
- `GetDiagnosticDescription()` - Maps all 43 codes to descriptions

### 2. Core.cs - Core validations
1. `ValidateTypeNames()` - TsEmitName, duplicates, reserved words
2. `ValidateMemberNames()` - TsEmitName, overload collisions
3. `ValidateGenericParameters()` - Names, constraints, narrowing
4. `ValidateInterfaceConformance()` - Conformance, TS-assignability, covariance
5. `ValidateInheritance()` - Base class validity, cycles
6. `ValidateEmitScopes()` - ClassSurface/ViewOnly counts
7. `ValidateImports()` - Circular dependency detection
8. `ValidatePolicyCompliance()` - Extensible checks

### 3. Views.cs - View validation
- `Validate()` - ViewOnly orphans, SourceInterface matching, indexers
- `ValidateIntegrity()` - 3 HARD RULES: PG_VIEW_001 (non-empty), PG_VIEW_002 (unique), PG_VIEW_003 (valid name)
- `ValidateMemberScoping()` - PG_NAME_003 (no view collisions), PG_NAME_004 (no shadowing)

### 4. Names.cs - Name validation
- `ValidateFinalNames()` - Renamer coverage, duplicates
- `ValidateAliases()` - Import alias collisions
- `ValidateIdentifiers()` - PG_ID_001: reserved word sanitization [TBG719]
- `ValidateOverloadCollisions()` - PG_OV_001: erased signature duplicates [TBG213]
- `ValidateClassSurfaceUniqueness()` - PG_NAME_005: post-dedup check [TBG105]

### 5. Scopes.cs - Scope validation
- `ValidateEmitScopeInvariants()` - PG_INT_002 (no dual-role), PG_INT_003 (no SourceInterface on ClassSurface)
- `ValidateScopeMismatches()` - PG_SCOPE_003 (well-formed keys), PG_SCOPE_004 (scope/EmitScope match)

### 6. Finalization.cs - FINAL sweep: all symbols validated
**Validate() [PG_FIN_001-009]:**
- PG_FIN_001: EmitScope explicit [TBG710]
- PG_FIN_002: ViewOnly has SourceInterface + exactly one view [TBG711]
- PG_FIN_003: Members have final names in scopes [TBG712]
- PG_FIN_004: Types have final names in namespace [TBG713]
- PG_FIN_005: No empty views [TBG714]
- PG_FIN_006: No duplicate view membership [TBG715]
- PG_FIN_007: No dual-role clashes [TBG716]
- PG_FIN_008: Required views exist [TBG717]
- PG_FIN_009: Identifiers sanitized [TBG718]

### 7. Constraints.cs - Constraint validation
- `EmitDiagnostics()` - PG_CT_001 (non-benign loss [TBG406]), PG_CT_002 (constructor loss with override [TBG407])

### 8. Types.cs - Type system validation
- `ValidatePrinterNameConsistency()` - PG_PRINT_001: TypeNameResolver matches Renamer [TBG530]
- `ValidateTypeMapCompliance()` - PG_TYPEMAP_001: no unsupported forms [TBG870]
- `ValidateExternalTypeResolution()` - PG_LOAD_001: external types resolvable [TBG880]

### 9. ImportExport.cs - Import/export validation
- `ValidatePublicApiSurface()` - PG_API_001 (no internal types [TBG860]), PG_API_002 (constraints [TBG861])
- `ValidateImportCompleteness()` - PG_IMPORT_001: foreign types imported [TBG850]
- `ValidateExportCompleteness()` - PG_EXPORT_001: imported types exported [TBG851]

### 10. Shared.cs - Utilities
- `IsTypeScriptReservedWord()`, `IsValidTypeScriptIdentifier()`, `IsRepresentableConformanceBreak()`, `GetPropertyTypeString()`, `IsInterfaceInGraph()`

---

## All Diagnostic Codes (43 Total)

| Code | Name | Severity | PhaseGate Rule |
|------|------|----------|----------------|
| **0xx - Resolution** |
| TBG001 | UnresolvedType | ERROR | |
| TBG002 | UnresolvedGenericParameter | ERROR | |
| TBG003 | UnresolvedConstraint | ERROR | |
| **1xx - Naming** |
| TBG100 | NameConflictUnresolved | ERROR/WARN | |
| TBG101 | AmbiguousOverload | WARNING | Core.ValidateMemberNames |
| TBG102 | DuplicateMember | ERROR | Core.ValidateTypeNames |
| TBG103 | ViewMemberCollisionInViewScope | ERROR | PG_NAME_003 |
| TBG104 | ViewMemberEqualsClassSurface | ERROR | PG_NAME_004 |
| TBG105 | DuplicatePropertyNamePostDedup | ERROR | PG_NAME_005 |
| TBG120 | ReservedWordUnsanitized | WARNING | Core.ValidateTypeNames |
| **2xx - Hierarchy** |
| TBG200 | DiamondInheritance | INFO/ERROR | |
| TBG201 | CircularInheritance | ERROR | Core.ValidateInheritance |
| TBG202 | InterfaceNotFound | WARNING | Views.Validate |
| TBG203 | StructuralConformanceFailure | WARNING | Core.ValidateInterfaceConformance |
| TBG204 | StaticSideInheritanceIssue | WARNING | |
| TBG205 | InterfaceMethodNotAssignable | ERROR | PG_IFC_001 |
| TBG211 | OverloadUnified | INFO | |
| TBG212 | OverloadUnresolvable | WARNING | |
| TBG213 | DuplicateErasedSurfaceSignature | ERROR | PG_OV_001 |
| **3xx - TS Compatibility** |
| TBG300 | PropertyCovarianceUnsupported | WARNING | |
| TBG301 | StaticSideVariance | WARNING | |
| TBG302 | IndexerConflict | ERROR | Views.Validate |
| TBG310 | CovarianceSummary | INFO | Core.ValidateInterfaceConformance |
| **4xx - Policy/Constraints** |
| TBG400 | PolicyViolation | ERROR | |
| TBG401 | UnsatisfiableConstraint | ERROR | |
| TBG402 | UnsupportedConstraintMerge | ERROR | |
| TBG403 | IncompatibleConstraints | ERROR | |
| TBG404 | UnrepresentableConstraint | WARNING | Core.ValidateGenericParameters |
| TBG405 | ValidationFailed | ERROR | Core (multiple) |
| TBG406 | NonBenignConstraintLoss | ERROR | PG_CT_001 |
| TBG407 | ConstructorConstraintLoss | WARNING | PG_CT_002 |
| TBG410 | ConstraintNarrowing | INFO | Core.ValidateGenericParameters |
| **5xx - Renaming/Views** |
| TBG500 | RenameConflict | ERROR | |
| TBG501 | ExplicitOverrideNotApplied | WARNING | |
| TBG510 | ViewCoverageMismatch | ERROR | Views.Validate |
| TBG511 | EmptyView | ERROR | PG_VIEW_001 |
| TBG512 | DuplicateViewForInterface | ERROR | PG_VIEW_002 |
| TBG513 | InvalidViewPropertyName | ERROR | PG_VIEW_003 |
| TBG530 | TypeNamePrinterRenamerMismatch | ERROR | PG_PRINT_001 |
| **6xx - Metadata** |
| TBG600 | MissingMetadataToken | ERROR | |
| TBG601 | BindingAmbiguity | ERROR | |
| **7xx - PhaseGate Core** |
| TBG702 | MemberInBothClassAndView | ERROR | PG_INT_002 |
| TBG703 | ClassSurfaceMemberHasSourceInterface | ERROR | PG_INT_003 |
| TBG710 | MissingEmitScopeOrIllegalCombo | ERROR | PG_FIN_001 |
| TBG711 | ViewOnlyWithoutExactlyOneExplicitView | ERROR | PG_FIN_002 |
| TBG712 | EmittingMemberMissingFinalName | ERROR | PG_FIN_003 |
| TBG713 | EmittingTypeMissingFinalName | ERROR | PG_FIN_004 |
| TBG714 | InvalidOrEmptyViewMembership | ERROR | PG_FIN_005 |
| TBG715 | DuplicateViewMembership | ERROR | PG_FIN_006 |
| TBG716 | ClassViewDualRoleClash | ERROR | PG_FIN_007 |
| TBG717 | RequiredViewMissingForInterface | ERROR | PG_FIN_008 |
| TBG718 | PostSanitizerUnsanitizedIdentifier | ERROR | PG_FIN_009 |
| TBG719 | PostSanitizerUnsanitizedReservedIdentifier | ERROR | PG_ID_001 |
| TBG720 | MalformedScopeKey | ERROR | PG_SCOPE_003 |
| TBG721 | ScopeKindMismatchWithEmitScope | ERROR | PG_SCOPE_004 |
| **8xx - Import/Export/TypeMap** |
| TBG850 | MissingImportForForeignType | ERROR | PG_IMPORT_001 |
| TBG851 | ImportedTypeNotExported | ERROR | PG_EXPORT_001 |
| TBG852 | InvalidImportModulePath | ERROR | PG_MODULE_001 |
| TBG853 | FacadeImportsMustUseInternalIndex | ERROR | PG_FACADE_001 |
| TBG860 | PublicApiReferencesNonEmittedType | ERROR | PG_API_001 |
| TBG861 | GenericConstraintReferencesNonEmittedType | ERROR | PG_API_002 |
| TBG862 | PublicApiReferencesNonPublicType | ERROR | PG_API_004 |
| TBG870 | UnsupportedClrSpecialForm | ERROR | PG_TYPEMAP_001 |
| **9xx - Assembly Load** |
| TBG880 | UnresolvedExternalType | ERROR | PG_LOAD_001 |
| TBG881 | MixedPublicKeyTokenForSameName | ERROR | PG_LOAD_002 |
| TBG882 | VersionDriftForSameIdentity | ERROR | PG_LOAD_003 |
| TBG883 | RetargetableOrContentTypeAssemblyRef | ERROR | PG_LOAD_004 |

---

## Output Files

### `.tests/phasegate-summary.json`
**Purpose:** Machine-readable summary for CI/trending

**Format:**
```json
{
  "timestamp": "2025-11-10 12:34:56",
  "totals": {
    "errors": 0,
    "warnings": 12,
    "info": 241,
    "sanitized_names": 47
  },
  "diagnostic_counts_by_code": {
    "TBG103": 0,
    "TBG104": 0,
    "TBG203": 12,
    "TBG310": 241,
    ...
  }
}
```

### `.tests/phasegate-diagnostics.txt`
**Purpose:** Human-readable detailed diagnostics

**Sections:**
1. Summary (totals)
2. Interface Conformance Issues (detailed breakdown per type)
3. All Diagnostics (chronological)

---

## Validation Flow

```
SymbolGraph (fully transformed)
    ↓
┌─────────────────────────────┐
│ 1. Core Validations (8)    │
│    - Types, members, etc.   │
└─────────────────────────────┘
    ↓
┌─────────────────────────────┐
│ 2. Hardening (M1-M18)       │
│    - Views, scopes, etc.    │
└─────────────────────────────┘
    ↓
┌─────────────────────────────┐
│ 3. Reporting                │
│    - Write files            │
│    - Check ErrorCount       │
└─────────────────────────────┘
    ↓
ErrorCount > 0 ?
├─ YES → SKIP EMIT (FAIL BUILD)
└─ NO  → PROCEED TO EMIT
```

---

## Key Invariants Enforced

**Names:**
- All types/members have final names from Renamer
- No collisions within scopes
- Reserved words sanitized (underscore suffix)
- No duplicate erased signatures

**Scopes:**
- All members have explicit EmitScope (not Unspecified)
- No dual-role members (ClassSurface + ViewOnly)
- ClassSurface members have NO SourceInterface
- ViewOnly members MUST have SourceInterface + belong to exactly one view
- Scope keys well-formed and match EmitScope

**Views (3 HARD RULES):**
- PG_VIEW_001: Non-empty (≥1 ViewMember)
- PG_VIEW_002: Unique target (no duplicate views)
- PG_VIEW_003: Valid/sanitized view property name

**Types:**
- TypeNameResolver matches Renamer
- No unsupported special forms
- All external types resolvable

**Import/Export:**
- All foreign types imported
- All imported types exported by source
- Public API doesn't expose internal types

**Finalization (PG_FIN_001-009):**
- EVERY symbol has proper placement and naming
- NOTHING escapes without validation
