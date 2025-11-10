# Pipeline Flow

## Main Orchestrator

### File: SinglePhaseBuilder.cs

**Location**: `src/tsbindgen/SinglePhase/SinglePhaseBuilder.cs`
**Lines**: 61
**Purpose**: Main entry point and orchestrator for the single-phase build pipeline

#### Build() Method

```csharp
public static BuildResult Build(
    IReadOnlyList<string> assemblyPaths,
    string outputDirectory,
    GenerationPolicy? policy = null,
    Action<string>? logger = null,
    bool verboseLogging = false,
    HashSet<string>? logCategories = null)
```

**Parameters**:
- `assemblyPaths`: List of assembly file paths to process
- `outputDirectory`: Output directory for generated files
- `policy`: Optional generation policy (naming transforms, constraints, etc.)
- `logger`: Optional progress logger callback
- `verboseLogging`: Enable verbose logging for all categories
- `logCategories`: Selective category-based logging

**Returns**: `BuildResult` with:
- `bool Success`: True if all phases completed without errors
- `SymbolGraphStatistics Statistics`: Type/member counts
- `IReadOnlyList<Diagnostic> Diagnostics`: All errors/warnings
- `IReadOnlyCollection<RenameDecision> RenameDecisions`: Complete rename audit trail

**Execution Flow**:

```
1. Create BuildContext
   ↓
2. LoadPhase(ctx, assemblyPaths)
   → Returns: SymbolGraph (CLR facts)
   ↓
3. ShapePhase(ctx, graph)
   → Returns: SymbolGraph (TypeScript-ready)
   ↓
4. PlanPhase(ctx, graph)
   → Returns: EmissionPlan (validated, ordered)
   ↓
5. EmitPhase(ctx, plan, outputDirectory)
   → Side effect: Writes files to disk
   ↓
6. Return BuildResult
```

#### LoadPhase() Method

```csharp
private static SymbolGraph LoadPhase(
    BuildContext ctx,
    IReadOnlyList<string> assemblyPaths)
```

**Purpose**: Load assemblies and build initial SymbolGraph via reflection

**Steps**:
1. Create MetadataLoadContext with transitive closure
2. Read all assemblies via ReflectionReader
3. Substitute closed generic interface members
4. Return SymbolGraph with pure CLR facts

**Output**: SymbolGraph containing:
- Namespaces with types
- Types with members
- Type references
- No TypeScript-specific transformations yet

**Details**: See [phase-1-load.md](phase-1-load.md)

#### ShapePhase() Method

```csharp
private static SymbolGraph ShapePhase(
    BuildContext ctx,
    SymbolGraph graph)
```

**Purpose**: Transform SymbolGraph for TypeScript emission

**Steps** (14 transformation passes):
1. Build GlobalInterfaceIndex
2. InterfaceInliner: Flatten interface hierarchies
3. StructuralConformance: Analyze structural conformance
4. ExplicitImplSynthesizer: Synthesize explicit interface implementations
5. DiamondResolver: Resolve diamond inheritance
6. BaseOverloadAdder: Add base class method overloads
7. StaticSideAnalyzer: Analyze static members
8. IndexerPlanner: Plan indexer handling
9. HiddenMemberPlanner: Handle 'new' keyword hiding
10. FinalIndexersPass: Remove indexer properties
11. ClassSurfaceDeduplicator: Resolve name collisions
12. ConstraintCloser: Complete generic constraints
13. OverloadReturnConflictResolver: Resolve return-type conflicts
14. ViewPlanner: Plan explicit interface views
15. NameReservation: Reserve all TS names via Renamer
16. OverloadUnifier: Unify method overloads
17. MemberDeduplicator: Remove duplicates

**Output**: SymbolGraph ready for TypeScript emission

**Details**: See [phase-3-shape.md](phase-3-shape.md)

#### PlanPhase() Method

```csharp
private static EmissionPlan PlanPhase(
    BuildContext ctx,
    SymbolGraph graph)
```

**Purpose**: Build import plan, determine emission order, validate

**Steps**:
1. Build ImportGraphData
2. Plan imports and exports (ImportPlanner)
3. Determine emission order (EmitOrderPlanner)
4. Audit interface constraints
5. Validate (PhaseGate.Validate)

**Output**: `EmissionPlan` containing:
- SymbolGraph (validated)
- ImportPlan (imports, exports, aliases)
- EmitOrder (deterministic emission order)

