# Phase: PhaseGate - Comprehensive Validation

**Location:** `src/tsbindgen/SinglePhase/Plan/PhaseGate.cs`

**Purpose:** Final validation gatekeeper before emission. Performs comprehensive validation checks and policy enforcement. Acts as quality gate between Shape/Plan phases and Emit phase.

---

## Table of Contents

1. [PhaseGate Overview](#1-phasegate-overview)
2. [PhaseGate Structure](#2-phasegate-structure)
3. [Validation Modules](#3-validation-modules)
4. [All Diagnostic Codes](#4-all-diagnostic-codes-complete-reference)
5. [Validation Flow Diagram](#5-validation-flow-diagram)
6. [Diagnostic Output Files](#6-diagnostic-output-files)

---

## 1. PhaseGate Overview

### Purpose
PhaseGate is the **FINAL validation checkpoint** before the Emit phase. It validates that every symbol has proper placement, naming, and structure. Nothing should leak past this gate.

### When It Runs
**After all transformations, before Emit**
- After Shape phase (EmitScope assignment, ViewPlanner)
- After Plan phase (NameReservation, ImportPlanner)
- Before Emit phase (.d.ts and .metadata.json generation)

### What Happens If Validation Fails
**Emit phase is SKIPPED**
- Errors are reported with detailed diagnostics
- Summary JSON and diagnostics file are written to `.tests/` directory
- Build fails with `ValidationFailed` error

### Input
`SymbolGraph` - Fully transformed and named symbol graph containing:
- All types with final names from Renamer
- All members with EmitScope assignment
- All views with member grouping
- Import/export plan

### Output
`ValidationContext` - Contains:
- Error count (severity: ERROR)
- Warning count (severity: WARNING)
- Info count (severity: INFO)
- All diagnostics with codes
- Diagnostic counts by code (for trending)
- Interface conformance issues (detailed breakdown)

---

## 2. PhaseGate Structure

### PhaseGate.Validate() Orchestration

**File:** `src/tsbindgen/SinglePhase/Plan/PhaseGate.cs`

```csharp
public static void Validate(
    BuildContext ctx,
    SymbolGraph graph,
    ImportPlan imports,
    InterfaceConstraintFindings constraintFindings)
```

### Order of Validation Execution

PhaseGate runs **20+ validation modules** in strict order:

```
┌─────────────────────────────────────────────────────────────┐
│ 1. CORE VALIDATIONS (8 functions)                          │
├─────────────────────────────────────────────────────────────┤
│   1.1 ValidateTypeNames()                                   │
│   1.2 ValidateMemberNames()                                 │
│   1.3 ValidateGenericParameters()                           │
│   1.4 ValidateInterfaceConformance()                        │
│   1.5 ValidateInheritance()                                 │
│   1.6 ValidateEmitScopes()                                  │
│   1.7 ValidateImports()                                     │
│   1.8 ValidatePolicyCompliance()                            │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ 2. HARDENING VALIDATIONS (M1-M10)                          │
├─────────────────────────────────────────────────────────────┤
│   M1: Views.Validate()                    (ViewOnly orphans)│
│   M2: Names.ValidateFinalNames()          (Renamer coverage)│
│   M3: Names.ValidateAliases()             (Import aliases)  │
│   M4: Names.ValidateIdentifiers()         (Reserved words)  │
│   M5: Names.ValidateOverloadCollisions()  (Erased sigs)     │
│   M6: Views.ValidateIntegrity()           (3 hard rules)    │
│   M7: Constraints.EmitDiagnostics()       (Constraint loss) │
│   M8: Views.ValidateMemberScoping()       (Name collisions) │
│   M9: Scopes.ValidateEmitScopeInvariants() (Dual-role)     │
│   M10: Scopes.ValidateScopeMismatches()   (Scope keys)      │
│   M11: Names.ValidateClassSurfaceUniqueness() (Dedup check) │
│   M12: Finalization.Validate()            (PG_FIN_001-009)  │
│   M13: Types.ValidatePrinterNameConsistency() (PG_PRINT_001)│
│   M14: Types.ValidateTypeMapCompliance()  (PG_TYPEMAP_001)  │
│   M15: Types.ValidateExternalTypeResolution() (PG_LOAD_001) │
│   M16: ImportExport.ValidatePublicApiSurface() (PG_API_001/2)│
│   M17: ImportExport.ValidateImportCompleteness() (PG_IMPORT_001)│
│   M18: ImportExport.ValidateExportCompleteness() (PG_EXPORT_001)│
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ 3. REPORTING                                                │
├─────────────────────────────────────────────────────────────┤
│   3.1 Write diagnostic summary table (by code)              │
│   3.2 Write diagnostics file (.tests/phasegate-diagnostics.txt)│
│   3.3 Write summary JSON (.tests/phasegate-summary.json)    │
│   3.4 Fail build if errors > 0                              │
└─────────────────────────────────────────────────────────────┘
```

### ValidationContext: Diagnostic Recording

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/Context.cs`

```csharp
internal sealed class ValidationContext
{
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public List<string> Diagnostics { get; set; }
    public Dictionary<string, int> DiagnosticCountsByCode { get; set; }
    public int SanitizedNameCount { get; set; }
    public Dictionary<string, List<string>> InterfaceConformanceIssuesByType { get; set; }

    public void RecordDiagnostic(string code, string severity, string message)
    {
        // Tracks count by code
        // Updates severity counters
        // Adds to diagnostics list
    }
}
```

**WriteSummaryJson()** - Writes `.tests/phasegate-summary.json`:
```json
{
  "timestamp": "2025-11-10 12:00:00",
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
    ...
  }
}
```

**WriteDiagnosticsFile()** - Writes `.tests/phasegate-diagnostics.txt`:
```
================================================================================
PhaseGate Detailed Diagnostics
Generated: 2025-11-10 12:00:00
================================================================================

Summary:
  Errors: 0
  Warnings: 12
  Info: 241
  Sanitized identifiers: 47

--------------------------------------------------------------------------------
Interface Conformance Issues (12 types)
--------------------------------------------------------------------------------

System.Collections.Generic.List_1:
  Missing method Add from ICollection_1
  Method Clear from ICollection_1 has incompatible TS signature

...

--------------------------------------------------------------------------------
All Diagnostics
--------------------------------------------------------------------------------

ERROR: [TBG710] Type System.Foo has no EmitScope placement (still Unspecified)
WARNING: [TBG203] System.Bar has 3 interface conformance issues (see above)
INFO: [TBG310] System.Baz has 2 property covariance issues (TS doesn't support)
...
```

**GetDiagnosticDescription()** - Maps ALL 40+ diagnostic codes to descriptions:
```csharp
internal static string GetDiagnosticDescription(string code)
{
    return code switch
    {
        DiagnosticCodes.ValidationFailed => "Validation failed",
        DiagnosticCodes.DuplicateMember => "Duplicate members",
        DiagnosticCodes.TBG103 => "View member collision within view scope",
        DiagnosticCodes.TBG104 => "View member name shadows class surface",
        // ... 40+ more mappings
    };
}
```

---

## 3. Validation Modules

### Module: Context.cs

**Purpose:** Validation context container and reporting functions.

**Key Types:**
- `ValidationContext` - Holds all diagnostic state
- `Context` static class - Factory and reporting methods

**Key Functions:**

#### `Context.Create()`
Creates new ValidationContext with zeroed counters.

#### `ValidationContext.RecordDiagnostic(string code, string severity, string message)`
- **What it does:** Records a single diagnostic
- **Parameters:**
  - `code` - Diagnostic code (e.g., "TBG710")
  - `severity` - "ERROR", "WARNING", or "INFO"
  - `message` - Detailed diagnostic message
- **Side effects:**
  - Increments `DiagnosticCountsByCode[code]`
  - Increments severity counter (ErrorCount/WarningCount/InfoCount)
  - Adds formatted message to `Diagnostics` list

#### `Context.WriteSummaryJson(BuildContext ctx, ValidationContext validationCtx)`
- **What it does:** Writes summary JSON to `.tests/phasegate-summary.json`
- **Format:**
  ```json
  {
    "timestamp": "...",
    "totals": { "errors": 0, "warnings": 12, "info": 241 },
    "diagnostic_counts_by_code": { "TBG103": 0, ... }
  }
  ```
- **Used for:** CI/snapshot comparison, trending

#### `Context.WriteDiagnosticsFile(BuildContext ctx, ValidationContext validationCtx)`
- **What it does:** Writes detailed diagnostics to `.tests/phasegate-diagnostics.txt`
- **Contents:**
  - Summary (totals)
  - Interface conformance issues (grouped by type)
  - All diagnostics (chronological order)

#### `Context.GetDiagnosticDescription(string code)`
- **What it does:** Maps diagnostic code to human-readable description
- **Returns:** String description for ALL 40+ diagnostic codes
- **Example:** `"TBG103"` → `"View member collision within view scope"`

---

### Module: Core.cs

**Purpose:** Core validation functions (types, members, generics, interfaces, inheritance, scopes, imports, policy).

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/Core.cs`

**Functions:**

#### 1. `ValidateTypeNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- All types have non-empty `TsEmitName`
- No duplicate type names within namespace
- TypeScript reserved words are properly sanitized

**Error codes:**
- `TBG405` (ValidationFailed) - Type missing TsEmitName
- `TBG102` (DuplicateMember) - Duplicate type name in namespace
- `TBG120` (ReservedWordUnsanitized) - Reserved word not sanitized

**Examples of failures:**
```csharp
// Type missing TsEmitName
class Foo { TsEmitName = "" }  // ERROR: TBG405

// Duplicate type name
namespace A {
    class Foo_1 { TsEmitName = "Foo_1" }
    class Foo_1 { TsEmitName = "Foo_1" }  // ERROR: TBG102
}

// Reserved word not sanitized
class @class { TsEmitName = "class" }  // WARNING: TBG120 (should be "class_")
```

**Algorithm:**
1. Track seen names in `HashSet<string>` per namespace
2. Check each type's `TsEmitName` is non-empty
3. Check for duplicates: `namesSeen.Contains(fullEmitName)`
4. Check if reserved word AND not sanitized: `IsTypeScriptReservedWord(name) && ClrName == TsEmitName`
5. Count sanitized names: `ClrName != TsEmitName && IsTypeScriptReservedWord(ClrName)`

---

#### 2. `ValidateMemberNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- All methods/properties/fields have non-empty `TsEmitName`
- No overload collisions within same scope (ClassSurface only)

**Error codes:**
- `TBG405` (ValidationFailed) - Member missing TsEmitName
- `TBG101` (AmbiguousOverload) - Method overload collision (WARNING)

**Examples of failures:**
```csharp
// Member missing TsEmitName
class Foo {
    method Bar() { TsEmitName = "" }  // ERROR: TBG405
}

// Overload collision
class Foo {
    method Bar(int x) { TsEmitName = "bar" }
    method Bar(string x) { TsEmitName = "bar" }  // WARNING: TBG101 (same signature)
}
```

**Algorithm:**
1. For each type, track member names per scope (instance/static)
2. Check each member's `TsEmitName` is non-empty
3. For methods: use signature `{TsEmitName}_{ParameterCount}` for collision detection
4. Warn on overload collisions (not errors - TypeScript allows overloads)

---

#### 3. `ValidateGenericParameters(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- All generic parameters have names
- Constraints are representable in TypeScript
- No illegal constraint narrowing in derived classes

**Error codes:**
- `TBG405` (ValidationFailed) - Generic parameter missing name
- `TBG404` (UnrepresentableConstraint) - Constraint can't be represented (WARNING)
- `TBG410` (ConstraintNarrowing) - Derived class narrows constraints (INFO)

**Examples of failures:**
```csharp
// Generic parameter missing name
class Foo<> { }  // ERROR: TBG405

// Unrepresentable constraint
class Foo<T> where T : Delegate { }  // WARNING: TBG404 (delegates not representable)

// Constraint narrowing
class Base<T> where T : class { }
class Derived<T> : Base<T> where T : IDisposable, class { }  // INFO: TBG410 (1→2 constraints)
```

**Algorithm:**
1. Check each generic parameter has non-empty name
2. Check constraints are representable: `IsConstraintRepresentable(constraint)`
3. For derived classes, compare constraint counts with base class
4. Emit INFO (not warning) for constraint narrowing (benign in TypeScript)

---

#### 4. `ValidateInterfaceConformance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- Classes structurally conform to claimed interfaces
- All interface methods/properties present on class surface
- Method signatures are TypeScript-assignable
- Property types match (no covariance)

**Error codes:**
- `TBG203` (StructuralConformanceFailure) - Missing or incompatible members (WARNING)
- `TBG310` (CovarianceSummary) - Property covariance issues (INFO)

**Examples of failures:**
```csharp
// Missing interface method
interface IFoo { method Bar(); }
class Foo : IFoo {
    // Missing Bar() implementation
}  // WARNING: TBG203

// Property covariance (TypeScript doesn't support)
interface IFoo { property Baz: object; }
class Foo : IFoo {
    property Baz: string;  // INFO: TBG310 (covariant return)
}
```

**Algorithm:**
1. Build set of interfaces with explicit views (skip these)
2. For each interface without explicit view:
   - Find all required methods/properties
   - Check class surface has matching members
   - Check signatures are TS-assignable: `IsRepresentableConformanceBreak()`
3. Aggregate issues per type (not per member)
4. Emit one-line summary + detailed conformance issues

**Special handling:**
- Interfaces with explicit views are **skipped** (satisfied via `As_IInterface` properties)
- Property covariance emits INFO (not warning) - TypeScript limitation
- Method covariance is OK (methods support overloads)

---

#### 5. `ValidateInheritance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- Base classes are actually classes (not interfaces/structs)
- No circular inheritance

**Error codes:**
- `TBG201` (CircularInheritance) - Base class is not a class

**Examples of failures:**
```csharp
// Base class is interface
interface IFoo { }
class Foo : IFoo { }  // ERROR: TBG201 (IFoo is interface, not class)
```

**Algorithm:**
1. For each type with BaseType:
   - Resolve base class in graph
   - Check `baseClass.Kind == TypeKind.Class`
2. Report errors for non-class base types

---

#### 6. `ValidateEmitScopes(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- Counts members by EmitScope (ClassSurface vs ViewOnly)
- No validation errors - just metrics

**Output:**
- Logs: `{totalMembers} members, {viewOnlyMembers} ViewOnly`

**Algorithm:**
1. Count all members
2. Count ViewOnly members
3. Log summary

---

#### 7. `ValidateImports(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)`

**What it validates:**
- No circular dependencies in import graph
- Import/export counts

**Error codes:**
- `TBG201` (CircularInheritance) - Circular dependency (WARNING)

**Examples of failures:**
```csharp
// Circular dependency
namespace A { imports B; }
namespace B { imports C; }
namespace C { imports A; }  // WARNING: TBG201 (A → B → C → A)
```

**Algorithm:**
1. Build adjacency list from import statements
2. DFS-based cycle detection: `DetectCyclesDFS()`
3. Report cycles as warnings (not errors - TypeScript allows)

---

#### 8. `ValidatePolicyCompliance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- Policy constraints are met (extensible)

**Error codes:**
- `TBG400` (PolicyViolation) - Policy violation

**Algorithm:**
- Placeholder for future policy checks
- Currently no-op

---

### Module: Views.cs

**Purpose:** View validation functions (orphan detection, integrity, member scoping).

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/Views.cs`

**Functions:**

#### 1. `Validate(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- ViewOnly members without views (orphans)
- ViewOnly members belong to correct view (SourceInterface matches)
- View interface types are resolvable

**Error codes:**
- `TBG510` (ViewCoverageMismatch) - ViewOnly member has no view or wrong view
- `TBG202` (InterfaceNotFound) - View references external interface (WARNING)
- `TBG302` (IndexerConflict) - Indexer property is ViewOnly (ERROR)

**Examples of failures:**
```csharp
// ViewOnly member without view
class Foo : IBar {
    method Baz() { EmitScope = ViewOnly, SourceInterface = IBar }
    // But no ExplicitView for IBar
}  // ERROR: TBG510

// ViewOnly member in wrong view
class Foo : IBar, IBaz {
    method Qux() { EmitScope = ViewOnly, SourceInterface = IBar }
    ExplicitView(IBaz) { members: [Qux] }  // Wrong interface!
}  // ERROR: TBG510
```

**Algorithm:**
1. Collect all ViewOnly members with SourceInterface (in graph)
2. Check each has matching ExplicitView: `plannedViews.Any(v => v.ViewMembers.Contains(member))`
3. Verify SourceInterface matches view's InterfaceReference
4. Warn if view interface is external (should be imported)

**Special handling:**
- Interfaces and static classes can have ViewOnly members without views (allowed)
- Indexers must NOT be ViewOnly (should be converted to methods)

---

#### 2. `ValidateIntegrity(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **3 HARD RULES** for view integrity

**Error codes:**
- `TBG511` (EmptyView) - View has zero members (PG_VIEW_001)
- `TBG512` (DuplicateViewForInterface) - Two views for same interface (PG_VIEW_002)
- `TBG513` (InvalidViewPropertyName) - View property name is invalid (PG_VIEW_003)

**The 3 Hard Rules:**

##### **PG_VIEW_001: Non-empty (must contain ≥1 ViewMember)**
```csharp
// INVALID - Empty view
ExplicitView(IFoo) { members: [] }  // ERROR: TBG511
```

##### **PG_VIEW_002: Unique target (no two views for same interface)**
```csharp
// INVALID - Duplicate views
ExplicitView(IFoo) { ViewPropertyName = "As_IFoo", members: [Bar] }
ExplicitView(IFoo) { ViewPropertyName = "As_IFoo2", members: [Baz] }  // ERROR: TBG512
```

##### **PG_VIEW_003: Valid/sanitized view property name**
```csharp
// INVALID - Reserved word not sanitized
ExplicitView(IFoo) { ViewPropertyName = "class" }  // ERROR: TBG513 (should be "class_")

// INVALID - Invalid identifier
ExplicitView(IFoo) { ViewPropertyName = "123-foo" }  // ERROR: TBG513 (starts with digit)
```

**Algorithm:**
1. Track seen interfaces per type (to detect duplicates)
2. For each view:
   - Check `ViewMembers.Length > 0` (PG_VIEW_001)
   - Check interface not already seen (PG_VIEW_002)
   - Check view property name: `IsValidTypeScriptIdentifier()` and not reserved word (PG_VIEW_003)

---

#### 3. `ValidateMemberScoping(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_NAME_003:** No name collisions within view scope
- **PG_NAME_004:** ViewOnly member names don't shadow class surface

**Error codes:**
- `TBG103` (ViewMemberCollisionInViewScope) - Collision within view (PG_NAME_003)
- `TBG104` (ViewMemberEqualsClassSurface) - View member shadows class surface (PG_NAME_004)

**Examples of failures:**

##### **PG_NAME_003: Collision within view**
```csharp
interface IFoo {
    method Bar();
    method bar();  // Different CLR names, same TS name after camelCase
}
class Foo : IFoo {
    ExplicitView(IFoo) {
        ViewMembers: [
            { ClrName = "Bar", TsEmitName = "bar" },
            { ClrName = "bar", TsEmitName = "bar" }  // ERROR: TBG103 (collision)
        ]
    }
}
```

##### **PG_NAME_004: View member shadows class surface**
```csharp
class Foo : IBar {
    method Baz() { EmitScope = ClassSurface, TsEmitName = "baz" }
    method IBaz() { EmitScope = ViewOnly, SourceInterface = IBar, TsEmitName = "baz" }
    ExplicitView(IBar) { ViewMembers: [IBaz] }
}  // ERROR: TBG104 (view member "baz" shadows class surface "baz")
```

**Algorithm:**
1. Collect class surface member names: `GetFinalMemberName(stableId, ClassSurface(type, isStatic))`
2. For each view:
   - Track view member names in `Dictionary<string, string>`
   - Check for collisions: `viewMemberNames.ContainsKey(emittedName)`
   - For ViewOnly members, check against class surface: `classSurfaceNames.Contains(emittedName)`

**Special handling:**
- Only checks ViewOnly members for PG_NAME_004 (members on both surface and view naturally have same name)

---

### Module: Names.cs

**Purpose:** Name-related validation (final names, aliases, identifiers, overloads, class surface uniqueness).

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/Names.cs`

**Functions:**

#### 1. `ValidateFinalNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- All types have final names from Renamer
- All ClassSurface members have final names from Renamer
- No duplicate names within namespace/type scopes

**Error codes:**
- `TBG405` (ValidationFailed) - Type/member missing final name
- `TBG102` (DuplicateMember) - Duplicate type/property/field name
- `TBG101` (AmbiguousOverload) - Method overload collision (WARNING)

**Examples of failures:**
```csharp
// Type missing final name
class Foo {
    Renamer.GetFinalTypeName(Foo) returns ""  // ERROR: TBG405
}

// Duplicate property name
class Foo {
    property Bar { TsEmitName = "bar" }
    property BAR { TsEmitName = "bar" }  // ERROR: TBG102
}
```

**Algorithm:**
1. For each namespace, track type names: `GetFinalTypeName(type)`
2. For each type, track member names (separated by static/instance):
   - Methods: use signature `{finalName}({paramCount})`
   - Properties/fields: use `finalName` directly
3. Check all names are non-empty and unique

**Special handling:**
- Only checks ClassSurface members (ViewOnly members may have duplicate names in different views)
- Method overload collisions are WARNINGs (not errors) - TypeScript allows overloads

---

#### 2. `ValidateAliases(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)`

**What it validates:**
- Import aliases don't collide within import scope
- Imported type names don't collide with local types

**Error codes:**
- `TBG100` (NameConflictUnresolved) - Alias collision (ERROR) or name collision (WARNING)

**Examples of failures:**
```csharp
// Alias collision
namespace A {
    import { Foo as Bar } from "B";
    import { Baz as Bar } from "C";  // ERROR: TBG100 (alias "Bar" collides)
}

// Imported name collides with local type
namespace A {
    import { Foo } from "B";
    class Foo { }  // WARNING: TBG100 (imported "Foo" collides with local "Foo")
}
```

**Algorithm:**
1. For each namespace, track:
   - Aliases: `aliasesInScope.Add(typeImport.Alias)`
   - Effective names: `typeNamesInScope.Add(effectiveName)` (alias or type name)
2. Check aliases are unique
3. Check imported names don't collide with local types

---

#### 3. `ValidateIdentifiers(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_ID_001:** All identifiers are properly sanitized (TypeScript reserved words have underscore suffix)
- Checks: types, members, parameters, type parameters, view members

**Error codes:**
- `TBG719` (PostSanitizerUnsanitizedReservedIdentifier) - Reserved word not sanitized (PG_ID_001)

**Examples of failures:**
```csharp
// Type name not sanitized
class @class {
    TsEmitName = "class"  // ERROR: TBG719 (should be "class_")
}

// Method name not sanitized
class Foo {
    method @for() { TsEmitName = "for" }  // ERROR: TBG719 (should be "for_")
}

// Parameter name not sanitized
class Foo {
    method Bar(int @while) { }  // ERROR: TBG719 (param should be "while_")
}
```

**Algorithm:**
1. For each identifier (type, member, parameter, type parameter):
   - Get emitted name: `GetFinalTypeName()` or `GetFinalMemberName()`
   - Check if reserved word: `IsReservedWord(emittedName)`
   - Check if sanitized: `emittedName.EndsWith("_")`
   - Emit error if reserved word AND not sanitized

**Identifiers checked:**
- Namespace names
- Type names (from Renamer)
- Type parameters (with `SanitizeParameterName()`)
- Method names (ClassSurface and ViewOnly)
- Method parameters (with `SanitizeParameterName()`)
- Method type parameters
- Property names (ClassSurface and ViewOnly)
- Indexer parameters
- Field names
- Event names
- View property names
- View member names (in ViewSurface scope)

---

#### 4. `ValidateOverloadCollisions(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_OV_001:** No duplicate erased TypeScript signatures in same surface
- Checks class surface and each view separately
- Groups by: (Name, IsStatic, ErasedSignature)

**Error codes:**
- `TBG213` (DuplicateErasedSurfaceSignature) - Duplicate erased signature (PG_OV_001)

**Examples of failures:**
```csharp
// Duplicate erased signature (generics erase to same signature)
class Foo {
    method Bar<T>(T x) { }
    method Bar<U>(U x) { }  // ERROR: TBG213 (both erase to "Bar(T): void")
}

// Duplicate property name
class Foo {
    property Baz: int;
    property Baz: string;  // ERROR: TBG213 (same name, different types)
}
```

**Algorithm:**
1. For each type, check class surface:
   - Group methods by `(finalName, isStatic, erasedParams, erasedReturn)`
   - Group properties by `(finalName, isStatic)`
   - Report collisions (>1 member per group)
2. For each view, check view surface:
   - Same grouping logic, but use ViewSurface scope

**Erasing:**
- Erases types to TypeScript-level representation
- Maps CLR types to TS equivalents: `System.Int32` → `number`
- Simplifies generics: `List<int>` → `List_1<number>`
- Removes ref/out: `ref T` → `T`

---

#### 5. `ValidateClassSurfaceUniqueness(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_NAME_005:** Class surface has no duplicate emitted names after deduplication
- Catches duplicates that slipped through ClassSurfaceDeduplicator

**Error codes:**
- `TBG105` (DuplicatePropertyNamePostDedup) - Duplicate property name (PG_NAME_005)

**Examples of failures:**
```csharp
// Duplicate property name after camelCase
class Foo {
    property Bar: int { EmitScope = ClassSurface, TsEmitName = "bar" }
    property bar: string { EmitScope = ClassSurface, TsEmitName = "bar" }
}  // ERROR: TBG105 (ClassSurfaceDeduplicator should have removed one)
```

**Algorithm:**
1. Group class-surface properties by emitted name (after camelCase)
2. Report groups with >1 member
3. This should NEVER happen (ClassSurfaceDeduplicator should prevent)

---

### Module: Scopes.cs

**Purpose:** Scope-related validation (EmitScope invariants, scope mismatches, scope key formatting).

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/Scopes.cs`

**Functions:**

#### 1. `ValidateEmitScopeInvariants(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_INT_002:** No member appears in both ClassSurface and ViewOnly
- **PG_INT_003:** ClassSurface members must NOT have SourceInterface

**Error codes:**
- `TBG702` (MemberInBothClassAndView) - Dual-scope member (PG_INT_002)
- `TBG703` (ClassSurfaceMemberHasSourceInterface) - ClassSurface member has SourceInterface (PG_INT_003)

**Examples of failures:**

##### **PG_INT_002: Member in both scopes**
```csharp
class Foo {
    // Same StableId appearing twice with different EmitScopes
    method Bar() { EmitScope = ClassSurface, StableId = "Foo.Bar" }
    method Bar() { EmitScope = ViewOnly, StableId = "Foo.Bar" }  // ERROR: TBG702
}
```

##### **PG_INT_003: ClassSurface member has SourceInterface**
```csharp
class Foo : IBar {
    method Baz() {
        EmitScope = ClassSurface,
        SourceInterface = IBar  // ERROR: TBG703 (SourceInterface only for ViewOnly)
    }
}
```

**Algorithm:**
1. Build scope map: `Dictionary<MemberStableId, (bool ClassSurface, bool ViewOnly)>`
2. Mark each member's scopes
3. Check for dual-scope: `scopeMap[id].ClassSurface && scopeMap[id].ViewOnly`
4. Check ClassSurface members: `EmitScope == ClassSurface && SourceInterface != null`

---

#### 2. `ValidateScopeMismatches(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_SCOPE_003:** Scope keys are well-formed (not empty/malformed)
- **PG_SCOPE_004:** ClassSurface members have decisions in class scope
- **PG_SCOPE_004:** ViewOnly members have decisions in view scope

**Error codes:**
- `TBG720` (MalformedScopeKey) - Empty/malformed scope key (PG_SCOPE_003)
- `TBG721` (ScopeKindMismatchWithEmitScope) - Scope kind doesn't match EmitScope (PG_SCOPE_004)

**Examples of failures:**

##### **PG_SCOPE_003: Malformed scope key**
```csharp
// ClassSurface member with bad scope key
method Foo() {
    EmitScope = ClassSurface,
    ScopeKey = ""  // ERROR: TBG720 (should be "type:...")
}

// ViewOnly member with bad scope key
method Bar() {
    EmitScope = ViewOnly,
    ScopeKey = ""  // ERROR: TBG720 (should be "view:...")
}
```

##### **PG_SCOPE_004: Scope/EmitScope mismatch**
```csharp
// ClassSurface member missing class scope decision
method Foo() {
    EmitScope = ClassSurface,
    // Renamer has no decision in ClassSurface(type, isStatic)
}  // ERROR: TBG721

// ViewOnly member missing view scope decision
method Bar() {
    EmitScope = ViewOnly,
    SourceInterface = IFoo,
    // Renamer has no decision in ViewSurface(type, "IFoo", isStatic)
}  // ERROR: TBG721
```

**Algorithm:**
1. For ClassSurface members:
   - Build scope: `ClassSurface(type, isStatic)`
   - Check scope key format: `scope.ScopeKey.StartsWith("type:")`
   - Check Renamer has decision: `Renamer.TryGetDecision(stableId, scope, out _)`
2. For ViewOnly members:
   - Build scope: `ViewSurface(type, interfaceStableId, isStatic)`
   - Check scope key format: `scope.ScopeKey.StartsWith("view:")`
   - Check Renamer has decision: `Renamer.TryGetDecision(stableId, scope, out _)`

---

### Module: Finalization.cs

**Purpose:** Comprehensive finalization sweep - validates every symbol has proper placement and naming. This is the FINAL check before emission.

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/Finalization.cs`

**Function:**

#### `Validate(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_FIN_001:** All members have explicit EmitScope (not Unspecified)
- **PG_FIN_002:** ViewOnly members have SourceInterface and belong to exactly one view
- **PG_FIN_003:** All ClassSurface/ViewOnly members have final names in correct scopes
- **PG_FIN_004:** All types have final names in namespace scope
- **PG_FIN_005:** No empty views (zero members)
- **PG_FIN_006:** No duplicate view membership (member in >1 view)
- **PG_FIN_007:** No dual-role clashes (same StableId in both ClassSurface and ViewOnly)
- **PG_FIN_008:** Interfaces requiring views have corresponding views
- **PG_FIN_009:** All emitting identifiers are sanitized

**Error codes:**
- `TBG710` (MissingEmitScopeOrIllegalCombo) - Member has no EmitScope (PG_FIN_001)
- `TBG711` (ViewOnlyWithoutExactlyOneExplicitView) - ViewOnly member not in exactly one view (PG_FIN_002)
- `TBG712` (EmittingMemberMissingFinalName) - Member missing final name (PG_FIN_003)
- `TBG713` (EmittingTypeMissingFinalName) - Type missing final name (PG_FIN_004)
- `TBG714` (InvalidOrEmptyViewMembership) - Empty view (PG_FIN_005)
- `TBG715` (DuplicateViewMembership) - Member in multiple views (PG_FIN_006)
- `TBG716` (ClassViewDualRoleClash) - Class/View dual-role clash (PG_FIN_007)
- `TBG717` (RequiredViewMissingForInterface) - Interface requires view but type has none (PG_FIN_008)
- `TBG718` (PostSanitizerUnsanitizedIdentifier) - Unsanitized identifier (PG_FIN_009)

**Examples of failures:**

##### **PG_FIN_001: Missing EmitScope**
```csharp
class Foo {
    method Bar() { EmitScope = Unspecified }  // ERROR: TBG710
}
```

##### **PG_FIN_002: ViewOnly without view**
```csharp
class Foo : IBar {
    method Baz() {
        EmitScope = ViewOnly,
        SourceInterface = null  // ERROR: TBG711 (no SourceInterface)
    }
}

// OR

class Foo : IBar {
    method Baz() {
        EmitScope = ViewOnly,
        SourceInterface = IBar
    }
    // But no ExplicitView for IBar
}  // ERROR: TBG711
```

##### **PG_FIN_003: Missing final name**
```csharp
class Foo {
    method Bar() {
        EmitScope = ClassSurface,
        // Renamer.GetFinalMemberName(Bar, ClassSurface) returns ""
    }
}  // ERROR: TBG712
```

##### **PG_FIN_004: Type missing final name**
```csharp
namespace A {
    class Foo {
        // Renamer.GetFinalTypeName(Foo, NamespaceScope) returns ""
    }
}  // ERROR: TBG713
```

##### **PG_FIN_005: Empty view**
```csharp
class Foo : IBar {
    ExplicitView(IBar) { ViewMembers: [] }  // ERROR: TBG714
}
```

##### **PG_FIN_006: Duplicate view membership**
```csharp
class Foo : IBar, IBaz {
    method Qux() { EmitScope = ViewOnly, SourceInterface = IBar }
    ExplicitView(IBar) { ViewMembers: [Qux] }
    ExplicitView(IBaz) { ViewMembers: [Qux] }  // ERROR: TBG715 (Qux in 2 views)
}
```

##### **PG_FIN_007: Dual-role clash**
```csharp
class Foo {
    // Same StableId in both scopes
    StableId(Foo.Bar) in ClassSurface
    StableId(Foo.Bar) in ViewOnly  // ERROR: TBG716
}
```

##### **PG_FIN_009: Unsanitized identifier**
```csharp
class Foo {
    property @class: int {
        EmitScope = ClassSurface,
        TsEmitName = "class"  // ERROR: TBG718 (should be "class_")
    }
}
```

**Algorithm:**
1. **PG_FIN_004:** Check all types: `Renamer.HasFinalTypeName(type.StableId, namespaceScope)`
2. **PG_FIN_001:** Check all members: `member.EmitScope != Unspecified`
3. **PG_FIN_007:** Build dual-role map: `classSurfaceMembers ∩ viewOnlyMembers`
4. **PG_FIN_003:** Check ClassSurface members: `Renamer.HasFinalMemberName(stableId, classSurfaceScope)`
5. **PG_FIN_002, PG_FIN_003:** Check ViewOnly members:
   - Has SourceInterface: `member.SourceInterface != null`
   - Belongs to view: `viewsByInterface.ContainsKey(interfaceStableId)`
   - Has final name: `Renamer.HasFinalViewMemberName(stableId, viewSurfaceScope)`
6. **PG_FIN_005:** Check views: `view.ViewMembers.Length > 0`
7. **PG_FIN_006:** Build membership map: `memberToViews[stableId]`, check count > 1
8. **PG_FIN_009:** Check identifiers: `Sanitize(emitName).WasSanitized && Sanitized != emitName`

---

### Module: Constraints.cs

**Purpose:** Constraint validation (generic parameter constraints, constraint losses).

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/Constraints.cs`

**Function:**

#### `EmitDiagnostics(BuildContext ctx, InterfaceConstraintFindings findings, ValidationContext validationCtx)`

**What it validates:**
- **PG_CT_001:** Non-benign constraint losses (constructor constraints)
- **PG_CT_002:** Constructor constraint losses (with policy override)

**Error codes:**
- `TBG406` (NonBenignConstraintLoss) - Non-benign constraint loss (PG_CT_001)
- `TBG407` (ConstructorConstraintLoss) - Constructor constraint loss with override (PG_CT_002)

**Examples of failures:**

##### **PG_CT_001: Non-benign constraint loss (strict mode)**
```csharp
interface IFoo<T> where T : new() { }
class Foo<T> : IFoo<T> {
    // TypeScript can't represent "new()" constraint
}  // ERROR: TBG406 (strict mode: AllowConstructorConstraintLoss = false)
```

##### **PG_CT_002: Constructor constraint loss (override mode)**
```csharp
// Same as above, but with policy override
Policy.Constraints.AllowConstructorConstraintLoss = true

interface IFoo<T> where T : new() { }
class Foo<T> : IFoo<T> {
    // TypeScript can't represent "new()" constraint
}  // WARNING: TBG407 (override mode allowed)
```

**Algorithm:**
1. Check policy: `ctx.Policy.Constraints.AllowConstructorConstraintLoss`
2. For each finding with `ConstraintLossKind.ConstructorConstraintLoss`:
   - If strict mode: emit ERROR (TBG406)
   - If override mode: emit WARNING (TBG407)

**Constraint loss kinds:**
- `ConstructorConstraintLoss` - TypeScript can't represent `new()` constraint
- Other kinds exist but are not yet validated

---

### Module: Types.cs

**Purpose:** Type system validation (printer name consistency, TypeMap compliance, external type resolution).

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/Types.cs`

**Functions:**

#### 1. `ValidatePrinterNameConsistency(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_PRINT_001:** TypeNameResolver produces names matching Renamer final names
- Validates TypeRefPrinter→TypeNameResolver→Renamer chain integrity
- Walks ALL type references in signatures

**Error codes:**
- `TBG530` (TypeNamePrinterRenamerMismatch) - Printer/Renamer mismatch (PG_PRINT_001)

**Examples of failures:**
```csharp
// TypeNameResolver returns wrong name
class Foo {
    TypeNameResolver.For(Foo) returns "Foo_CLR"
    Renamer.GetFinalTypeName(Foo) returns "Foo_TS"  // ERROR: TBG530 (mismatch)
}
```

**Algorithm:**
1. Create TypeNameResolver instance
2. Walk all type references in graph:
   - Base types
   - Interfaces
   - Method parameters/returns
   - Property types
   - Field types
   - Event handler types
   - Generic parameter constraints
3. For each NamedTypeReference:
   - Skip primitives: `TypeNameResolver.IsPrimitive(fullName)`
   - Resolve to TypeSymbol: `graph.TypeIndex[stableId]`
   - Compare: `resolver.For(named) == renamer.GetFinalTypeName(type)`

---

#### 2. `ValidateTypeMapCompliance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_TYPEMAP_001:** No unsupported special forms (function pointers, etc.)
- **NOTE:** Pointers and byrefs are NOW supported via branded markers (TSUnsafePointer<T>, TSByRef<T>)

**Error codes:**
- `TBG870` (UnsupportedClrSpecialForm) - Unsupported special form (PG_TYPEMAP_001)

**Examples of failures:**
```csharp
// Function pointer (unsupported)
delegate* unmanaged<int, void> fp;  // ERROR: TBG870 (function pointers not supported)
```

**Algorithm:**
1. Walk all type references recursively
2. Check for unsupported forms:
   - Function pointers (not yet supported)
   - Other special forms
3. Recurse into:
   - Type arguments
   - Array element types
   - Pointer pointee types (allowed - recurse into pointee)
   - ByRef referenced types (allowed - recurse into referenced)

**Special handling:**
- Pointers: NOW allowed (mapped to `TSUnsafePointer<T>`) - recurse into pointee
- ByRefs: NOW allowed (mapped to `TSByRef<T>`) - recurse into referenced

---

#### 3. `ValidateExternalTypeResolution(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)`

**What it validates:**
- **PG_LOAD_001:** All external type references are either in TypeIndex or built-in
- Detects types that should have been loaded but weren't (missing transitive closure)

**Error codes:**
- `TBG880` (UnresolvedExternalType) - Unresolved external type (PG_LOAD_001)

**Examples of failures:**
```csharp
// External type not in graph and not built-in
class Foo {
    property Bar: ExternalLib.Baz;
    // ExternalLib.Baz not in TypeIndex and not in source assemblies
}  // ERROR: TBG880 (transitive closure loading failed)
```

**Algorithm:**
1. Walk all type references in public API
2. For each NamedTypeReference:
   - Skip built-ins: `TypeMap.TryMapBuiltin(fullName, out _)`
   - Check TypeIndex: `graph.TypeIndex.TryGetValue(stableId, out _)`
   - If not found, check if source assembly: `graph.SourceAssemblies.Any(path => GetFileName(path) == assemblyName)`
   - If external (not source assembly) AND not in graph: ERROR TBG880

**Special handling:**
- Allows references to types in source assemblies (might be internal types)
- Only errors on truly external types (not in source assemblies)

---

### Module: ImportExport.cs

**Purpose:** Import/export validation (public API surface, import completeness, export completeness).

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/ImportExport.cs`

**Functions:**

#### 1. `ValidatePublicApiSurface(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)`

**What it validates:**
- **PG_API_001:** Public API doesn't reference non-emitted types
- **PG_API_002:** Generic constraints don't reference non-emitted types

**Error codes:**
- `TBG860` (PublicApiReferencesNonEmittedType) - Public API exposes internal type (PG_API_001)
- `TBG861` (GenericConstraintReferencesNonEmittedType) - Constraint references non-emitted type (PG_API_002)

**Examples of failures:**

##### **PG_API_001: Public API exposes internal type**
```csharp
// Internal type exposed in public API
internal class InternalFoo { }
public class PublicBar {
    public InternalFoo Baz { get; }  // ERROR: TBG860 (public API exposes internal type)
}
```

##### **PG_API_002: Generic constraint references non-emitted type**
```csharp
internal interface IInternalFoo { }
public class PublicBar<T> where T : IInternalFoo {
    // Constraint references internal interface
}  // ERROR: TBG861 (constraint references non-emitted type)
```

**Algorithm:**
1. Walk all PUBLIC types (Accessibility == Public)
2. For each type reference in public API:
   - Base types
   - Interfaces
   - Method parameters/returns (public methods only)
   - Property types (public properties only)
   - Field types (public fields only)
   - Event handler types (public events only)
   - Generic parameter constraints (PG_API_002)
3. For each NamedTypeReference:
   - Skip primitives
   - Resolve to TypeSymbol
   - Check: `IsEmitted(type)` (Accessibility == Public)
   - Check: `IsExported(type, imports)` (in export list)
   - If not emitted OR not exported: ERROR TBG860

---

#### 2. `ValidateImportCompleteness(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)`

**What it validates:**
- **PG_IMPORT_001:** Every foreign type used in signatures has corresponding import

**Error codes:**
- `TBG850` (MissingImportForForeignType) - Type used but not imported (PG_IMPORT_001)

**Examples of failures:**
```csharp
namespace A {
    // Uses type from namespace B but doesn't import it
    class Foo {
        property Bar: B.Baz;
        // B.Baz not imported
    }
}  // ERROR: TBG850 (type B.Baz used but not imported)
```

**Algorithm:**
1. For each namespace:
2. Walk all type references in all types
3. For each NamedTypeReference:
   - Skip primitives
   - Check if declared locally AND emitted: `IsDeclaredAndEmitted(ns, fullName)`
   - Check if in graph: `graph.TypeIndex.TryGetValue(stableId, out targetType)`
   - If external (not in graph): skip (handled by external imports)
   - If foreign (in graph but different namespace):
     - Check if imported: `IsImported(ns.Name, tsName)`
     - If not imported: ERROR TBG850

---

#### 3. `ValidateExportCompleteness(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)`

**What it validates:**
- **PG_EXPORT_001:** Every imported type is actually exported by source namespace

**Error codes:**
- `TBG851` (ImportedTypeNotExported) - Import references unexported type (PG_EXPORT_001)

**Examples of failures:**
```csharp
namespace A {
    import { Foo } from "./B";
    // But namespace B doesn't export "Foo"
}  // ERROR: TBG851 (imported Foo but B doesn't export it)
```

**Algorithm:**
1. For each namespace's imports:
2. For each import statement:
   - Get exports from target namespace: `imports.NamespaceExports[targetNamespace]`
   - Build export names set: `exportedNames = new HashSet(exports.Select(e => e.ExportName))`
3. For each imported type:
   - Check if exported: `exportedNames.Contains(importedName)`
   - If not exported: ERROR TBG851

---

### Module: Shared.cs

**Purpose:** Shared utility functions for validation.

**File:** `src/tsbindgen/SinglePhase/Plan/Validation/Shared.cs`

**Functions:**

#### `IsTypeScriptReservedWord(string name)`
- **What it does:** Checks if name is TypeScript reserved word
- **Returns:** `true` if reserved, `false` otherwise
- **Reserved words:** `class`, `function`, `interface`, `type`, `new`, etc. (40+ words)

#### `IsValidTypeScriptIdentifier(string name)`
- **What it does:** Checks if name is valid TypeScript identifier
- **Rules:**
  - Must start with letter, `_`, or `$`
  - Subsequent chars: letters, digits, `_`, or `$`
- **Returns:** `true` if valid, `false` otherwise

#### `IsRepresentableConformanceBreak(MethodSymbol classMethod, MethodSymbol ifaceMethod)`
- **What it does:** Checks if class method is assignable to interface method in TypeScript
- **Returns:** `true` if NOT assignable (representable break), `false` if assignable (benign)
- **Algorithm:**
  1. Erase both methods to TS signatures: `TsErase.EraseMember()`
  2. Check assignability: `TsAssignability.IsMethodAssignable(classSig, ifaceSig)`

#### `GetPropertyTypeString(PropertySymbol property)`
- **What it does:** Gets string representation of property type for comparison
- **Returns:** Full name of property type

#### `IsInterfaceInGraph(SymbolGraph graph, TypeReference ifaceRef)`
- **What it does:** Checks if interface exists in symbol graph
- **Returns:** `true` if in graph, `false` if external
- **Used by:** View validation to determine if interface should have view

---

## 4. All Diagnostic Codes (Complete Reference)

**Source:** `src/tsbindgen/Core/Diagnostics/DiagnosticCodes.cs`

| Code | Name | Severity | Category | Description | PhaseGate Code |
|------|------|----------|----------|-------------|----------------|
| **0xx - Resolution / Binding** |
| TBG001 | UnresolvedType | ERROR | Resolution | Unresolved type reference | |
| TBG002 | UnresolvedGenericParameter | ERROR | Resolution | Unresolved generic parameter | |
| TBG003 | UnresolvedConstraint | ERROR | Resolution | Unresolved constraint | |
| **1xx - Naming / Conflicts** |
| TBG100 | NameConflictUnresolved | ERROR/WARNING | Naming | Name conflict unresolved | |
| TBG101 | AmbiguousOverload | WARNING | Naming | Ambiguous overload | Core.ValidateMemberNames |
| TBG102 | DuplicateMember | ERROR | Naming | Duplicate member | Core.ValidateTypeNames |
| TBG103 | ViewMemberCollisionInViewScope | ERROR | Naming | View member collision within view scope | **PG_NAME_003** |
| TBG104 | ViewMemberEqualsClassSurface | ERROR | Naming | View member name shadows class surface | **PG_NAME_004** |
| TBG105 | DuplicatePropertyNamePostDedup | ERROR | Naming | Duplicate property name on class surface | **PG_NAME_005** |
| TBG120 | ReservedWordUnsanitized | WARNING | Naming | Reserved word not sanitized | Core.ValidateTypeNames |
| **2xx - Overload & Hierarchy** |
| TBG200 | DiamondInheritance | INFO/ERROR | Hierarchy | Diamond inheritance detected/conflict | |
| TBG201 | CircularInheritance | ERROR | Hierarchy | Circular inheritance/dependencies | Core.ValidateInheritance |
| TBG202 | InterfaceNotFound | WARNING | Hierarchy | External interface reference | Views.Validate |
| TBG203 | StructuralConformanceFailure | WARNING | Hierarchy | Interface conformance failure | Core.ValidateInterfaceConformance |
| TBG204 | StaticSideInheritanceIssue | WARNING | Hierarchy | Static side inheritance issue | |
| TBG205 | InterfaceMethodNotAssignable | ERROR | Hierarchy | Interface method not assignable (erased) | **PG_IFC_001** |
| TBG211 | OverloadUnified | INFO | Overload | Overload unified | |
| TBG212 | OverloadUnresolvable | WARNING | Overload | Overload unresolvable | |
| TBG213 | DuplicateErasedSurfaceSignature | ERROR | Overload | Duplicate erased signature | **PG_OV_001** |
| **3xx - TS Compatibility** |
| TBG300 | PropertyCovarianceUnsupported | WARNING | TS Compat | Property covariance unsupported | |
| TBG301 | StaticSideVariance | WARNING | TS Compat | Static side variance | |
| TBG302 | IndexerConflict | ERROR | TS Compat | Indexer conflict | Views.Validate |
| TBG310 | CovarianceSummary | INFO | TS Compat | Property covariance summary | Core.ValidateInterfaceConformance |
| **4xx - Policy / Constraints** |
| TBG400 | PolicyViolation | ERROR | Policy | Policy violation | |
| TBG401 | UnsatisfiableConstraint | ERROR | Constraint | Unsatisfiable constraint | |
| TBG402 | UnsupportedConstraintMerge | ERROR | Constraint | Unsupported constraint merge | |
| TBG403 | IncompatibleConstraints | ERROR | Constraint | Incompatible constraints | |
| TBG404 | UnrepresentableConstraint | WARNING | Constraint | Unrepresentable constraint | Core.ValidateGenericParameters |
| TBG405 | ValidationFailed | ERROR | Policy | Validation failed | Core.ValidateTypeNames, etc. |
| TBG406 | NonBenignConstraintLoss | ERROR | Constraint | Non-benign constraint loss | **PG_CT_001** |
| TBG407 | ConstructorConstraintLoss | WARNING | Constraint | Constructor constraint loss (override) | **PG_CT_002** |
| TBG410 | ConstraintNarrowing | INFO | Constraint | Constraint narrowing | Core.ValidateGenericParameters |
| **5xx - Renaming & Views** |
| TBG500 | RenameConflict | ERROR | Renaming | Rename conflict | |
| TBG501 | ExplicitOverrideNotApplied | WARNING | Renaming | Explicit override not applied | |
| TBG510 | ViewCoverageMismatch | ERROR | View | ViewOnly member coverage issue | Views.Validate |
| TBG511 | EmptyView | ERROR | View | Empty view (no members) | **PG_VIEW_001** |
| TBG512 | DuplicateViewForInterface | ERROR | View | Duplicate view for same interface | **PG_VIEW_002** |
| TBG513 | InvalidViewPropertyName | ERROR | View | Invalid/unsanitized view property name | **PG_VIEW_003** |
| TBG530 | TypeNamePrinterRenamerMismatch | ERROR | Renaming | Type name mismatch (Printer vs Renamer) | **PG_PRINT_001** |
| **6xx - Metadata / Binding** |
| TBG600 | MissingMetadataToken | ERROR | Metadata | Missing metadata token | |
| TBG601 | BindingAmbiguity | ERROR | Metadata | Binding ambiguity | |
| **7xx - PhaseGate Core** |
| TBG702 | MemberInBothClassAndView | ERROR | Scope | Member in both ClassSurface and ViewOnly | **PG_INT_002** |
| TBG703 | ClassSurfaceMemberHasSourceInterface | ERROR | Scope | ClassSurface member has SourceInterface | **PG_INT_003** |
| TBG710 | MissingEmitScopeOrIllegalCombo | ERROR | Finalization | Member has no EmitScope or illegal combo | **PG_FIN_001** |
| TBG711 | ViewOnlyWithoutExactlyOneExplicitView | ERROR | Finalization | ViewOnly member not in exactly one view | **PG_FIN_002** |
| TBG712 | EmittingMemberMissingFinalName | ERROR | Finalization | Member missing final name in scope | **PG_FIN_003** |
| TBG713 | EmittingTypeMissingFinalName | ERROR | Finalization | Type missing final name in namespace | **PG_FIN_004** |
| TBG714 | InvalidOrEmptyViewMembership | ERROR | Finalization | Empty/invalid view | **PG_FIN_005** |
| TBG715 | DuplicateViewMembership | ERROR | Finalization | Duplicate view membership | **PG_FIN_006** |
| TBG716 | ClassViewDualRoleClash | ERROR | Finalization | Class/View dual-role clash | **PG_FIN_007** |
| TBG717 | RequiredViewMissingForInterface | ERROR | Finalization | Interface requires view but type has none | **PG_FIN_008** |
| TBG718 | PostSanitizerUnsanitizedIdentifier | ERROR | Finalization | Unsanitized identifier post-sanitizer | **PG_FIN_009** |
| TBG719 | PostSanitizerUnsanitizedReservedIdentifier | ERROR | Identifier | Reserved identifier not sanitized | **PG_ID_001** |
| TBG720 | MalformedScopeKey | ERROR | Scope | Empty/malformed scope key | **PG_SCOPE_003** |
| TBG721 | ScopeKindMismatchWithEmitScope | ERROR | Scope | Scope kind doesn't match EmitScope | **PG_SCOPE_004** |
| **8xx - Emission / Modules / TypeMap** |
| TBG850 | MissingImportForForeignType | ERROR | Import | Type used but not imported | **PG_IMPORT_001** |
| TBG851 | ImportedTypeNotExported | ERROR | Export | Import references unexported type | **PG_EXPORT_001** |
| TBG852 | InvalidImportModulePath | ERROR | Module | Invalid import module path | **PG_MODULE_001** |
| TBG853 | FacadeImportsMustUseInternalIndex | ERROR | Facade | Facade imports must use internal index | **PG_FACADE_001** |
| TBG860 | PublicApiReferencesNonEmittedType | ERROR | API | Public API exposes internal/non-emitted type | **PG_API_001** |
| TBG861 | GenericConstraintReferencesNonEmittedType | ERROR | API | Generic constraint references non-emitted type | **PG_API_002** |
| TBG862 | PublicApiReferencesNonPublicType | ERROR | API | Public API exposes non-public type | **PG_API_004** |
| TBG870 | UnsupportedClrSpecialForm | ERROR | TypeMap | Unsupported special form (function pointer) | **PG_TYPEMAP_001** |
| **9xx - Assembly Load** |
| TBG880 | UnresolvedExternalType | ERROR | Load | Unresolved external type reference | **PG_LOAD_001** |
| TBG881 | MixedPublicKeyTokenForSameName | ERROR | Load | Mixed PublicKeyToken for same assembly | **PG_LOAD_002** |
| TBG882 | VersionDriftForSameIdentity | ERROR | Load | Version drift (same assembly, different versions) | **PG_LOAD_003** |
| TBG883 | RetargetableOrContentTypeAssemblyRef | ERROR | Load | Retargetable/ContentType assembly reference | **PG_LOAD_004** |

**Total: 43 diagnostic codes**

---

## 5. Validation Flow Diagram

```
┌──────────────────────────────────────────────────────────────────────────┐
│                         PHASEGATE VALIDATION                              │
│                                                                           │
│  Input: SymbolGraph (fully transformed, named, shaped, planned)          │
└──────────────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ STEP 1: CORE VALIDATIONS                                                 │
├──────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Core.ValidateTypeNames()                                        │    │
│  │   • All types have TsEmitName                                   │    │
│  │   • No duplicate type names in namespace                        │    │
│  │   • Reserved words sanitized                                    │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Core.ValidateMemberNames()                                      │    │
│  │   • All members have TsEmitName                                 │    │
│  │   • No overload collisions                                      │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Core.ValidateGenericParameters()                                │    │
│  │   • All generic params have names                               │    │
│  │   • Constraints representable                                   │    │
│  │   • No illegal constraint narrowing                             │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Core.ValidateInterfaceConformance()                             │    │
│  │   • Classes structurally conform to interfaces                  │    │
│  │   • Method signatures TS-assignable                             │    │
│  │   • Property covariance detected (INFO)                         │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Core.ValidateInheritance()                                      │    │
│  │   • Base classes are classes                                    │    │
│  │   • No circular inheritance                                     │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Core.ValidateEmitScopes()                                       │    │
│  │   • Count ClassSurface/ViewOnly members                         │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Core.ValidateImports()                                          │    │
│  │   • No circular dependencies                                    │    │
│  │   • Import/export counts                                        │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Core.ValidatePolicyCompliance()                                 │    │
│  │   • Policy constraints met                                      │    │
│  └─────────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ STEP 2: HARDENING VALIDATIONS (M1-M18)                                   │
├──────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M1: Views.Validate()                                            │    │
│  │   • ViewOnly members have views                                 │    │
│  │   • SourceInterface matches view                                │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M2: Names.ValidateFinalNames()                                  │    │
│  │   • All types have final names from Renamer                     │    │
│  │   • All members have final names from Renamer                   │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M3: Names.ValidateAliases()                                     │    │
│  │   • Import aliases don't collide                                │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M4: Names.ValidateIdentifiers() [PG_ID_001]                     │    │
│  │   • ALL identifiers sanitized (reserved words)                  │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M5: Names.ValidateOverloadCollisions() [PG_OV_001]              │    │
│  │   • No duplicate erased signatures                              │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M6: Views.ValidateIntegrity() [PG_VIEW_001-003]                 │    │
│  │   • 3 HARD RULES: non-empty, unique, valid name                │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M7: Constraints.EmitDiagnostics() [PG_CT_001-002]               │    │
│  │   • Constructor constraint losses                               │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M8: Views.ValidateMemberScoping() [PG_NAME_003-004]             │    │
│  │   • No collisions within view                                   │    │
│  │   • ViewOnly names don't shadow class surface                   │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M9: Scopes.ValidateEmitScopeInvariants() [PG_INT_002-003]       │    │
│  │   • No dual-scope members                                       │    │
│  │   • ClassSurface members have no SourceInterface                │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M10: Scopes.ValidateScopeMismatches() [PG_SCOPE_003-004]        │    │
│  │   • Scope keys well-formed                                      │    │
│  │   • Scope kind matches EmitScope                                │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M11: Names.ValidateClassSurfaceUniqueness() [PG_NAME_005]       │    │
│  │   • No duplicates after deduplication                           │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M12: Finalization.Validate() [PG_FIN_001-009]                   │    │
│  │   • ALL symbols have proper placement and naming                │    │
│  │   • FINAL check before emission                                 │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M13: Types.ValidatePrinterNameConsistency() [PG_PRINT_001]      │    │
│  │   • Printer names match Renamer names                           │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M14: Types.ValidateTypeMapCompliance() [PG_TYPEMAP_001]         │    │
│  │   • No unsupported special forms                                │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M15: Types.ValidateExternalTypeResolution() [PG_LOAD_001]       │    │
│  │   • All external types resolvable                               │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M16: ImportExport.ValidatePublicApiSurface() [PG_API_001-002]   │    │
│  │   • Public API doesn't expose internal types                    │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M17: ImportExport.ValidateImportCompleteness() [PG_IMPORT_001]  │    │
│  │   • All foreign types imported                                  │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ M18: ImportExport.ValidateExportCompleteness() [PG_EXPORT_001]  │    │
│  │   • All imported types exported by source                       │    │
│  └─────────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────────┘
                                   │
                                   ▼
┌──────────────────────────────────────────────────────────────────────────┐
│ STEP 3: REPORTING                                                         │
├──────────────────────────────────────────────────────────────────────────┤
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Print diagnostic summary table (by code)                        │    │
│  │   TBG103:     0 - View member collision within view scope       │    │
│  │   TBG203:    12 - Interface conformance failures                │    │
│  │   TBG310:   241 - Property covariance (TS limitation)           │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ WriteDiagnosticsFile()                                          │    │
│  │   • .tests/phasegate-diagnostics.txt                            │    │
│  │   • Summary + interface conformance + all diagnostics           │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ WriteSummaryJson()                                              │    │
│  │   • .tests/phasegate-summary.json                               │    │
│  │   • Totals + diagnostic counts by code                          │    │
│  └─────────────────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────────────────┐    │
│  │ Check error count                                               │    │
│  │   if (ErrorCount > 0) → FAIL BUILD                              │    │
│  └─────────────────────────────────────────────────────────────────┘    │
└──────────────────────────────────────────────────────────────────────────┘
                                   │
                    ┌──────────────┴──────────────┐
                    │                             │
                    ▼                             ▼
         ┌──────────────────┐         ┌──────────────────┐
         │  ErrorCount > 0  │         │  ErrorCount == 0 │
         │                  │         │                  │
         │  SKIP EMIT PHASE │         │  PROCEED TO EMIT │
         └──────────────────┘         └──────────────────┘
```

---

## 6. Diagnostic Output Files

### `.tests/phasegate-summary.json`

**Purpose:** Machine-readable summary for CI/snapshot comparison and trending.

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
    "TBG105": 0,
    "TBG203": 12,
    "TBG310": 241,
    "TBG406": 0,
    "TBG407": 0,
    "TBG511": 0,
    "TBG512": 0,
    "TBG513": 0,
    "TBG702": 0,
    "TBG703": 0,
    "TBG710": 0,
    "TBG711": 0,
    "TBG712": 0,
    "TBG713": 0,
    "TBG714": 0,
    "TBG715": 0,
    "TBG716": 0,
    "TBG717": 0,
    "TBG718": 0,
    "TBG719": 0,
    "TBG720": 0,
    "TBG721": 0,
    "TBG850": 0,
    "TBG851": 0,
    "TBG860": 0,
    "TBG870": 0,
    "TBG880": 0
  }
}
```

**Used for:**
- Automated CI checks (fail if errors increase)
- Trending over time (warning/info counts)
- Snapshot comparison (detect regressions)

---

### `.tests/phasegate-diagnostics.txt`

**Purpose:** Human-readable detailed diagnostics for debugging.

**Format:**
```
================================================================================
PhaseGate Detailed Diagnostics
Generated: 2025-11-10 12:34:56
================================================================================

Summary:
  Errors: 0
  Warnings: 12
  Info: 241
  Sanitized identifiers: 47

--------------------------------------------------------------------------------
Interface Conformance Issues (12 types)
--------------------------------------------------------------------------------

System.Collections.Generic.List_1:
  Missing method Add from ICollection_1
  Missing method Remove from ICollection_1
  Method Clear from ICollection_1 has incompatible TS signature

System.Collections.Generic.Dictionary_2:
  Missing property Keys from IDictionary_2
  Missing property Values from IDictionary_2
  Method Add from IDictionary_2 has incompatible TS signature

... (10 more types) ...

--------------------------------------------------------------------------------
All Diagnostics
--------------------------------------------------------------------------------

WARNING: [TBG203] System.Collections.Generic.List_1 has 3 interface conformance issues (see diagnostics file)
WARNING: [TBG203] System.Collections.Generic.Dictionary_2 has 3 interface conformance issues (see diagnostics file)
INFO: [TBG310] System.Collections.Generic.List_1 has 2 property covariance issues (TS doesn't support property covariance)
INFO: [TBG310] System.Array has 1 property covariance issues (TS doesn't support property covariance)
... (237 more INFO diagnostics) ...
```

**Sections:**
1. **Summary** - Totals (errors, warnings, info, sanitized)
2. **Interface Conformance Issues** - Detailed breakdown per type
3. **All Diagnostics** - Chronological list of all diagnostics

**Used for:**
- Debugging validation failures
- Understanding conformance issues
- Manual review of warnings/info

---

## Summary

PhaseGate is the **FINAL validation checkpoint** before emission. It runs **20+ validation modules** checking:

- **Names:** All types/members have final names, no collisions, reserved words sanitized
- **Scopes:** EmitScope assignment correct, no dual-role members, scope keys well-formed
- **Views:** All ViewOnly members have views, no collisions, 3 hard rules enforced
- **Types:** Printer/Renamer consistency, no unsupported forms, external types resolvable
- **Imports/Exports:** All foreign types imported, all imported types exported, public API clean
- **Constraints:** Constructor constraint losses detected
- **Finalization:** EVERY symbol has proper placement and naming (PG_FIN_001-009)

**If validation fails (ErrorCount > 0):**
- Emit phase is SKIPPED
- Detailed diagnostics written to `.tests/phasegate-diagnostics.txt`
- Summary JSON written to `.tests/phasegate-summary.json`
- Build fails with `ValidationFailed` error

**PhaseGate ensures:**
- **100% coverage** - Every symbol validated
- **Zero leaks** - Nothing escapes without validation
- **Type safety** - All emitted code is correct TypeScript
- **Debuggability** - Detailed diagnostics for every failure
