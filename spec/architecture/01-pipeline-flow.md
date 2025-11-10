# Pipeline Flow: Sequential Phase Execution

## Overview

The tsbindgen pipeline executes in **strict sequential order** through 5 main phases, with sub-phases executing in deterministic order within each main phase. Each phase is **pure** (returns new immutable data) except for Phase 5 (Emit) which has file I/O side effects.

**Entry Point**: `SinglePhaseBuilder.Build()` in `src/tsbindgen/SinglePhase/SinglePhaseBuilder.cs`

## Sequential Phase Execution

The exact order of execution as implemented in `SinglePhaseBuilder.Build()`:

```
1. BuildContext.Create()
   ↓
2. PHASE 1: LOAD
   ↓
3. PHASE 2: NORMALIZE (Build Indices)
   ↓
4. PHASE 3: SHAPE (14 transformation passes)
   ↓
5. PHASE 3.5: NAME RESERVATION
   ↓
6. PHASE 4: PLAN
   ↓
7. PHASE 4.5: OVERLOAD UNIFICATION
   ↓
8. PHASE 4.6: INTERFACE CONSTRAINT AUDIT
   ↓
9. PHASE 4.7: PHASEGATE VALIDATION
   ↓
10. PHASE 5: EMIT (if no errors)
```

---

## Phase 1: LOAD

**Purpose**: Reflect over .NET assemblies using MetadataLoadContext to build initial SymbolGraph

**Input**:
- `string[]` assemblyPaths

**Output**:
- `SymbolGraph` (pure CLR facts, no TypeScript concepts)

**Mutability**: Pure function (returns new immutable SymbolGraph)

**Key Operations**:
1. Create `MetadataLoadContext` with reference paths
2. Load transitive closure of assemblies (seed + dependencies)
3. Reflect over all types and members via `ReflectionReader.ReadAssemblies()`
4. Substitute closed generic interface members (`InterfaceMemberSubstitution.SubstituteClosedInterfaces()`)
5. Build initial SymbolGraph with:
   - Namespaces
   - Types (classes, interfaces, structs, enums, delegates)
   - Members (methods, properties, fields, events)
   - Type references (base types, interfaces, generic arguments)

**Files Involved**:
- `src/tsbindgen/SinglePhase/Load/AssemblyLoader.cs`
- `src/tsbindgen/SinglePhase/Load/ReflectionReader.cs`
- `src/tsbindgen/SinglePhase/Load/InterfaceMemberSubstitution.cs`

**Data Characteristics**:
- Pure CLR metadata (no TypeScript names yet)
- `TsEmitName` is null for all symbols
- `EmitScope` not yet determined
- Interface hierarchies not yet flattened

---

## Phase 2: NORMALIZE (Build Indices)

**Purpose**: Build lookup tables for efficient cross-reference resolution

**Input**:
- `SymbolGraph` (from Phase 1)

**Output**:
- `SymbolGraph` (with indices populated)

**Mutability**: Pure function (returns new immutable SymbolGraph with indices)

**Key Operations**:
1. Call `graph.WithIndices()` to populate:
   - `NamespaceIndex`: namespace name → NamespaceSymbol
   - `TypeIndex`: CLR full name → TypeSymbol (includes nested types)
2. Build `GlobalInterfaceIndex` (interface inheritance lookups)
3. Build `InterfaceDeclIndex` (interface member declarations)

**Files Involved**:
- `src/tsbindgen/SinglePhase/Model/SymbolGraph.cs` (`WithIndices()` method)
- `src/tsbindgen/SinglePhase/Shape/GlobalInterfaceIndex.cs`
- `src/tsbindgen/SinglePhase/Shape/InterfaceDeclIndex.cs`

**Data Transformations**:
- Input: SymbolGraph with empty indices
- Output: SymbolGraph with populated indices (no other changes)

---

## Phase 3: SHAPE (14 Transformation Passes)

**Purpose**: Transform CLR semantics → TypeScript semantics

**Input**:
- `SymbolGraph` (from Phase 2, with indices)