**Details**: See [phase-4-plan.md](phase-4-plan.md)

#### EmitPhase() Method

```csharp
private static void EmitPhase(
    BuildContext ctx,
    EmissionPlan plan,
    string outputDirectory)
```

**Purpose**: Generate all output files

**Steps**:
1. Emit _support/types.d.ts (if needed)
2. For each namespace:
   - Emit internal/index.d.ts
   - Emit index.d.ts facade
   - Emit metadata.json
   - Emit bindings.json (if name transforms active)
   - Emit index.js stub

**Side Effects**: Writes files to `outputDirectory`

**Details**: See [phase-5-emit.md](phase-5-emit.md)

## Records

### BuildResult

```csharp
public sealed record BuildResult(
    bool Success,
    SymbolGraphStatistics Statistics,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyCollection<RenameDecision> RenameDecisions);
```

**Properties**:
- `Success`: True if no PhaseGate errors occurred
- `Statistics`: Counts of namespaces, types, members
- `Diagnostics`: All errors, warnings, info diagnostics
- `RenameDecisions`: Complete audit trail of all rename decisions

### EmissionPlan

```csharp
public sealed record EmissionPlan(
    SymbolGraph Graph,
    ImportPlan Imports,
    EmitOrder Order);
```

**Properties**:
- `Graph`: Validated SymbolGraph ready to emit
- `Imports`: Import/export plan for all namespaces
- `Order`: Deterministic emission order for types and members

## Pipeline Execution

### Phase Sequence

```
Assembly Paths (string[])
    │
    ↓
┌─────────────────────────────────────────┐
│ PHASE 1: LOAD                           │
│                                         │
│ Input:  string[] (assembly paths)      │
│ Output: SymbolGraph (CLR facts)        │
│ Files:  4                               │
│ Time:   ~10% of build                   │
└────────────────┬────────────────────────┘
                 │
                 ↓ SymbolGraph (pure CLR)
                 │
┌────────────────┴────────────────────────┐
│ PHASE 2: NORMALIZE                      │
│                                         │
│ Input:  SymbolGraph                    │
│ Output: SymbolGraph (indexed, named)   │
│ Files:  3                               │
│ Time:   ~5% of build                    │
└────────────────┬────────────────────────┘
                 │
                 ↓ SymbolGraph (indexed)
                 │
┌────────────────┴────────────────────────┐
│ PHASE 3: SHAPE                          │
│                                         │
│ Input:  SymbolGraph                    │
│ Output: SymbolGraph (transformed)      │
│ Passes: 14 transformations              │
│ Files:  14                              │
│ Time:   ~40% of build                   │
└────────────────┬────────────────────────┘
                 │
                 ↓ SymbolGraph (TypeScript-ready)
                 │
┌────────────────┴────────────────────────┐
│ PHASE 4: PLAN                           │
│                                         │
│ Input:  SymbolGraph                    │
│ Output: EmissionPlan                   │
│ Files:  8                               │
│ Time:   ~30% of build                   │
│                                         │
│ Key: PhaseGate validation (26 checks)  │
└────────────────┬────────────────────────┘
                 │
                 ↓ EmissionPlan (validated)
                 │
┌────────────────┴────────────────────────┐
│ PHASE 5: EMIT                           │
│                                         │
│ Input:  EmissionPlan                   │
│ Output: File System (side effects)     │
│ Files:  11                              │
│ Time:   ~15% of build                   │
└────────────────┬────────────────────────┘
                 │
                 ↓
        Output Files (.d.ts, .json, .js)
```

### Data Transformations

| Phase | Input | Output | Mutability | Key Operations |
|-------|-------|--------|------------|----------------|
| Load | `string[]` | `SymbolGraph` | Immutable | Reflection, type reference creation |
| Normalize | `SymbolGraph` | `SymbolGraph` | Immutable | Indexing, signature normalization, name reservation |
| Shape | `SymbolGraph` | `SymbolGraph` | Immutable | 14 transformation passes |
| Plan | `SymbolGraph` | `EmissionPlan` | Immutable | Import planning, ordering, validation |
| Emit | `EmissionPlan` | File I/O | Side effects | File generation |

### Immutability Pattern

Every phase receives immutable input and returns immutable output:

