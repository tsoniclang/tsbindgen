# Plan Phase - Concise

## Overview

Final preparation before emission:
- Build cross-namespace dependency graph
- Plan imports/exports with alias resolution
- Compute TypeScript module paths
- Topological emission order
- Audit constraint losses
- Validate 50+ PhaseGate rules

**Input:** Shaped `SymbolGraph`
**Output:** `ImportPlan`, `EmitOrder`, validation reports

---

## ImportPlanner.cs

Plans TypeScript import/export statements with name collision resolution.

### Key Methods

**PlanImports()** - Creates `ImportPlan` with three dictionaries:
- `NamespaceImports` - namespace → import statements
- `NamespaceExports` - namespace → export statements
- `ImportAliases` - namespace → (name → alias)

**PlanNamespaceImports()** - Foreign type detection:
1. Lookup dependencies in `ImportGraphData.NamespaceDependencies`
2. Filter `CrossNamespaceReferences` for this namespace
3. Get relative path via `PathPlanner.GetSpecifier()`
4. Check collisions, assign aliases via `DetermineAlias()`
5. Build `ImportStatement(Path, Target, TypeImports)`

**Alias strategy:** Assign when name collision or policy requires. Format: `{TypeName}_{TargetNamespaceShort}`

**PlanNamespaceExports()** - Catalog public types:
- Filter to `Accessibility.Public`
- Map TypeKind → ExportKind (Class/Interface/Enum/Type)
- Create `ExportStatement(Name, Kind)`

### Records

```csharp
ImportStatement(ImportPath, TargetNamespace, List<TypeImport>)
TypeImport(TypeName, Alias?)
ExportStatement(ExportName, ExportKind)
```

---

## ImportGraph.cs

Builds cross-namespace dependency graph via signature analysis.

### Build() Algorithm

1. Create empty `ImportGraphData`:
   - `NamespaceDependencies` - namespace → dependent namespaces
   - `NamespaceTypeIndex` - namespace → public type names
   - `CrossNamespaceReferences` - detailed reference list