**Output**:
- `SymbolGraph` (TypeScript-ready, but still no `TsEmitName` assigned)

**Mutability**: Pure function (each pass returns new immutable SymbolGraph)

**Key Transformations**:
- Interface flattening
- Explicit interface implementation synthesis
- Diamond inheritance resolution
- Method overload handling
- Member deduplication
- EmitScope determination (ClassSurface vs ViewOnly)

### 14 Shape Passes (Exact Order)

Each pass executes sequentially and returns a new immutable SymbolGraph:

#### Pass 1: GlobalInterfaceIndex.Build()
- **Purpose**: Build global interface inheritance lookup
- **Input**: SymbolGraph (original hierarchy)
- **Output**: Side effect in BuildContext (populates GlobalInterfaceIndex)
- **Files**: `src/tsbindgen/SinglePhase/Shape/GlobalInterfaceIndex.cs`

#### Pass 2: InterfaceDeclIndex.Build()
- **Purpose**: Build interface member declaration lookup
- **Input**: SymbolGraph (original hierarchy)
- **Output**: Side effect in BuildContext (populates InterfaceDeclIndex)
- **Files**: `src/tsbindgen/SinglePhase/Shape/InterfaceDeclIndex.cs`

#### Pass 3: StructuralConformance.Analyze()
- **Purpose**: Synthesize ViewOnly members for structural interface conformance
- **Input**: SymbolGraph (original hierarchy)
- **Output**: SymbolGraph (with synthesized ViewOnly members)
- **Files**: `src/tsbindgen/SinglePhase/Shape/StructuralConformance.cs`
- **Key**: Must run BEFORE interface flattening so `FindDeclaringInterface` can walk hierarchy

#### Pass 4: InterfaceInliner.Inline()
- **Purpose**: Flatten interface hierarchies (copy inherited members into each interface)
- **Input**: SymbolGraph (original hierarchy)
- **Output**: SymbolGraph (flattened interfaces)
- **Files**: `src/tsbindgen/SinglePhase/Shape/InterfaceInliner.cs`
- **Key**: Must run AFTER indices and conformance

#### Pass 5: ExplicitImplSynthesizer.Synthesize()
- **Purpose**: Synthesize ViewOnly members for explicit interface implementations
- **Input**: SymbolGraph (flattened interfaces)
- **Output**: SymbolGraph (with explicit impl ViewOnly members)
- **Files**: `src/tsbindgen/SinglePhase/Shape/ExplicitImplSynthesizer.cs`

#### Pass 6: DiamondResolver.Resolve()
- **Purpose**: Resolve diamond inheritance (same member from multiple interfaces)
- **Input**: SymbolGraph
- **Output**: SymbolGraph (diamond conflicts resolved)
- **Files**: `src/tsbindgen/SinglePhase/Shape/DiamondResolver.cs`

#### Pass 7: BaseOverloadAdder.AddOverloads()
- **Purpose**: Add base class method overloads for interface compatibility
- **Input**: SymbolGraph
- **Output**: SymbolGraph (with base overloads)
- **Files**: `src/tsbindgen/SinglePhase/Shape/BaseOverloadAdder.cs`

#### Pass 8: StaticSideAnalyzer.Analyze()
- **Purpose**: Analyze static members and constructors
- **Input**: SymbolGraph
- **Output**: Side effect only (updates BuildContext)
- **Files**: `src/tsbindgen/SinglePhase/Shape/StaticSideAnalyzer.cs`

#### Pass 9: IndexerPlanner.Plan()
- **Purpose**: Mark indexers for omission (TypeScript limitation)
- **Input**: SymbolGraph
- **Output**: SymbolGraph (indexers marked for omission)
- **Files**: `src/tsbindgen/SinglePhase/Shape/IndexerPlanner.cs`

#### Pass 10: HiddenMemberPlanner.Plan()
- **Purpose**: Handle C# 'new' keyword hiding (rename hidden members)
- **Input**: SymbolGraph
- **Output**: Side effect only (creates rename decisions in Renamer)
- **Files**: `src/tsbindgen/SinglePhase/Shape/HiddenMemberPlanner.cs`