```csharp
// Phase signature pattern
public static TOutput PhaseFunction(BuildContext ctx, TInput input)
{
    // Read input (immutable)
    // Apply transformations
    // Return new output (immutable)
    return transformedOutput;
}

// Example: Shape pass
public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph)
{
    // graph is immutable - read only
    var transformed = ApplyTransformation(graph);
    // Return new immutable graph
    return transformed;
}
```

## Call Graph

### Top-Level Calls

```
Program.Main()
    ↓
GenerateCommand.Execute()
    ↓
SinglePhaseBuilder.Build()
    ├→ BuildContext.Create()
    ├→ LoadPhase()
    │   ├→ AssemblyLoader.LoadClosure()
    │   ├→ ReflectionReader.ReadAssemblies()
    │   └→ InterfaceMemberSubstitutor.SubstituteClosedInterfaces()
    ├→ ShapePhase()
    │   ├→ GlobalInterfaceIndex.Build()
    │   ├→ InterfaceInliner.Inline()
    │   ├→ StructuralConformance.Analyze()
    │   ├→ ExplicitImplSynthesizer.Synthesize()
    │   ├→ DiamondResolver.Resolve()
    │   ├→ BaseOverloadAdder.AddOverloads()
    │   ├→ StaticSideAnalyzer.Analyze()
    │   ├→ IndexerPlanner.Plan()
    │   ├→ HiddenMemberPlanner.Plan()
    │   ├→ FinalIndexersPass.Run()
    │   ├→ ClassSurfaceDeduplicator.Deduplicate()
    │   ├→ ConstraintCloser.Close()
    │   ├→ OverloadReturnConflictResolver.Resolve()
    │   ├→ ViewPlanner.Plan()
    │   ├→ NameReservation.ReserveAll()
    │   ├→ OverloadUnifier.UnifyOverloads()
    │   └→ MemberDeduplicator.Deduplicate()
    ├→ PlanPhase()
    │   ├→ ImportGraph.Build()
    │   ├→ ImportPlanner.PlanImports()
    │   ├→ EmitOrderPlanner.PlanOrder()
    │   ├→ InterfaceConstraintAuditor.Audit()
    │   └→ PhaseGate.Validate()
    └→ EmitPhase()
        ├→ SupportTypesEmitter.Emit()
        ├→ InternalIndexEmitter.Emit()
        ├→ FacadeEmitter.Emit()
        ├→ MetadataEmitter.Emit()
        ├→ BindingEmitter.Emit()
        └→ ModuleStubEmitter.Emit()
```

### Renamer Interaction

SymbolRenamer is called throughout Shape and Plan phases:

```
ShapePhase()
    ↓
NameReservation.ReserveAll()
    ├→ ctx.Renamer.ReserveTypeName() (for each type)
    └→ ctx.Renamer.ReserveMemberName() (for each member)
        ↓
PlanPhase()
    ↓
PhaseGate.Validate()
    ├→ ctx.Renamer.GetFinalTypeName() (for validation)
    └→ ctx.Renamer.GetFinalMemberName() (for validation)
        ↓
EmitPhase()
    ↓
InternalIndexEmitter.Emit()
    ├→ ctx.Renamer.GetFinalTypeName() (for .d.ts)
    └→ ctx.Renamer.GetFinalMemberName() (for .d.ts)
```

### DiagnosticBag Interaction

Diagnostics recorded throughout pipeline:

```
All Phases
    ↓
ctx.Diagnostics.Add(severity, code, message)
    ↓
PhaseGate.Validate()
    ├→ Records all validation failures
    └→ Writes summary JSON and detailed text files
        ↓
SinglePhaseBuilder.Build()
    ↓
Return BuildResult with Diagnostics
```

## Error Handling

### Phase Failure Strategy

Each phase handles errors differently:

**Load Phase**:
- Assembly loading failures → throw exception (fatal)
- Missing dependencies → PhaseGate PG_LOAD_001 error
- Reflection errors → throw exception (fatal)

**Shape Phase**:
- Transformation errors → record diagnostic, continue
- Invalid state → record diagnostic, continue
- Most errors caught by PhaseGate in Plan phase

**Plan Phase**:
- Import planning errors → record diagnostic
- PhaseGate validation errors → record diagnostic
- Fatal: Any ERROR-level diagnostic prevents emission

**Emit Phase**:
- File I/O errors → throw exception (fatal)
- Printer errors → throw exception (fatal)

