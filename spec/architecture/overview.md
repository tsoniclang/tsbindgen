# System Overview

## Purpose and Objectives

### Primary Objective

tsbindgen generates TypeScript declaration files (.d.ts) and metadata sidecars (.metadata.json) from .NET assemblies to enable seamless interoperation between TypeScript and .NET in the Tsonic compiler.

### Key Goals

1. **Type Safety**: Generate TypeScript declarations that preserve .NET type semantics
2. **Completeness**: Emit all public APIs with zero data loss
3. **Correctness**: Ensure generated TypeScript is syntactically and semantically valid
4. **Metadata Preservation**: Track CLR-specific information not expressible in TypeScript
5. **Cross-Assembly References**: Handle external type references and dependencies
6. **Naming Flexibility**: Support camelCase transforms while maintaining CLR name mappings

### Target Consumers

1. **Tsonic Compiler**: Consumes .d.ts for TypeScript type checking and .metadata.json for C# code generation
2. **TypeScript Compiler**: Validates generated .d.ts files
3. **IDE Tooling**: Provides IntelliSense and type information
4. **Package Consumers**: Reference types from published tsbindgen packages

## Architectural Principles

### 1. Pure Functional Architecture

**Principle**: All transformations are pure functions with immutable data.

**Implementation**:
- All data structures are immutable records
- Static classes with pure static methods
- No mutable state or side effects (except I/O in Emit phase)
- Transformations return new copies via wither methods

**Benefits**:
- Predictable behavior
- Easy to reason about and test
- No hidden state mutations
- Parallelizable (future optimization)

**Example**:
```csharp
// Pure function - input → output, no side effects
public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph)
{
    // Read graph, return transformed copy
    var updatedNamespaces = graph.Namespaces
        .Select(ns => TransformNamespace(ctx, ns))
        .ToImmutableArray();

    return graph with { Namespaces = updatedNamespaces };
}
```

### 2. Single-Pass Processing

**Principle**: One continuous pipeline from reflection to emission.

**Implementation**:
- All context maintained in-memory throughout build
- No intermediate serialization between phases
- Strict sequential phase execution
- Each phase produces immutable output for next phase

**Benefits**:
- Faster execution (no I/O between phases)
- Complete context available to all phases
- Easier to add cross-phase validations
- Simpler error handling and recovery

**Contrast with Old Pipeline**:
- Old: Reflect → Serialize snapshot → Load snapshot → Aggregate → Serialize → Load → Render
- New: Reflect → Normalize → Shape → Plan → Emit (all in-memory)

### 3. StableId-Based Identity

**Principle**: Use assembly-qualified identifiers for all symbols.

**Implementation**:
- `TypeStableId`: `AssemblyName:ClrFullName`
- `MemberStableId`: `AssemblyName:DeclaringType::MemberName{CanonicalSignature}`
- Used as keys in rename decisions, bindings, and cross-assembly references

**Benefits**:
- Disambiguates types from different assemblies
- Enables cross-assembly type resolution
- Supports external package resolution (--ref-path)
- Prevents name collisions in multi-assembly builds

**Example**:
```csharp
// Type identity
TypeStableId = "System.Private.CoreLib:System.String"

// Member identity
MemberStableId = "System.Private.CoreLib:System.String::Substring|arity=0|(int:in,int:in)|->String|static=false"
```

### 4. Centralized Naming Authority

**Principle**: All TypeScript identifiers flow through SymbolRenamer.

**Implementation**:
- Single `SymbolRenamer` instance in BuildContext
- All name reservation calls go through Renamer
- All name lookups come from Renamer
- Renamer records every decision with full provenance

**Benefits**:
- Single source of truth for all names
- Complete rename audit trail
- Consistent suffix allocation
- Prevents name collision bugs

**Responsibilities**:
- Materialize final TS identifiers
- Allocate numeric suffixes for collisions
- Enforce TypeScript reserved word rules
- Separate static and instance member scopes
- Support dual-scope reservations (class + view)

### 5. Scope-Based Naming

**Principle**: Separate naming scopes for different contexts.

**Implementation**:
- **Namespace scopes**: `ns:System.Collections.Generic:public` / `ns:System.Collections.Generic:internal`
- **Class scopes**: `type:System.Decimal#instance` / `type:System.Decimal#static`
- **View scopes**: `view:System.Private.CoreLib:System.Decimal:System.Private.CoreLib:System.IConvertible#instance`

**Benefits**:
- Static and instance members can have same name
- Class surface and views can have same member names
- Public and internal types can have same name
- Prevents scope-crossing collisions