#### Pass 11: FinalIndexersPass.Run()
- **Purpose**: Remove any indexer properties that leaked through
- **Input**: SymbolGraph
- **Output**: SymbolGraph (indexer properties removed)
- **Files**: `src/tsbindgen/SinglePhase/Shape/FinalIndexersPass.cs`

#### Pass 12: ClassSurfaceDeduplicator.Deduplicate()
- **Purpose**: Resolve name collisions on class surface (pick winner, demote rest to ViewOnly)
- **Input**: SymbolGraph
- **Output**: SymbolGraph (duplicates demoted)
- **Files**: `src/tsbindgen/SinglePhase/Shape/ClassSurfaceDeduplicator.cs`

#### Pass 13: ConstraintCloser.Close()
- **Purpose**: Complete generic constraint closures
- **Input**: SymbolGraph
- **Output**: SymbolGraph (constraints closed)
- **Files**: `src/tsbindgen/SinglePhase/Shape/ConstraintCloser.cs`

#### Pass 14: OverloadReturnConflictResolver.Resolve()
- **Purpose**: Resolve method overloads with conflicting return types
- **Input**: SymbolGraph
- **Output**: SymbolGraph (return conflicts resolved)
- **Files**: `src/tsbindgen/SinglePhase/Shape/OverloadReturnConflictResolver.cs`

#### Pass 15: ViewPlanner.Plan()
- **Purpose**: Plan explicit interface views (one interface per view)
- **Input**: SymbolGraph
- **Output**: SymbolGraph (views planned)
- **Files**: `src/tsbindgen/SinglePhase/Shape/ViewPlanner.cs`

#### Pass 16: MemberDeduplicator.Deduplicate()
- **Purpose**: Remove any duplicate members introduced by Shape passes
- **Input**: SymbolGraph
- **Output**: SymbolGraph (final deduplication)
- **Files**: `src/tsbindgen/SinglePhase/Shape/MemberDeduplicator.cs`

**Output State After Shape**:
- All members have `EmitScope` determined (ClassSurface or ViewOnly)
- All transformations complete
- `TsEmitName` still null (assigned in Phase 3.5)

---

## Phase 3.5: NAME RESERVATION

**Purpose**: Assign all TypeScript names via central Renamer

**Input**:
- `SymbolGraph` (from Phase 3, TypeScript-ready but unnamed)

**Output**:
- `SymbolGraph` (with `TsEmitName` assigned to all symbols)

**Mutability**:
- Side effect: Populates Renamer decision tables
- Pure: Returns new SymbolGraph with `TsEmitName` set

