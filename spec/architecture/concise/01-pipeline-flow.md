# Pipeline Flow

**Entry**: `SinglePhaseBuilder.Build()` (`src/tsbindgen/SinglePhase/SinglePhaseBuilder.cs`)

## Execution Sequence

```
BuildContext.Create()
  ↓ LOAD
  ↓ NORMALIZE
  ↓ SHAPE (16 passes)
  ↓ NAME RESERVATION
  ↓ PLAN
  ↓ OVERLOAD UNIFICATION
  ↓ CONSTRAINT AUDIT
  ↓ PHASEGATE VALIDATION
  ↓ EMIT (if no errors)
```

---

## PHASE 1: LOAD

**Input**: `string[]` assemblyPaths
**Output**: `SymbolGraph` (pure CLR, TsEmitName=null, EmitScope undetermined)

- Load transitive closure (seed + dependencies)
- Reflect via `ReflectionReader.ReadAssemblies()`
- Substitute closed generic interface members

**Files**: `src/tsbindgen/SinglePhase/Load/*.cs`

---

## PHASE 2: NORMALIZE

**Input**: `SymbolGraph`
**Output**: `SymbolGraph` (with indices)

- `graph.WithIndices()` → NamespaceIndex, TypeIndex (includes nested types)
- Build GlobalInterfaceIndex, InterfaceDeclIndex

**Files**: `src/tsbindgen/SinglePhase/Shape/GlobalInterfaceIndex.cs`, `InterfaceDeclIndex.cs`

---

## PHASE 3: SHAPE (16 Passes)

**Input**: `SymbolGraph` (indexed)
**Output**: `SymbolGraph` (TS-ready, EmitScope set, TsEmitName=null)

**Purpose**: CLR → TS semantics (flatten interfaces, synthesize members, resolve diamonds, deduplicate, determine EmitScope)

### 16 Passes (Sequential)