2. Build reverse index: `BuildNamespaceTypeIndex()`
   - Public types only (internal won't be emitted)

3. Analyze dependencies: `AnalyzeNamespaceDependencies()`
   - Scan all type signatures recursively

### AnalyzeNamespaceDependencies()

For each public type:
1. **Base class:** `CollectTypeReferences(type.BaseType)` → `ReferenceKind.BaseClass`
2. **Interfaces:** Each interface → `ReferenceKind.Interface`
3. **Generic constraints:** Each constraint → `ReferenceKind.GenericConstraint`
4. **Members:** `AnalyzeMemberDependencies()`

### AnalyzeMemberDependencies()

Scans signatures:
- **Methods:** Return type (`MethodReturn`), parameters (`MethodParameter`), generic constraints
- **Properties:** Property type (`PropertyType`), indexer parameters
- **Fields:** Field type (`FieldType`)
- **Events:** Handler type (`EventType`)

### CollectTypeReferences() - Recursive Deep Scan

Critical function that finds ALL foreign types via tree traversal:

**NamedTypeReference:**
- Find namespace via `FindNamespaceForType()`
- **Recurse into type arguments** (e.g., `Dictionary<string, List<int>>` finds both)

**NestedTypeReference:** Use full name (`Outer.Inner`)

**ArrayTypeReference:** Recurse into element type

**PointerTypeReference / ByRefTypeReference:** Recurse into pointee/referenced type

**GenericParameterReference:** Skip (local, no import)

### FindNamespaceForType()

Lookup via `NamespaceTypeIndex`. Returns null if external/built-in.

### ImportGraphData Structure

```csharp
NamespaceDependencies: Dict<string, HashSet<string>>  // namespace edges
NamespaceTypeIndex: Dict<string, HashSet<string>>     // namespace → type names (reverse index)
CrossNamespaceReferences: List<CrossNamespaceReference>  // detailed references
```

### CrossNamespaceReference

```csharp
record CrossNamespaceReference(
    SourceNamespace, SourceType,
    TargetNamespace, TargetType,
    ReferenceKind)  // BaseClass|Interface|GenericConstraint|MethodReturn|etc.
```

---

## EmitOrderPlanner.cs

Deterministic emission order using `Renamer.GetFinalTypeName()` for stability.

### PlanOrder() Algorithm

1. Sort namespaces alphabetically
2. For each namespace: `OrderTypes()`
3. Return `EmitOrder` with `NamespaceEmitOrder` list

### OrderTypes() - Stable Sort

Primary keys (in order):
1. **Kind sort order:** Enums(0) → Delegates(1) → Interfaces(2) → Structs(3) → Classes(4) → StaticNamespaces(5)
2. **Final TS name** from `ctx.Renamer.GetFinalTypeName()` (post-collision)
3. **Arity** (generic parameter count)

For each type:
- Recurse into nested types
- Order members via `OrderMembers()`

**Rationale:** Forward-ref safe (enums/delegates first), alphabetical by final name (git-friendly)

### OrderMembers() - Category Order

Emission order: Constructors → Fields → Properties → Events → Methods

Within category sort by:
1. **IsStatic** (instance first, then static)
2. **Final TS member name** via `ctx.Renamer.GetFinalMemberName()` (uses `ScopeFactory.ClassSurface/StaticSurface`)
3. **Arity** (method generics)
4. **Canonical signature** (from `StableId.CanonicalSignature` - disambiguates overloads)

**Filtering:** Only `ClassSurface`/`StaticSurface` scopes (excludes view-only members)

### Records

```csharp
EmitOrder(Namespaces: List<NamespaceEmitOrder>)
NamespaceEmitOrder(Namespace, OrderedTypes: List<TypeEmitOrder>)
TypeEmitOrder(Type, OrderedMembers, OrderedNestedTypes)  // recursive
MemberEmitOrder(Constructors, Fields, Properties, Events, Methods)  // all pre-sorted
```

---

## PathPlanner.cs

Computes relative TypeScript module paths for imports.

### GetSpecifier() Algorithm

**Input:** Source/target namespace names (empty = root)

**Rules:**
1. Determine root: `isRoot = string.IsNullOrEmpty(namespace)`
2. Target path:
   - Root: `targetDir="_root"`, `targetFile="index"`
   - Named: `targetDir=namespace`, `targetFile="internal/index"`
3. Relative path:
   - Root → Root: `./_root/index`
   - Root → Named: `./{namespace}/internal/index`
   - Named → Root: `../_root/index`
   - Named → Named: `../{namespace}/internal/index`

**Why always `internal/index`:** Public API re-exports from internal. Imports need full definitions.

**Examples:**

| Source | Target | Specifier |
|--------|--------|-----------|
| (root) | (root) | `./_root/index` |
| (root) | `System` | `./System/internal/index` |
| `System.Collections` | (root) | `../_root/index` |
| `System.Collections` | `System.Text` | `../System.Text/internal/index` |

---

## InterfaceConstraintAuditor.cs

Audits constructor constraint loss per (Type, Interface) pair.

**M4/M5 Fix:** One finding per interface implementation, not per view member.

### Audit() Algorithm

1. For each type implementing interfaces:
2. For each interface reference:
   - Resolve interface: `ResolveInterface()`
   - Check constraints: `CheckInterfaceConstraints()`
   - Add finding if detected

### CheckInterfaceConstraints()

1. Skip if interface has no generic parameters
2. For each generic parameter:
   - Check `(gp.SpecialConstraints & GenericParameterConstraints.DefaultConstructor) != 0`
   - If true: **Constructor constraint loss detected**

3. Create finding:
```csharp
InterfaceConstraintFinding {
    ImplementingTypeStableId, InterfaceStableId,
    LossKind = ConstraintLossKind.ConstructorConstraintLoss,
    GenericParameterName, TypeFullName, InterfaceFullName
}
```

**What's lost:**
```csharp
// C#: interface IFactory<T> where T : new()
// TypeScript: interface IFactory_1<T>  // No "new()" constraint
```

**Why it matters:** TypeScript can't enforce `new()`. Metadata tracks it for runtime binding.

### ResolveInterface()

Search all namespaces for type with matching CLR name and `TypeKind.Interface`. Returns null if external.

---

## TsAssignability.cs

TypeScript structural typing for erased type shapes.

### IsAssignable() Rules

1. **Exact match:** `source.Equals(target)`
2. **Unknown type:** Conservative (assume compatible)
3. **Type parameter:** Match by name (`T` = `T`)
4. **Array covariance:** `IsAssignable(elemSource, elemTarget)` (readonly arrays)
5. **Generic application:**
   - Base generic must match: `List<>` = `List<>`
   - Type arguments pairwise (invariant currently)
6. **Named widening:** `IsWideningConversion()`

### IsWideningConversion()

Known widenings:
- Same type
- All numeric types → all numeric types (map to `number` brand)
- Everything → `System.Object`
- `System.ValueType` → `System.Object`

### IsMethodAssignable()

Checks:
1. Name match
2. Arity match (generic parameters)
3. Parameter count match
4. **Return type covariance:** `IsAssignable(sourceReturn, targetReturn)`
5. **Parameter invariance:** Both directions assignable (stricter than TS contravariance)

### IsPropertyAssignable()

1. Name match
2. **Readonly covariance:** `IsAssignable(sourceProp, targetProp)` (safe, can't write)
3. **Mutable invariance:** Exact type match (prevents unsound reads/writes)

---

## TsErase.cs

Erases CLR specifics to TypeScript-level signatures.

### EraseMember(MethodSymbol)

1. Take `method.TsEmitName`, `method.Arity`
2. **Erase parameters:** `EraseType(p.Type)` - **removes ref/out**
3. **Erase return:** `EraseType(method.ReturnType)`

Result: `TsMethodSignature` (no CLR concepts)

### EraseMember(PropertySymbol)

1. Take `property.TsEmitName`
2. Erase type: `EraseType(property.PropertyType)`
3. Readonly: `!property.HasSetter`

### EraseType() - Type Erasure

**NamedTypeReference (generic):** `GenericApplication(Named(fullName), typeArgs.Select(EraseType))`

**NamedTypeReference (simple):** `Named(fullName)`

**NestedTypeReference:** `Named(nested.FullReference.FullName)`

**GenericParameterReference:** `TypeParameter(name)`

**ArrayTypeReference:** `Array(EraseType(elementType))`

**PointerTypeReference:** `EraseType(pointeeType)` - **erase pointer**

**ByRefTypeReference:** `EraseType(referencedType)` - **erase ref/out**

**Fallback:** `Unknown(description)`

### TsTypeShape Hierarchy

```csharp
abstract record TsTypeShape {
    Named(FullName)
    TypeParameter(Name)
    Array(ElementType)
    GenericApplication(GenericType, TypeArguments)
    Unknown(Description)
}
```

---

## PhaseGate.cs

Master validation orchestrator - 50+ correctness rules.

### Validate() Flow

1. **Create ValidationContext** (error/warning counts, diagnostics)

2. **Core validation checks** (delegated to `Validation.Core`):
   - Type/member naming rules
   - Generic parameter constraints
   - Interface conformance
   - Inheritance hierarchies
   - EmitScope assignments
   - Import consistency
   - Policy compliance

3. **PhaseGate Hardening** (module checks):

   **M1:** Identifier sanitization (`Names.ValidateIdentifiers()` - PG_NAME_001/002)

   **M2:** Overload collisions (`Names.ValidateOverloadCollisions()` - PG_NAME_006)

   **M3:** View integrity (`Views.Validate()`, `Views.ValidateIntegrity()` - PG_VIEW_001/002/003)

   **M4:** Constructor constraints (`Constraints.EmitDiagnostics()` - PG_CT_001/002)

   **M5:** Scoping/naming:
   - `Views.ValidateMemberScoping()` - PG_NAME_003/004
   - `Scopes.ValidateEmitScopeInvariants()` - PG_INT_002/003
   - `Scopes.ValidateScopeMismatches()` - PG_SCOPE_003/004
   - `Names.ValidateClassSurfaceUniqueness()` - PG_NAME_005

   **M6:** Finalization (`Finalization.Validate()` - PG_FIN_001..009)

   **M7:** Type references:
   - `Types.ValidateTypeMapCompliance()` - PG_TYPEMAP_001 (MUST RUN EARLY)
   - `Types.ValidateExternalTypeResolution()` - PG_LOAD_001 (AFTER TypeMap)
   - `Types.ValidatePrinterNameConsistency()` - PG_PRINT_001

   **M8:** Public API (`ImportExport.ValidatePublicApiSurface()` - PG_API_001/002, BEFORE imports)

   **M9:** Import completeness (`ImportExport.ValidateImportCompleteness()` - PG_IMPORT_001)

   **M10:** Export completeness (`ImportExport.ValidateExportCompleteness()` - PG_EXPORT_001)

4. **Report results:** Error/warning/info counts, sanitized name count

5. **Print diagnostic summary:** Group by code, sort by frequency, show descriptions

6. **Handle errors:** If errors > 0, write diagnostic files and fail build

7. **Write outputs:** Detailed report + machine-readable summary JSON

### Validation Module Structure

Delegated to `Validation/` modules:
- **Core.cs** - 8 core categories
- **Names.cs** - 5 checks (collision, sanitization, uniqueness)
- **Views.cs** - 4 checks (integrity, scoping)
- **Scopes.cs** - 3 checks (EmitScope validation)
- **Types.cs** - 3 checks (TypeMap, external resolution, printer consistency)
- **ImportExport.cs** - 3 checks (API surface, import/export completeness)
- **Constraints.cs** - 2 diagnostics (constructor constraint loss)
- **Finalization.cs** - 9 checks (finalization sweep)
- **Context.cs** - Diagnostic tracking
- **Shared.cs** - Utilities

**Diagnostic format:** `PG_CATEGORY_NNN` (e.g., PG_NAME_001, PG_VIEW_003)

**Full details:** See `07-phasegate.md`

---

## Key Algorithms

### Import Graph Construction

**Build cross-namespace dependency graph:**

1. **Reverse index:** For each namespace, collect public type CLR names → `NamespaceTypeIndex`
2. **Scan types:** For each public type: base class, interfaces, constraints, member signatures
3. **Recursive collection:**
   - Descend into generic type arguments
   - Descend into array elements
   - Erase pointers/byrefs
   - Skip generic parameters (local)
4. **Namespace resolution:** Lookup in NamespaceTypeIndex, add edge if foreign namespace
5. **Aggregation:** `NamespaceDependencies` (edges) + `CrossNamespaceReferences` (detailed)

**Complexity:** O(N × M) where N = types, M = avg references per type

### Topological Sort (NOT IMPLEMENTED)

Current implementation: Alphabetical namespace + kind/name sort (TypeScript allows forward refs)

**If needed:**
1. Build dependency graph (type → referenced types)
2. Initialize in-degree
3. Zero in-degree → queue
4. Dequeue → output → decrement dependents → enqueue if zero
5. Cycle detection: types remain = cycle

### Relative Path Computation

**Compute module path from source to target namespace:**

1. **Directory structure:**
   - Root: `_root/` (no internal subdirectory)
   - Named: `{Namespace}/internal/index.d.ts`

2. **Relative path:**
   - Same level: `./` prefix
   - Different level: `../` prefix
   - Always target `internal/index`

3. **Special cases:**
   - Root → Root: `./_root/index`
   - Root → Named: `./{namespace}/internal/index`
   - Named → Root: `../_root/index`
   - Named → Named: `../{namespace}/internal/index`

**Why `internal/index`:** Public API re-exports. Imports need full definitions.

### Constraint Loss Detection

**Detect when TypeScript loses C# `new()` constraints:**

1. Get interface generic parameters (skip if none)
2. Check `SpecialConstraints & GenericParameterConstraints.DefaultConstructor`
3. If true: Create `InterfaceConstraintFinding`:
   - `LossKind = ConstructorConstraintLoss`
   - Record type/interface StableIds
   - Record generic parameter name
4. PhaseGate emits PG_CT_001 (ERROR)
5. Metadata sidecar tracks for runtime binding

**Why `new()` specifically:** TypeScript has no equivalent. Must track for runtime.

**Other constraints not tracked:** `class`, `struct` (future expansion)

---

## Summary

**Plan phase outputs:**
1. **ImportGraph** - Complete namespace dependencies
2. **ImportPlan** - TypeScript import/export statements with aliases
3. **EmitOrder** - Deterministic emission order
4. **PathPlanner** - Relative module paths
5. **ConstraintFindings** - Constructor constraint losses
6. **PhaseGate validation** - 50+ rules enforced

**PhaseGate categories:**
- Name correctness (5 checks)
- View integrity (4 checks)
- EmitScope validation (3 checks)
- Type references (3 checks)
- Import/export completeness (3 checks)
- Constraint tracking (2 diagnostics)
- Finalization (9 checks)
- Policy compliance

**After Plan:**
- Symbol graph validated
- Import dependencies resolved
- Emission order determined
- All invariants hold
- Ready for Emit phase

**Next:** Emit phase (generate `.d.ts`, `.metadata.json`, `.bindings.json`)

**See also:**
- `07-phasegate.md` - Detailed PhaseGate validation
- `05-phase-shape.md` - Symbol shaping before validation
- `08-phase-emit.md` - File emission after validation