### Diagnostic Severity

- **ERROR**: Blocks emission, causes Build() to return Success=false
- **WARNING**: Logged but doesn't block emission
- **INFO**: Informational only

### Success Determination

```csharp
// After PhaseGate validation
bool hasErrors = ctx.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

if (hasErrors)
{
    // Skip Emit phase
    return new BuildResult(
        Success: false,
        Statistics: graph.GetStatistics(),
        Diagnostics: ctx.Diagnostics,
        RenameDecisions: ctx.Renamer.GetAllDecisions());
}

// Proceed to Emit phase
EmitPhase(ctx, plan, outputDirectory);

return new BuildResult(Success: true, ...);
```

## Logging and Instrumentation

### Log Categories

BuildContext supports selective logging:

```csharp
var logCategories = new HashSet<string>
{
    "Load",
    "Shape",
    "ViewPlanner",
    "PhaseGate",
    "Emit"
};

var ctx = BuildContext.Create(logger: Console.WriteLine, logCategories: logCategories);
```

### Logging Points

- **Load Phase**: Assembly loading, closure resolution
- **Shape Phase**: Each transformation pass start/end
- **ViewPlanner**: Detailed view planning logs
- **PhaseGate**: Validation summary, diagnostic counts
- **Emit Phase**: File writes, emitter execution

### Debug Output

Optional debug files:

```csharp
// Generated if --debug-snapshot flag used
<output-dir>/assemblies/<Assembly>.snapshot.json
<output-dir>/assemblies/assemblies-manifest.json
<output-dir>/namespaces/<Namespace>.snapshot.json

// Generated if --debug-typelist flag used
<namespace>/typelist.json

// Generated always in Plan phase
.tests/phasegate-summary.json
.tests/phasegate-diagnostics.txt
```

## Performance Considerations

### Memory Usage

- Entire SymbolGraph kept in memory (~50-100MB for BCL)
- String interning reduces memory by ~30%
- Immutable collections: Higher memory but safe

### Execution Time

**BCL Build (4,047 types)**:
- Total: ~30-60 seconds
- Load: ~3-6 seconds (10%)
- Shape: ~12-24 seconds (40%)
- Plan: ~9-18 seconds (30%)
- Emit: ~6-12 seconds (20%)

**Optimization Opportunities**:
- Parallelize independent Shape passes
- Parallelize namespace emission
- Cache reflection results
- Reduce allocations in hot paths

### Scalability

- Linear scaling with number of types
- Shape passes dominate execution time
- PhaseGate validation time proportional to type count
- File I/O becomes bottleneck for large builds

## Testing

### Unit Testing

Each phase tested independently:

```csharp
[Test]
public void InterfaceInliner_FlattensHierarchy()
{
    var ctx = BuildContext.Create();
    var graph = CreateTestGraph();

    var result = InterfaceInliner.Inline(ctx, graph);

    Assert.That(result.GetInterface("IList_1").Members,
        Contains.AllMembersFrom("IEnumerable_1", "ICollection_1", "IList_1"));
}
```

### Integration Testing

Full pipeline tests:

```csharp
[Test]
public void Build_GeneratesBCL_WithZeroErrors()
{
    var result = SinglePhaseBuilder.Build(
        assemblyPaths: GetBCLPaths(),
        outputDirectory: "./test-output",
        policy: null);

    Assert.That(result.Success, Is.True);
    Assert.That(result.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error), Is.Zero);
}
```

### Validation Testing

PhaseGate tests validate error detection:

```csharp
[Test]
public void PhaseGate_DetectsUnsanitizedIdentifiers()
{
    var ctx = BuildContext.Create();
    var graph = CreateGraphWithReservedWord("class");

    PhaseGate.Validate(ctx, graph, imports, constraintFindings);

    Assert.That(ctx.Diagnostics, Contains.Code("PG_ID_001"));
}
```

## Related Documentation

- [phase-1-load.md](phase-1-load.md) - Load phase details
- [phase-2-normalize.md](phase-2-normalize.md) - Normalize phase details
- [phase-3-shape.md](phase-3-shape.md) - Shape phase details
- [phase-4-plan.md](phase-4-plan.md) - Plan phase details
- [phase-5-emit.md](phase-5-emit.md) - Emit phase details
- [phasegate.md](phasegate.md) - PhaseGate validation
- [build-context.md](build-context.md) - BuildContext services