**Scope Rules**:
- Reservation uses base scopes (no suffix)
- Lookup uses surface scopes (with #instance/#static suffix)
- ViewOnly members reserved in view scope
- ClassSurface members reserved in class scope

### 6. Comprehensive Validation

**Principle**: Validate before emission, not during.

**Implementation**:
- PhaseGate runs between Plan and Emit phases
- 26 validation methods execute in strict order
- All diagnostics recorded with error codes
- Detailed reports generated for debugging

**Benefits**:
- Early error detection
- Complete validation coverage
- Actionable error messages
- Prevents emission of invalid TypeScript

**Validation Categories**:
- Type names, member names, generic parameters
- Interface conformance, inheritance
- Emit scopes, imports, exports
- Policy compliance
- Views, final names, aliases
- Identifier sanitization, overload collisions
- Printer consistency, type map compliance
- External type resolution, public API surface

## Key Design Decisions

### Decision 1: MetadataLoadContext for Reflection

**Rationale**:
- Assemblies may target different .NET versions
- Assemblies may have different dependencies
- Need to load assemblies in isolation without runtime conflicts

**Implementation**:
- Use MetadataLoadContext with custom resolver
- Load assemblies in separate context from host process
- All type comparisons use name-based equality (not typeof())

**Impact**:
- Can load BCL from .NET 10 while running on .NET 8
- No assembly version conflicts
- Requires name-based type mapping (can't use typeof())

### Decision 2: Separate Class Surface and Views

**Rationale**:
- TypeScript doesn't support explicit interface implementations
- C# allows same member name on class and in explicit interface implementation
- Need to preserve explicit interface implementations while avoiding name collisions

**Implementation**:
- **ClassSurface**: Members on main public API
- **ViewOnly**: Members only in explicit interface views
- Views are accessed via `As_IInterface` properties

**Impact**:
- Prevents name collisions from explicit implementations
- Allows faithful representation of C# explicit interface patterns
- Requires dual-scope naming support in SymbolRenamer

**Example**:
```typescript
// ClassSurface member
class MyClass {
    ToString(): string;  // ClassSurface

    // ViewOnly members accessed via view
    readonly As_IConvertible: {
        ToString(provider: IFormatProvider): string;  // ViewOnly
    };
}
```

### Decision 3: Branded Marker Types for Unsafe Constructs

**Rationale**:
- TypeScript has no native pointer or ref/out types
- Need to represent unsafe CLR constructs without losing type safety
- Need to prevent accidental usage while preserving API completeness

**Implementation**:
- `TSUnsafePointer<T>`: Opaque marker for C# pointer types (`void*`, `int*`, `T*`)
- `TSByRef<T>`: Structural wrapper for C# ref/out/in parameters

**Impact**:
- Unsafe APIs represented in TypeScript
- Type safety maintained (can't accidentally use as regular types)
- Zero data loss (all public APIs emitted)
- Clear marker for Tsonic compiler to handle specially

**Example**:
```typescript
// Centralized in _support/types.d.ts
export type TSUnsafePointer<T> = unknown & { readonly __tsbindgenPtr?: unique symbol };
export type TSByRef<T> = { value: T } & { readonly __tsbindgenByRef?: unique symbol };

// Usage in declarations
class UnsafeClass {
    static AllocHGlobal(cb: int): TSUnsafePointer<void>;
    static Method(ptr: TSUnsafePointer<byte>): void;
}
```

### Decision 4: Flatten Interface Hierarchies

**Rationale**:
- TypeScript structural typing makes extends clauses unnecessary
- C# interface hierarchies can be very deep (IEnumerable → IEnumerable<T> → ICollection<T> → IList<T>)
- Deep extends chains complicate type checking and emit

**Implementation**:
- InterfaceInliner pass flattens all interface members
- Each interface gets all inherited members directly
- No extends clauses in generated TypeScript

**Impact**:
- Simpler generated TypeScript
- Faster TypeScript compilation
- No extends chain complexity
- Full API preserved on each interface

### Decision 5: Intentional Omissions with Metadata Tracking

**Rationale**:
- Some CLR features can't be represented in TypeScript (generic static members, overloaded indexers)
- Complete omission would cause data loss
- Tsonic compiler needs to know about these members

**Implementation**:
- Skip emission to .d.ts
- Track in metadata.json `intentionalOmissions` field
- Verification script filters these out when checking completeness

**Impact**:
- TypeScript declarations remain valid
- Tsonic compiler has complete CLR information
- 100% data integrity through pipeline
- Clear documentation of limitations

**Examples**:
- Generic static members: `static T DefaultValue<T>()` in `class List<T>`
- Overloaded indexers: `T this[int index]` and `T this[string key]` causing duplicate name
- Result: 241 indexers intentionally omitted in BCL, tracked in metadata

## Architecture Diagrams

### Component Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                       CLI Entry Point                            │
│                   Program.cs → GenerateCommand                   │
└───────────────────────────────┬─────────────────────────────────┘
                                │
                                ↓
┌─────────────────────────────────────────────────────────────────┐
│                    SinglePhaseBuilder.cs                         │
│                      (Main Orchestrator)                         │
│                                                                   │
│  Build() → LoadPhase() → ShapePhase() → PlanPhase() → EmitPhase()│
└───────────────────────────────┬─────────────────────────────────┘
                                │
                                ↓
┌─────────────────────────────────────────────────────────────────┐
│                        BuildContext                              │
│                    (Shared Services Layer)                       │
│                                                                   │
│  ┌──────────────┐  ┌───────────────┐  ┌──────────────┐         │
│  │   Policy     │  │ SymbolRenamer │  │ DiagnosticBag│         │
│  │  (Config)    │  │  (Naming)     │  │   (Errors)   │         │
│  └──────────────┘  └───────────────┘  └──────────────┘         │
│  ┌──────────────┐  ┌───────────────┐                            │
│  │   Interner   │  │    Logger     │                            │
│  │  (Strings)   │  │  (Optional)   │                            │
│  └──────────────┘  └───────────────┘                            │
└───────────────────────────────┬─────────────────────────────────┘
                                │
        ┌───────────────────────┼───────────────────────┐
        │                       │                       │
        ↓                       ↓                       ↓
┌───────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Load/       │    │    Normalize/    │    │    Shape/       │
│   4 files     │    │    3 files       │    │   14 files      │
└───────────────┘    └──────────────────┘    └─────────────────┘
        │                       │                       │
        └───────────────────────┼───────────────────────┘
                                │
                                ↓
                    ┌──────────────────────┐
                    │  SymbolGraph         │
                    │  (Core Data Model)   │
                    │                      │
                    │  - Namespaces        │
                    │  - Types             │
                    │  - Members           │
                    │  - Indices           │
                    └──────────┬───────────┘
                               │
        ┌──────────────────────┼──────────────────────┐
        │                      │                      │
        ↓                      ↓                      ↓
┌───────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Plan/       │    │   PhaseGate      │    │    Emit/        │
│   8 files     │    │   (Validation)   │    │   11 files      │
└───────────────┘    └──────────────────┘    └─────────────────┘
        │                      │                      │
        └──────────────────────┴──────────────────────┘
                               │
                               ↓
                    ┌──────────────────────┐
                    │  File System Output  │
                    │                      │
                    │  - .d.ts             │
                    │  - .metadata.json    │
                    │  - .bindings.json    │
                    │  - .js stubs         │
                    └──────────────────────┘
```

### Data Flow Diagram

```
Assembly DLL Files
        │
        ↓
┌──────────────────────────────────────────┐
│  Phase 1: LOAD                            │
│                                           │
│  AssemblyLoader                           │
│    → MetadataLoadContext                  │
│    → Load transitive closure (BFS)       │
│                                           │
│  ReflectionReader                         │
│    → Read types & members                │
│    → Filter compiler-generated           │
│                                           │
│  TypeReferenceFactory                     │
│    → Convert Type → TypeReference        │
│    → Memoization + cycle detection       │
│                                           │
│  InterfaceMemberSubstitutor               │
│    → Substitute closed generic params     │
└──────────────┬───────────────────────────┘
               │
               ↓ SymbolGraph (CLR facts)
               │
┌──────────────┴───────────────────────────┐
│  Phase 2: NORMALIZE                       │
│                                           │
│  Build Indices                            │
│    → NamespaceIndex (name → namespace)   │
│    → TypeIndex (stableId → type)         │
│                                           │
│  SignatureNormalization                   │
│    → Canonical method signatures         │
│    → Canonical property signatures       │
│                                           │
│  NameReservation                          │
│    → Reserve all TS names via Renamer    │
│                                           │
│  OverloadUnifier                          │
│    → Unify method overloads              │
└──────────────┬───────────────────────────┘
               │
               ↓ SymbolGraph (indexed, normalized)
               │
┌──────────────┴───────────────────────────┐
│  Phase 3: SHAPE (14 passes)               │
│                                           │
│  1. GlobalInterfaceIndex                  │
│  2. InterfaceInliner                      │
│  3. StructuralConformance                 │
│  4. ExplicitImplSynthesizer               │
│  5. DiamondResolver                       │
│  6. BaseOverloadAdder                     │
│  7. StaticSideAnalyzer                    │
│  8. IndexerPlanner                        │
│  9. HiddenMemberPlanner                   │
│  10. FinalIndexersPass                    │
│  11. ClassSurfaceDeduplicator             │
│  12. ConstraintCloser                     │
│  13. OverloadReturnConflictResolver       │
│  14. ViewPlanner                          │
└──────────────┬───────────────────────────┘
               │
               ↓ SymbolGraph (TypeScript-ready)
               │
┌──────────────┴───────────────────────────┐
│  Phase 4: PLAN                            │
│                                           │
│  ImportGraph                              │
│    → Build namespace dependencies        │
│                                           │
│  ImportPlanner                            │
│    → Plan imports and exports            │
│    → Generate import aliases             │
│                                           │
│  EmitOrderPlanner                         │
│    → Deterministic emission order        │
│                                           │
│  InterfaceConstraintAuditor               │
│    → Audit constraint losses             │
│                                           │
│  PhaseGate (26 validations)               │
│    → Comprehensive validation            │
│    → Record all diagnostics              │
└──────────────┬───────────────────────────┘
               │
               ↓ EmissionPlan (validated, ordered)
               │
┌──────────────┴───────────────────────────┐
│  Phase 5: EMIT                            │
│                                           │
│  SupportTypesEmitter                      │
│    → _support/types.d.ts                 │
│                                           │
│  InternalIndexEmitter                     │
│    → internal/index.d.ts                 │
│                                           │
│  FacadeEmitter                            │
│    → index.d.ts                          │
│                                           │
│  MetadataEmitter                          │
│    → metadata.json                       │
│                                           │
│  BindingEmitter                           │
│    → bindings.json                       │
│                                           │
│  ModuleStubEmitter                        │
│    → index.js stubs                      │
└──────────────┬───────────────────────────┘
               │
               ↓
        Output Files
```

## Success Criteria

### Zero Syntax Errors

All generated TypeScript must parse successfully:
- Command: `tsc --noEmit <output-dir>/**/*.d.ts`
- Requirement: Zero TS1xxx errors
- Current Status: ✅ 0 syntax errors on full BCL

### Documented Semantic Errors

Known .NET/TypeScript impedance mismatches:
- TS2417: Property covariance (~12 errors, documented limitation)
- Generic static members: Intentionally omitted (TypeScript limitation)
- Indexers: Intentionally omitted when overloaded (tracked in metadata)
- Requirement: All semantic errors must be documented
- Current Status: ✅ 12 TS2417 errors (expected)

### PhaseGate Validation

All PhaseGate checks must pass:
- 26 validation methods
- 24 error codes
- Requirement: Zero errors
- Current Status: ✅ 0 PhaseGate errors

### 100% Type Coverage

All reflected types must be accounted for:
- Verification: Compare snapshot.json (reflected) vs typelist.json (emitted)
- Requirement: Zero unintentional omissions
- Current Status: ✅ 4,047 types in, 4,047 types out

### Completeness Metrics

- **Namespaces**: 130 BCL namespaces
- **Types**: 4,047 types
- **Members**: 50,720 members
- **PhaseGate Errors**: 0
- **Syntax Errors**: 0
- **Semantic Errors**: 12 (TS2417, expected)
- **Intentional Omissions**: 241 indexers (tracked)

## Performance Characteristics

### Pipeline Execution

- BCL generation (130 namespaces, 4,047 types): ~30-60 seconds
- Load phase: ~10% of time (reflection + closure loading)
- Shape phase: ~40% of time (14 transformation passes)
- Plan phase: ~30% of time (validation + planning)
- Emit phase: ~20% of time (file I/O)

### Memory Usage

- SymbolGraph: Entire graph in memory (~50-100MB for BCL)
- String interning: Reduces memory footprint by ~30%
- Immutable collections: Higher memory usage but safe

### Scalability

- Single-pass design scales linearly with assembly size
- In-memory processing eliminates I/O overhead
- Future optimization: Parallelize independent transformations

## Extension Points

### Custom Name Transforms

Policy allows custom name transformation:
```csharp
var policy = new GenerationPolicy {
    NameTransforms = new NameTransforms {
        MethodStyle = NameStyle.CamelCase,
        PropertyStyle = NameStyle.CamelCase
    }
};
```

### Custom Diagnostics

DiagnosticBag extensible for custom validation:
```csharp
ctx.Diagnostics.Add(DiagnosticSeverity.Error, "CUSTOM_001", "Custom validation message");
```

### Custom Shape Passes

Shape phase extensible with new transformation passes:
```csharp
public static SymbolGraph CustomTransform(BuildContext ctx, SymbolGraph graph)
{
    // Custom transformation logic
    return transformedGraph;
}
```

## Related Documentation

- [pipeline.md](pipeline.md) - Detailed pipeline flow
- [phasegate.md](phasegate.md) - Validation error codes
- [renaming.md](renaming.md) - Naming authority design
- [model.md](model.md) - Core data structures