1. **GlobalInterfaceIndex.Build()** - Interface inheritance lookup (side effect → BuildContext)
2. **InterfaceDeclIndex.Build()** - Member declaration lookup (side effect → BuildContext)
3. **StructuralConformance.Analyze()** - Synthesize ViewOnly for structural conformance
4. **InterfaceInliner.Inline()** - Flatten interface hierarchies (copy inherited members)
5. **ExplicitImplSynthesizer.Synthesize()** - Synthesize ViewOnly for explicit impls
6. **DiamondResolver.Resolve()** - Resolve diamond inheritance
7. **BaseOverloadAdder.AddOverloads()** - Add base overloads for interface compat
8. **StaticSideAnalyzer.Analyze()** - Analyze static members/ctors (side effect)
9. **IndexerPlanner.Plan()** - Mark indexers for omission
10. **HiddenMemberPlanner.Plan()** - Rename hidden members (C# `new`) → Renamer decisions
11. **FinalIndexersPass.Run()** - Remove leaked indexer properties
12. **ClassSurfaceDeduplicator.Deduplicate()** - Pick winner, demote rest to ViewOnly
13. **ConstraintCloser.Close()** - Complete generic constraint closures
14. **OverloadReturnConflictResolver.Resolve()** - Resolve conflicting return types
15. **ViewPlanner.Plan()** - Plan explicit interface views (one per interface)
16. **MemberDeduplicator.Deduplicate()** - Final deduplication

**Dependencies**:
- StructuralConformance BEFORE InterfaceInliner (needs original hierarchy)
- InterfaceInliner BEFORE ExplicitImplSynthesizer (needs flattened)
- IndexerPlanner BEFORE FinalIndexersPass
- ClassSurfaceDeduplicator BEFORE ConstraintCloser
- OverloadReturnConflictResolver BEFORE ViewPlanner
- ViewPlanner BEFORE MemberDeduplicator

**Files**: `src/tsbindgen/SinglePhase/Shape/*.cs`

---

## PHASE 3.5: NAME RESERVATION

**Input**: `SymbolGraph` (TS-ready, unnamed)
**Output**: `SymbolGraph` (TsEmitName assigned)

- For types: Apply syntax transforms → `Renamer.ReserveTypeName()`
- For members: Skip if renamed by earlier passes; syntax transforms (`` ` `` → `_`, `+` → `_`); reserved word sanitization (`_` suffix); `Renamer.ReserveMemberName()` with scope
- Audit completeness (fail if missing rename decision)
- `Application.ApplyNamesToGraph()` → set TsEmitName

**Scopes**:
- Types: `ScopeFactory.Namespace(name, NamespaceArea.Internal)`
- Class surface: `ScopeFactory.ClassSurface(type, isStatic)`
- Views: `ScopeFactory.View(type, interfaceStableId)`

**Files**: `src/tsbindgen/SinglePhase/Normalize/NameReservation.cs`, `Normalize/Naming/*.cs`

---

## PHASE 4: PLAN

**Input**: `SymbolGraph` (fully named)
**Output**: `EmissionPlan` (SymbolGraph + ImportPlan + EmitOrder)

- Build ImportGraph (cross-namespace deps)
- `ImportPlanner.PlanImports()` → imports, exports, aliases
- `EmitOrderPlanner.PlanOrder()` → stable emission order

**Files**: `src/tsbindgen/SinglePhase/Plan/ImportGraph.cs`, `ImportPlanner.cs`, `EmitOrderPlanner.cs`

---

## PHASE 4.5: OVERLOAD UNIFICATION

**Input**: `SymbolGraph`
**Output**: `SymbolGraph` (overloads unified)

- Group by name/scope, merge compatible returns, preserve distinct overloads for different returns

**Files**: `src/tsbindgen/SinglePhase/Plan/OverloadUnifier.cs`

---

## PHASE 4.6: CONSTRAINT AUDIT

**Input**: `SymbolGraph`
**Output**: `ConstraintFindings`

- Audit constructor constraints per (Type, Interface) pair

**Files**: `src/tsbindgen/SinglePhase/Plan/InterfaceConstraintAuditor.cs`

---

## PHASE 4.7: PHASEGATE VALIDATION

**Input**: `SymbolGraph` + `ImportPlan` + `ConstraintFindings`
**Output**: Side effect → `ctx.Diagnostics` (ERROR/WARNING/INFO)

- 26 validation checks
- Any ERROR blocks Phase 5 → `Success = false`

**Files**: `src/tsbindgen/SinglePhase/Plan/PhaseGate.cs`, `Plan/Validation/*.cs` (26 validators)

---

## PHASE 5: EMIT

**Input**: `EmissionPlan` (validated)
**Output**: File I/O

**Gate**: Only executes if `!ctx.Diagnostics.HasErrors()`

- `SupportTypesEmit` → `_support/types.d.ts`
- Per namespace (emission order):
  - `InternalIndexEmitter` → `<ns>/internal/index.d.ts`
  - `FacadeEmitter` → `<ns>/index.d.ts`
  - `MetadataEmitter` → `<ns>/metadata.json`
  - `BindingEmitter` → `<ns>/bindings.json`
  - `ModuleStubEmitter` → `<ns>/index.js`

**Files**: `src/tsbindgen/SinglePhase/Emit/*.cs`

---

## Data Flow

```
string[] assemblies
  │
  ↓ LOAD
SymbolGraph (CLR only, TsEmitName=null, EmitScope undetermined)
  │
  ↓ NORMALIZE
SymbolGraph (indexed, TsEmitName=null)
  │
  ↓ SHAPE (16 passes)
SymbolGraph (TS-ready, EmitScope set, TsEmitName=null)
  │
  ↓ NAME RESERVATION
SymbolGraph (fully named, TsEmitName set)
  │
  ↓ PLAN
EmissionPlan (Graph + ImportPlan + EmitOrder)
  │
  ↓ OVERLOAD UNIFICATION
SymbolGraph (overloads merged)
  │
  ↓ CONSTRAINT AUDIT
ConstraintFindings
  │
  ↓ PHASEGATE
ctx.Diagnostics (ERROR/WARNING/INFO)
  │
  ├─ Has errors? → Return Success=false
  │
  ↓ No errors
EMIT → Files (*.d.ts, *.json, *.js)
```

---

## Data Transformations Table

| Phase | Input → Output | Pure? | Key Transform |
|-------|----------------|-------|---------------|
| **LOAD** | `string[]` → `SymbolGraph` | ✓ | Reflection → Graph |
| **NORMALIZE** | `SymbolGraph` → `SymbolGraph` | ✓ | Build indices |
| **SHAPE** | `SymbolGraph` → `SymbolGraph` | ✓ | 16 passes: CLR → TS, set EmitScope |
| **NAME RESERVATION** | `SymbolGraph` → `SymbolGraph` | ✓* | Reserve names, set TsEmitName |
| **PLAN** | `SymbolGraph` → `EmissionPlan` | ✓ | Import graph, emission order |
| **OVERLOAD UNIFICATION** | `SymbolGraph` → `SymbolGraph` | ✓ | Merge overloads |
| **CONSTRAINT AUDIT** | `SymbolGraph` → `ConstraintFindings` | ✓ | Audit constraints |
| **PHASEGATE** | Multi-input → Side effect | ✗ | 26 validators → ctx.Diagnostics |
| **EMIT** | `EmissionPlan` → File I/O | ✗ | Generate files |

*Side effect: Populates Renamer; Pure: Returns new graph

---

## Critical Sequencing

**Shape Dependencies**:
- StructuralConformance BEFORE InterfaceInliner (needs hierarchy)
- InterfaceInliner BEFORE ExplicitImplSynthesizer (needs flattened)
- IndexerPlanner BEFORE FinalIndexersPass (mark before remove)
- ClassSurfaceDeduplicator BEFORE ConstraintCloser (affects constraints)
- OverloadReturnConflictResolver BEFORE ViewPlanner (conflicts first)
- ViewPlanner BEFORE MemberDeduplicator (plan before dedup)

**Name Reservation**: AFTER Shape (EmitScope set), BEFORE Plan (PhaseGate needs TsEmitName)

**PhaseGate**: AFTER transformations + naming, BEFORE Emit

**Emit**: ONLY if `!ctx.Diagnostics.HasErrors()`

---

## Immutability Pattern

Every phase (except Emit): `TOutput PhaseFunction(BuildContext ctx, TInput input)` - immutable input, returns new output

**Benefits**: No mutations, parallelizable, debuggable, clear flow

**Example**:
```csharp
public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph) {
    var newNamespaces = graph.Namespaces
        .Select(ns => ns with { Types = FlattenTypes(ns.Types) })
        .ToImmutableArray();
    return graph with { Namespaces = newNamespaces };
}
```

---

## Output Files Per Namespace

1. **index.d.ts** - TS declarations (namespaces, classes, interfaces, enums, delegates, generics, branded types)
2. **metadata.json** - CLR info (virtual/override, static, ref/out, intentional omissions)
3. **bindings.json** - CLR name → TS name mappings
4. **typelist.json** - Actually emitted (completeness verification)
5. **snapshot.json** - Post-transform state (verification)

---

## Related Docs

- [pipeline.md](../pipeline.md) - Full reference
- [overview.md](../overview.md) - Architecture
- [scopes.md](../../scopes.md) - Renamer scopes
