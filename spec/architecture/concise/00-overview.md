# SinglePhase Pipeline Architecture

## What is tsbindgen?

**Input**: .NET assembly DLLs
**Process**: Reflection → Analysis → TypeScript generation
**Output**: `.d.ts` files + JSON metadata sidecars

Enables Tsonic (TS→C# transpiler) to understand .NET BCL with full IDE support and type safety.

## Core Principles

### Single-Pass Processing
Six sequential phases, one pass through each. No iteration. Each phase transforms immutably.

### Immutable Data
- `SymbolGraph` → `NamespaceSymbol[]` → `TypeSymbol[]` → `MemberSymbol[]`
- Transformations return new graphs via `with` expressions
- No mutation

### Pure Functions
- Static classes only
- No instance state, no side effects (except I/O)
- Input → Process → Output

### Centralized State (BuildContext)
- **Policy**: Configuration (naming, filters, etc.)
- **SymbolRenamer**: Naming authority
- **DiagnosticBag**: Error collection
- **Interner**: String deduplication
- **Logger**: Progress reporting

### StableId-Based Identity
Assembly-qualified identifiers:
- **TypeStableId**: `"System.Private.CoreLib:System.Decimal"`
- **MemberStableId**: `"System.Private.CoreLib:System.Decimal::ToString(IFormatProvider):String"`

Properties: Immutable, unique, stable across runs, semantic (not token-based)

Used for: Rename keys, cross-assembly refs, bindings, duplicate detection

### Scope-Based Naming
TypeScript names reserved in scopes for uniqueness:
1. **Namespace**: `ns:System.Collections.Generic:internal`
2. **Class Surface**: `type:System.Decimal#instance`, `type:System.Decimal#static`
3. **View Surface**: `view:CoreLib:System.Decimal:CoreLib:IConvertible#instance`

Enables class/view members with same name to coexist.

## Pipeline Phases

### Phase 1: Load (Reflection)
- `AssemblyLoader`: Transitive closure loading
- `ReflectionReader`: System.Reflection → SymbolGraph (pure CLR, no TS concepts)
- `InterfaceMemberSubstitution`: Substitute closed generic interface members
- **Output**: SymbolGraph (CLR data only)

### Phase 2: Normalize (Indices)
- `SymbolGraph.WithIndices()`: Build NamespaceIndex, TypeIndex
- **Output**: SymbolGraph with fast lookups

### Phase 3: Shape (Transformations)
16 sequential passes:
1. `GlobalInterfaceIndex.Build`: Index interfaces
2. `InterfaceDeclIndex.Build`: Index interface members
3. `StructuralConformance.Analyze`: Mark ViewOnly members (can't conform structurally)
4. `InterfaceInliner.Inline`: Flatten interface hierarchy
5. `ExplicitImplSynthesizer`: Synthesize explicit implementations
6. `DiamondResolver.Resolve`: Resolve diamond inheritance
7. `BaseOverloadAdder`: Add base overloads
8. `StaticSideAnalyzer.Analyze`: Analyze static members
9. `IndexerPlanner.Plan`: Mark indexers as Omitted
10. `HiddenMemberPlanner.Plan`: Handle C# `new` keyword
11. `FinalIndexersPass.Run`: Remove leaked indexers
12. `ClassSurfaceDeduplicator`: Deduplicate class surface (losers → ViewOnly)
13. `ConstraintCloser.Close`: Resolve generic constraints
14. `OverloadReturnConflictResolver`: Resolve return type conflicts
15. `ViewPlanner.Plan`: Group ViewOnly members by source interface
16. `MemberDeduplicator`: Final deduplication

**Output**: SymbolGraph (shaped for TypeScript)

### Phase 3.5: Name Reservation
- `NameReservation.ReserveAllNames`: Reserve all type/member names via SymbolRenamer
- Apply naming policy, resolve conflicts, sanitize reserved words
- **Output**: SymbolGraph + RenameDecisions in Renamer

### Phase 4: Plan (Imports/Validation)
- `ImportGraph.Build`: Analyze type dependencies
- `ImportPlanner.PlanImports`: Determine imports/aliases
- `EmitOrderPlanner.PlanOrder`: Stable namespace order
- `OverloadUnifier.UnifyOverloads`: Merge overload variants
- `InterfaceConstraintAuditor`: Audit constructor constraints
- `PhaseGate.Validate`: **Pre-emission validation (50+ rules)**
- **Output**: EmissionPlan (graph + imports + order)

### Phase 5: Emit (File Generation)
- `SupportTypesEmit`: `_support/types.d.ts`
- `InternalIndexEmitter`: `internal/index.d.ts` per namespace
- `FacadeEmitter`: `facade/index.d.ts` per namespace
- `MetadataEmitter`: `metadata.json` per namespace
- `BindingEmitter`: `bindings.json` per namespace
- `ModuleStubEmitter`: `index.js` stubs per namespace
- **Output**: Files written to disk

## Key Concepts

### StableId
Immutable identity before name transformation. Key for rename decisions.

Format:
- Type: `{AssemblyName}:{ClrFullName}`
- Member: `{AssemblyName}:{DeclaringType}::{MemberName}{CanonicalSignature}`

Equality: Based on signature, **excludes metadata token** (semantic identity only).

### EmitScope
Where member is emitted:

```csharp
public enum EmitScope
{
    ClassSurface,  // On class/interface body
    StaticSurface, // In static section
    ViewOnly,      // In As_IInterface view property
    Omitted        // Not emitted (tracked in metadata)
}
```

Decision process:
1. `StructuralConformance`: ClassSurface → ViewOnly (structural failure)
2. `IndexerPlanner`: → Omitted (TS limitation)
3. `HiddenMemberPlanner`: Handle C# `new` keyword
4. `ClassSurfaceDeduplicator`: → ViewOnly (duplicate losers)
5. `PhaseGate`: Validate consistency

### ViewPlanner: Explicit Interface Implementation

C# has explicit interface implementations, TypeScript doesn't:

```csharp
// C# - explicit impl ONLY available via cast
class Decimal : IConvertible {
    bool IConvertible.ToBoolean(IFormatProvider? p) => ...;
}
```

Solution: View properties:

```typescript
// TypeScript
class Decimal {
    ToString(): string;  // ClassSurface
    As_IConvertible: {   // View property
        ToBoolean(provider: IFormatProvider | null): boolean;  // ViewOnly
    };
}
```

Flow:
1. `StructuralConformance` marks members ViewOnly
2. `ViewPlanner` groups ViewOnly by source interface
3. Creates `ExplicitView` objects
4. Attaches to `TypeSymbol.ExplicitViews`
5. `FacadeEmitter` emits view properties

### Scope-Based Naming Rationale

C# allows same name in different contexts:

```csharp
class Decimal : IConvertible, IFormattable {
    string ToString() => "1.0";                               // On class
    string IConvertible.ToString(IFormatProvider p) => "1.0"; // IConvertible only
    string IFormattable.ToString(string f, IFormatProvider p) => "1.0"; // IFormattable only
}
```

Without scopes: ToString, ToString2, ToString3 (bad)
With scopes: Each context has independent `ToString` (good)

Scope format:
- Namespace: `ns:System.Collections.Generic:internal`
- Class: `type:System.Decimal#instance` / `type:System.Decimal#static`
- View: `view:CoreLib:System.Decimal:CoreLib:IConvertible#instance`

### PhaseGate: Pre-Emission Validation

Runs after transformations, before emission. Enforces 50+ invariants.

Categories:
1. **Finalization** (PG_FIN_001-009): Every symbol has final TS name
2. **Scope Integrity** (PG_SCOPE_001-004): Well-formed scopes
3. **Name Uniqueness** (PG_NAME_001-005): No duplicates in scope
4. **View Integrity** (PG_INT_001-003): Every ViewOnly member in a view
5. **Import/Export** (PG_IMPORT_001, PG_EXPORT_001, PG_API_001-002): Valid imports
6. **Type Resolution** (PG_LOAD_001, PG_TYPEMAP_001): All types resolvable
7. **Overload Collision** (PG_OL_001-002): No overload collisions
8. **Constraint Integrity** (PG_CNSTR_001-004): Constraints satisfied

Severity: ERROR (blocks emission), WARNING (logged), INFO (diagnostic)

Output: Console log, `.diagnostics.txt`, `validation-summary.json`

## Directory Structure

```
SinglePhase/
├── SinglePhaseBuilder.cs        # Main orchestrator
├── BuildContext.cs              # Shared services

├── Load/                        # Phase 1
│   ├── AssemblyLoader.cs
│   ├── ReflectionReader.cs
│   ├── TypeReferenceFactory.cs
│   └── InterfaceMemberSubstitution.cs

├── Model/                       # Immutable data
│   ├── SymbolGraph.cs
│   ├── Symbols/
│   │   ├── NamespaceSymbol.cs
│   │   ├── TypeSymbol.cs
│   │   └── MemberSymbols/
│   ├── Types/
│   │   ├── TypeReference.cs
│   │   └── NamedTypeReference.cs, GenericTypeReference.cs, etc.
│   └── AssemblyKey.cs

├── Normalize/                   # Phase 2
│   ├── SignatureNormalization.cs
│   ├── OverloadUnifier.cs
│   └── NameReservation.cs

├── Shape/                       # Phase 3 (16 passes)
│   ├── GlobalInterfaceIndex.cs
│   ├── InterfaceDeclIndex.cs
│   ├── StructuralConformance.cs
│   ├── InterfaceInliner.cs
│   ├── ExplicitImplSynthesizer.cs
│   ├── DiamondResolver.cs
│   ├── BaseOverloadAdder.cs
│   ├── StaticSideAnalyzer.cs
│   ├── IndexerPlanner.cs
│   ├── HiddenMemberPlanner.cs
│   ├── FinalIndexersPass.cs
│   ├── ClassSurfaceDeduplicator.cs
│   ├── ConstraintCloser.cs
│   ├── OverloadReturnConflictResolver.cs
│   ├── ViewPlanner.cs
│   └── MemberDeduplicator.cs

├── Renaming/                    # Phase 3.5
│   ├── SymbolRenamer.cs         # Central naming authority
│   ├── StableId.cs
│   ├── RenameScope.cs
│   ├── ScopeFactory.cs
│   ├── RenameDecision.cs
│   ├── NameReservationTable.cs
│   └── TypeScriptReservedWords.cs

├── Plan/                        # Phase 4
│   ├── ImportGraph.cs
│   ├── ImportPlanner.cs
│   ├── EmitOrderPlanner.cs
│   ├── InterfaceConstraintAuditor.cs
│   ├── PhaseGate.cs             # Pre-emission validation
│   ├── TsAssignability.cs
│   ├── TsErase.cs
│   └── Validation/              # PhaseGate modules
│       ├── Core.cs
│       ├── Names.cs, Views.cs, Scopes.cs
│       ├── Types.cs, ImportExport.cs
│       ├── Constraints.cs, Finalization.cs
│       └── Context.cs

└── Emit/                        # Phase 5
    ├── SupportTypesEmitter.cs
    ├── InternalIndexEmitter.cs
    ├── FacadeEmitter.cs
    ├── MetadataEmitter.cs
    ├── BindingEmitter.cs
    ├── ModuleStubEmitter.cs
    ├── TypeMap.cs
    └── TypeNameResolver.cs
```

## BuildContext Services

### Policy (Configuration)
- Naming transforms (PascalCase, camelCase, ExplicitMap overrides)
- Emission filters (internal types, docs, debug info)
- Branded primitives (int vs number)
- Import style (ES6 vs namespace)

### SymbolRenamer (Naming Service)
Central naming authority for all TS identifiers.

Key methods:
- `ReserveTypeName(id, requested, scope, reason)`
- `ReserveMemberName(id, requested, scope, reason, isStatic)`
- `GetFinalTypeName(type, area)`
- `GetFinalMemberName(id, scope)`
- `HasFinalTypeName/MemberName(id, scope)`

Responsibilities:
1. Reserve names in scopes
2. Apply style transforms
3. Resolve conflicts (numeric suffixes)
4. Sanitize reserved words (class → class_)
5. Track rename decisions with provenance

Decision record:
```csharp
public sealed record RenameDecision {
    StableId Id, string Requested, string Final, string From,
    string Reason, string DecisionSource, string Strategy,
    string ScopeKey, bool? IsStatic
}
```

### DiagnosticBag (Error Collection)
- `Error(code, message)`, `Warning(code, message)`, `Info(code, message)`
- `HasErrors()`, `GetAll()`

Diagnostic format: Severity, Code, Message, Location

### Interner (String Deduplication)
- `Intern(value)` → deduplicated string
- Typical savings: 30-40% memory for BCL

### Logger (Progress Reporting)
- `Log(category, message)`
- Categories: Build, Load, Shape, ViewPlanner, PhaseGate, Emit
- Filter by verbosity or categories

## Type Mapping (Tsonic Conventions)

### Primitives → Branded Types
```typescript
type int = number & { __brand: "int" };
type uint = number & { __brand: "uint" };
type decimal = number & { __brand: "decimal" };
```

### Collections → ReadonlyArray
`IEnumerable<T>`, `ICollection<T>`, `IList<T>` → `ReadonlyArray<T>`

### Tasks → Promises
`Task<T>` → `Promise<T>`

### Nullable → Union
`int?` → `int | null`

### Generic Arity
C# `List\`1` → TS `List_1`

## Completeness Verification

Pipeline ensures **100% data integrity**:
1. **snapshot.json**: What was reflected/transformed (Phase 2/3 output)
2. **typelist.json**: What was emitted to .d.ts (Phase 4 output)
3. **verify-completeness.js**: Compares to ensure zero data loss

Both use flat structure with `tsEmitName` as key.

## Current Status (2025-11-08)

- **130 BCL namespaces** generated
- **4,047 types** emitted
- **Zero syntax errors** (TS1xxx)
- **12 semantic errors** (TS2417 - property covariance, expected)
- **100% type coverage** - All reflected types accounted for
- **241 indexers** intentionally omitted (tracked in metadata)

## Known .NET/TypeScript Impedance Mismatches

1. **Property Covariance** (~12 TS2417 errors)
   - C# allows covariant property returns
   - TS doesn't support property overloads
   - Status: Safe to ignore

2. **Generic Static Members**
   - C# allows `static T DefaultValue` in `class List<T>`
   - TS doesn't support
   - Status: Skipped, tracked in metadata

3. **Indexers** (~241 instances)
   - Different parameter types cause duplicate identifiers
   - Status: Omitted, tracked in metadata

## Summary

**Deterministic, pure functional transformation**: .NET assemblies → TypeScript declarations

**Flow**: Load → Normalize → Shape → Name Reservation → Plan → Emit

**Core Guarantees**: Immutability, purity, centralized state, stable identity, scope-based naming, validation

**Result**: 100% data integrity, zero data loss, type-safe TypeScript for entire .NET BCL