**Key Operations**:
1. For each type: Apply syntax transforms and reserve name via `Renamer.ReserveTypeName()`
2. For each member:
   - Skip members already renamed by earlier passes (HiddenMemberPlanner, IndexerPlanner)
   - Apply syntax transforms (`` ` `` → `_`, `+` → `_`, etc.)
   - Apply reserved word sanitization (add `_` suffix if needed)
   - Reserve name via `Renamer.ReserveMemberName()` with correct scope
3. Audit completeness (fail fast if any emitted member lacks rename decision)
4. Apply names to SymbolGraph (`Application.ApplyNamesToGraph()`)

**Files Involved**:
- `src/tsbindgen/SinglePhase/Normalize/NameReservation.cs`
- `src/tsbindgen/SinglePhase/Normalize/Naming/Reservation.cs`
- `src/tsbindgen/SinglePhase/Normalize/Naming/Application.cs`
- `src/tsbindgen/SinglePhase/Normalize/Naming/Shared.cs`

**Scopes Used**:
- Type names: `ScopeFactory.Namespace(namespaceName, NamespaceArea.Internal)`
- Class surface members: `ScopeFactory.ClassSurface(type, isStatic)`
- View members: `ScopeFactory.View(type, interfaceStableId)`

**Data Transformations**:
- Input: All symbols have `TsEmitName = null`
- Output: All emitted symbols have `TsEmitName` assigned
- Renamer: Decision tables fully populated

---

## Phase 4: PLAN

**Purpose**: Build import graph, plan emission order, prepare for validation

**Input**:
- `SymbolGraph` (from Phase 3.5, fully named)

**Output**:
- `EmissionPlan` containing:
  - `SymbolGraph` (unchanged)
  - `ImportPlan` (imports, exports, aliases)
  - `EmitOrder` (deterministic emission order)

**Mutability**: Pure function (returns new immutable EmissionPlan)

**Key Operations**:
1. Build `ImportGraph` (cross-namespace dependencies)
2. Plan imports and exports via `ImportPlanner.PlanImports()`
3. Determine stable emission order via `EmitOrderPlanner.PlanOrder()`

**Files Involved**:
- `src/tsbindgen/SinglePhase/Plan/ImportGraph.cs`
- `src/tsbindgen/SinglePhase/Plan/ImportPlanner.cs`
- `src/tsbindgen/SinglePhase/Plan/EmitOrderPlanner.cs`

**Data Transformations**:
- Input: SymbolGraph (fully named)
- Output: EmissionPlan (SymbolGraph + ImportPlan + EmitOrder)

---

## Phase 4.5: OVERLOAD UNIFICATION

**Purpose**: Unify method overloads (merge signatures into single overloaded declaration)

**Input**:
- `SymbolGraph` (from Phase 4)

**Output**:
- `SymbolGraph` (overloads unified)

**Mutability**: Pure function (returns new immutable SymbolGraph)

**Key Operations**:
- Group methods by name and emit scope
- Merge overloads with compatible return types
- Preserve distinct overloads for different return types

**Files Involved**:
- `src/tsbindgen/SinglePhase/Plan/OverloadUnifier.cs`

---

## Phase 4.6: INTERFACE CONSTRAINT AUDIT

**Purpose**: Audit constructor constraints per (Type, Interface) pair

**Input**:
- `SymbolGraph` (from Phase 4.5)

**Output**:
- `ConstraintFindings` (audit results)

**Mutability**: Pure function (returns new immutable ConstraintFindings)

**Key Operations**:
- Check each (Type, Interface) pair for constructor constraints
- Record findings for PhaseGate validation

**Files Involved**:
- `src/tsbindgen/SinglePhase/Plan/InterfaceConstraintAuditor.cs`

---

## Phase 4.7: PHASEGATE VALIDATION

**Purpose**: Validate entire pipeline output before emission

**Input**:
- `SymbolGraph` (from Phase 4.5)
- `ImportPlan` (from Phase 4)
- `ConstraintFindings` (from Phase 4.6)

**Output**:
- Side effect: Records diagnostics in `BuildContext.Diagnostics`

**Mutability**: Side effect only (no data returned)

**Key Operations**:
- Run 26 validation checks (see PhaseGate spec)
- Record ERROR, WARNING, INFO diagnostics
- Fail fast if any ERROR-level diagnostics found

**Files Involved**:
- `src/tsbindgen/SinglePhase/Plan/PhaseGate.cs`
- `src/tsbindgen/SinglePhase/Plan/Validation/*.cs` (26 validators)

**Critical Rule**:
- Any ERROR-level diagnostic blocks Phase 5 (Emit)
- Build returns `Success = false`

---

## Phase 5: EMIT

**Purpose**: Generate all output files

**Input**:
- `EmissionPlan` (from Phase 4, validated)

**Output**:
- Side effects: File I/O (*.d.ts, *.json, *.js files)

**Mutability**: Side effects only (writes to file system)

**Key Operations**:
1. Emit `_support/types.d.ts` (centralized marker types)
2. For each namespace (in emission order):
   - Emit `<namespace>/internal/index.d.ts` (internal declarations)
   - Emit `<namespace>/index.d.ts` (public facade)
   - Emit `<namespace>/metadata.json` (CLR-specific info)
   - Emit `<namespace>/bindings.json` (CLR → TS name mappings)
   - Emit `<namespace>/index.js` (ES module stub)

**Files Involved**:
- `src/tsbindgen/SinglePhase/Emit/SupportTypesEmit.cs`
- `src/tsbindgen/SinglePhase/Emit/InternalIndexEmitter.cs`
- `src/tsbindgen/SinglePhase/Emit/FacadeEmitter.cs`
- `src/tsbindgen/SinglePhase/Emit/MetadataEmitter.cs`
- `src/tsbindgen/SinglePhase/Emit/BindingEmitter.cs`
- `src/tsbindgen/SinglePhase/Emit/ModuleStubEmitter.cs`

**Critical Rule**:
- Only executes if `ctx.Diagnostics.HasErrors() == false`
- If errors present, Build returns immediately with `Success = false`

---

## Data Transformations Table

| Phase | Input Type | Output Type | Mutability | Key Transformations |
|-------|-----------|-------------|------------|---------------------|
| **1. LOAD** | `string[]` | `SymbolGraph` | Immutable (pure) | Reflection → SymbolGraph |
| **2. NORMALIZE** | `SymbolGraph` | `SymbolGraph` | Immutable (pure) | Build indices (NamespaceIndex, TypeIndex, GlobalInterfaceIndex) |
| **3. SHAPE** | `SymbolGraph` | `SymbolGraph` | Immutable (pure) | 14 passes: flatten interfaces, synthesize members, determine EmitScope |
| **3.5. NAME RESERVATION** | `SymbolGraph` | `SymbolGraph` | Side effect + pure | Reserve names in Renamer, set TsEmitName on symbols |
| **4. PLAN** | `SymbolGraph` | `EmissionPlan` | Immutable (pure) | Build import graph, plan imports/exports, determine emission order |
| **4.5. OVERLOAD UNIFICATION** | `SymbolGraph` | `SymbolGraph` | Immutable (pure) | Merge method overloads |
| **4.6. CONSTRAINT AUDIT** | `SymbolGraph` | `ConstraintFindings` | Immutable (pure) | Audit constructor constraints |
| **4.7. PHASEGATE** | `SymbolGraph` + `ImportPlan` + `ConstraintFindings` | Side effect | Side effect only | 26 validation checks, record diagnostics |
| **5. EMIT** | `EmissionPlan` | File I/O | Side effects | Generate .d.ts, .json, .js files |

---

## Data Flow Diagram (ASCII)

```
Assembly Paths (string[])
    │
    ├─────────────────────────────────────────────┐
    │ PHASE 1: LOAD                               │
    │                                             │
    │ AssemblyLoader.LoadClosure()                │
    │   → MetadataLoadContext                     │
    │ ReflectionReader.ReadAssemblies()           │
    │   → Read all types/members                  │
    │ InterfaceMemberSubstitution.Substitute()    │
    │   → Substitute closed generics              │
    │                                             │
    │ Output: SymbolGraph (pure CLR)              │
    └─────────────────┬───────────────────────────┘
                      │
                      ↓ SymbolGraph (TsEmitName=null, EmitScope=undetermined)
                      │
    ┌─────────────────┴───────────────────────────┐
    │ PHASE 2: NORMALIZE                          │
    │                                             │
    │ SymbolGraph.WithIndices()                   │
    │   → Build NamespaceIndex, TypeIndex         │
    │ GlobalInterfaceIndex.Build()                │
    │   → Build interface inheritance lookup      │
    │ InterfaceDeclIndex.Build()                  │
    │   → Build interface member declaration map  │
    │                                             │
    │ Output: SymbolGraph (indexed)               │
    └─────────────────┬───────────────────────────┘
                      │
                      ↓ SymbolGraph (indexed, TsEmitName=null)
                      │
    ┌─────────────────┴───────────────────────────┐
    │ PHASE 3: SHAPE (14 passes)                  │
    │                                             │
    │ 1.  GlobalInterfaceIndex.Build()            │
    │ 2.  InterfaceDeclIndex.Build()              │
    │ 3.  StructuralConformance.Analyze()         │
    │       → Synthesize ViewOnly members         │
    │ 4.  InterfaceInliner.Inline()               │
    │       → Flatten interface hierarchies       │
    │ 5.  ExplicitImplSynthesizer.Synthesize()    │
    │       → Explicit impl ViewOnly members      │
    │ 6.  DiamondResolver.Resolve()               │
    │       → Resolve diamond inheritance         │
    │ 7.  BaseOverloadAdder.AddOverloads()        │
    │       → Add base overloads                  │
    │ 8.  StaticSideAnalyzer.Analyze()            │
    │       → Analyze static members              │
    │ 9.  IndexerPlanner.Plan()                   │
    │       → Mark indexers for omission          │
    │ 10. HiddenMemberPlanner.Plan()              │
    │       → Rename hidden members (new keyword) │
    │ 11. FinalIndexersPass.Run()                 │
    │       → Remove leaked indexer properties    │
    │ 12. ClassSurfaceDeduplicator.Deduplicate()  │
    │       → Demote duplicate members            │
    │ 13. ConstraintCloser.Close()                │
    │       → Complete constraint closures        │
    │ 14. OverloadReturnConflictResolver.Resolve()│
    │       → Resolve return conflicts            │
    │ 15. ViewPlanner.Plan()                      │
    │       → Plan explicit interface views       │
    │ 16. MemberDeduplicator.Deduplicate()        │
    │       → Final deduplication                 │
    │                                             │
    │ Output: SymbolGraph (TS-ready, unnamed)     │
    └─────────────────┬───────────────────────────┘
                      │
                      ↓ SymbolGraph (EmitScope determined, TsEmitName=null)
                      │
    ┌─────────────────┴───────────────────────────┐
    │ PHASE 3.5: NAME RESERVATION                 │
    │                                             │
    │ NameReservation.ReserveAllNames()           │
    │   → For each type:                          │
    │       Renamer.ReserveTypeName()             │
    │   → For each member:                        │
    │       Renamer.ReserveMemberName()           │
    │   → Application.ApplyNamesToGraph()         │
    │       Set TsEmitName on all symbols         │
    │                                             │
    │ Output: SymbolGraph (fully named)           │
    └─────────────────┬───────────────────────────┘
                      │
                      ↓ SymbolGraph (TsEmitName assigned)
                      │
    ┌─────────────────┴───────────────────────────┐
    │ PHASE 4: PLAN                               │
    │                                             │
    │ ImportGraph.Build()                         │
    │   → Build cross-namespace dependencies      │
    │ ImportPlanner.PlanImports()                 │
    │   → Plan imports, exports, aliases          │
    │ EmitOrderPlanner.PlanOrder()                │
    │   → Determine stable emission order         │
    │                                             │
    │ Output: EmissionPlan                        │
    │   (Graph + Imports + Order)                 │
    └─────────────────┬───────────────────────────┘
                      │
                      ↓ EmissionPlan
                      │
    ┌─────────────────┴───────────────────────────┐
    │ PHASE 4.5: OVERLOAD UNIFICATION             │
    │                                             │
    │ OverloadUnifier.UnifyOverloads()            │
    │   → Merge method overloads                  │
    │                                             │
    │ Output: SymbolGraph (overloads unified)     │
    └─────────────────┬───────────────────────────┘
                      │
                      ↓ SymbolGraph
                      │
    ┌─────────────────┴───────────────────────────┐
    │ PHASE 4.6: CONSTRAINT AUDIT                 │
    │                                             │
    │ InterfaceConstraintAuditor.Audit()          │
    │   → Audit constructor constraints           │
    │                                             │
    │ Output: ConstraintFindings                  │
    └─────────────────┬───────────────────────────┘
                      │
                      ↓ ConstraintFindings
                      │
    ┌─────────────────┴───────────────────────────┐
    │ PHASE 4.7: PHASEGATE VALIDATION             │
    │                                             │
    │ PhaseGate.Validate()                        │
    │   → 26 validation checks                    │
    │   → Record diagnostics                      │
    │                                             │
    │ Side effect: ctx.Diagnostics populated      │
    └─────────────────┬───────────────────────────┘
                      │
                      ↓ Check: ctx.Diagnostics.HasErrors()?
                      │
            ┌─────────┴─────────┐
            │ Has Errors?       │
            └───┬───────────┬───┘
                │ YES       │ NO
                ↓           ↓
    ┌───────────────┐   ┌─────────────────┴───────────────────────────┐
    │ Return        │   │ PHASE 5: EMIT                               │
    │ Success=false │   │                                             │
    └───────────────┘   │ SupportTypesEmit.Emit()                     │
                        │   → _support/types.d.ts                     │
                        │ InternalIndexEmitter.Emit()                 │
                        │   → <ns>/internal/index.d.ts                │
                        │ FacadeEmitter.Emit()                        │
                        │   → <ns>/index.d.ts                         │
                        │ MetadataEmitter.Emit()                      │
                        │   → <ns>/metadata.json                      │
                        │ BindingEmitter.Emit()                       │
                        │   → <ns>/bindings.json                      │
                        │ ModuleStubEmitter.Emit()                    │
                        │   → <ns>/index.js                           │
                        │                                             │
                        │ Side effects: File I/O                      │
                        └─────────────────┬───────────────────────────┘
                                          │
                                          ↓
                                ┌─────────────────────┐
                                │ Return              │
                                │ Success=true        │
                                │ BuildResult         │
                                └─────────────────────┘
```

---

## Critical Sequencing Rules

### 1. Shape Pass Dependencies

These passes MUST execute in order due to dependencies:

- **StructuralConformance BEFORE InterfaceInliner**: Conformance needs original hierarchy to walk up
- **InterfaceInliner BEFORE ExplicitImplSynthesizer**: Explicit impl synthesis needs flattened interfaces
- **IndexerPlanner BEFORE FinalIndexersPass**: Mark indexers before removing them
- **ClassSurfaceDeduplicator BEFORE ConstraintCloser**: Deduplication may affect constraints
- **OverloadReturnConflictResolver BEFORE ViewPlanner**: Return conflicts resolved before view planning
- **ViewPlanner BEFORE MemberDeduplicator**: Views planned before final deduplication

### 2. Name Reservation Timing

**Critical**: Name reservation (Phase 3.5) MUST occur:
- **AFTER** all Shape passes (EmitScope must be determined first)
- **BEFORE** Plan phase (PhaseGate validation needs TsEmitName)

### 3. PhaseGate Position

**Critical**: PhaseGate (Phase 4.7) MUST occur:
- **AFTER** all transformations complete
- **AFTER** names assigned
- **BEFORE** Emit phase

### 4. Emit Phase Gating

**Critical**: Emit phase (Phase 5) ONLY executes if:
- `ctx.Diagnostics.HasErrors() == false`
- If errors present, Build returns `Success = false` immediately

---

## Immutability Guarantees

Every phase (except Emit) follows this pattern:

```csharp
// Phase function signature
public static TOutput PhaseFunction(BuildContext ctx, TInput input)
{
    // input is immutable - read only

    // Apply transformations (create new objects)
    var transformed = ApplyTransformation(input);

    // Return new immutable output
    return transformed;
}
```

**Example: Shape pass**

```csharp
public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph)
{
    // graph is immutable - never modified

    // Build new namespaces with flattened interfaces
    var newNamespaces = graph.Namespaces
        .Select(ns => ns with { Types = FlattenTypes(ns.Types) })
        .ToImmutableArray();

    // Return new graph (original unchanged)
    return graph with { Namespaces = newNamespaces };
}
```

**Benefits**:
- No hidden state mutations
- Safe to parallelize (future)
- Easy to debug (snapshot at any phase)
- Clear data flow

---

## Related Documentation

- [pipeline.md](pipeline.md) - Comprehensive pipeline reference
- [overview.md](overview.md) - Architecture overview
- [scopes.md](../scopes.md) - Renamer scope system
