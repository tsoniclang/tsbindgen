# 10. Call Graphs - Concise

## Entry Point Chain

```
Program.Main → RootCommand.InvokeAsync → GenerateCommand.ExecuteAsync
  → ExecuteNewPipelineAsync → SinglePhaseBuilder.Build
```

## Phase 1: Load

```
LoadPhase
├─→ AssemblyLoader.LoadClosure(seedPaths, refPaths, strictVersions)
│   ├─→ BuildCandidateMap → scans ref dirs for .dlls
│   ├─→ ResolveClosure → BFS for transitive deps via PEReader
│   ├─→ ValidateAssemblyIdentity → PG_LOAD_002/003 (PKT, version drift)
│   ├─→ FindCoreLibrary → locate System.Private.CoreLib.dll
│   └─→ new MetadataLoadContext(resolver, coreLib)
│
└─→ ReflectionReader.ReadAssemblies(loadContext, paths)
    ├─→ AssemblyLoader.LoadAssemblies → loadContext.LoadFromAssemblyPath
    └─→ For each type: ReadType(type)
        ├─→ DetermineTypeKind, ComputeAccessibility
        ├─→ TypeReferenceFactory.Create* → GenericParameterSymbol, NamedTypeReference
        └─→ ReadMembers → ReadMethod, ReadProperty, ReadField, ReadEvent, ReadConstructor
            └─→ CreateMethodSignature → ctx.CanonicalizeMethod
            └─→ ReadParameter → TypeScriptReservedWords.SanitizeParameterName
            └─→ IsMethodOverride → check MethodAttributes.NewSlot

└─→ InterfaceMemberSubstitution.SubstituteClosedInterfaces
    ├─→ BuildInterfaceIndex
    └─→ ProcessType → BuildSubstitutionMap (T → int for IComparable<int>)
```

## Phase 2: Normalize

```
graph.WithIndices() → populates TypeIndex, NamespaceIndex for O(1) lookups
```

## Phase 3: Shape (14 passes)

```
ShapePhase
├─→ Pass 1: GlobalInterfaceIndex.Build + InterfaceDeclIndex.Build
│   └─→ ComputeMethodSignatures, CollectInheritedSignatures
│
├─→ Pass 2: StructuralConformance.Analyze
│   └─→ Synthesize ViewOnly members (EmitScope.ViewOnly, Provenance.InterfaceView)
│
├─→ Pass 3: InterfaceInliner.Inline
│   └─→ Flatten interface members onto class (EmitScope.ClassSurface, Provenance.InterfaceInlining)
│
├─→ Pass 4: ExplicitImplSynthesizer.Synthesize
│   └─→ Tag explicit impls (name contains '.') as ViewOnly
│
├─→ Pass 5: DiamondResolver.Resolve
│   └─→ Pick winner for diamond conflicts → PG_INT_005
│
├─→ Pass 6: BaseOverloadAdder.AddOverloads
│   └─→ Add base overloads (Provenance.BaseOverload)
│
├─→ Pass 7: StaticSideAnalyzer.Analyze
│   └─→ Check static/instance collision → PG_NAME_002
│
├─→ Pass 8: IndexerPlanner.Plan
│   └─→ Set EmitScope.Omit for IndexParameters.Count > 0
│
├─→ Pass 9: HiddenMemberPlanner.Plan
│   └─→ Reserve renamed names via ctx.Renamer
│
├─→ Pass 10: FinalIndexersPass.Run
│   └─→ Catch indexer leaks → PG_EMIT_001
│
├─→ Pass 10.5: ClassSurfaceDeduplicator.Deduplicate (M5)
│   └─→ Group by TsEmitName, pick winner, demote losers → PG_DEDUP_001
│
├─→ Pass 11: ConstraintCloser.Close
│   └─→ Compute transitive closure of constraints
│
├─→ Pass 12: OverloadReturnConflictResolver.Resolve
│   └─→ Disambiguate return type conflicts → PG_OVERLOAD_001
│
├─→ Pass 13: ViewPlanner.Plan
│   └─→ Finalize ViewOnly membership, set SourceInterface
│
└─→ Pass 14: MemberDeduplicator.Deduplicate
    └─→ Remove duplicates by StableId → PG_DEDUP_002
```

**Key**: Shape passes are PURE (return new graph), Renamer is MUTATED (accumulates decisions)

## Phase 3.5: Name Reservation

```
NameReservation.ReserveAllNames
│
├─→ Step 1: Reserve Type Names
│   └─→ Shared.ComputeTypeRequestedBase (List`1 → List)
│   └─→ ctx.Renamer.ReserveTypeName → _typeDecisions[stableId][scope] = RenameDecision
│
├─→ Step 2: Reserve Class Surface Member Names
│   └─→ Reservation.ReserveMemberNamesOnly (EmitScope.ClassSurface only)
│       └─→ Shared.ComputeMemberRequestedBase (sanitize, camelCase if policy)
│       └─→ ScopeFactory.ClassSurface(type, isStatic) → ns/internal/TypeName/instance|static
│       └─→ ctx.Renamer.ReserveMemberName → _memberDecisions[stableId][classScope]
│
├─→ Step 3: Build Class Surface Name Sets
│   └─→ Collect all classInstanceNames, classStaticNames, classAllNames
│
├─→ Step 4: Reserve View-Scoped Member Names (M5)
│   └─→ Reservation.ReserveViewMemberNamesOnly (EmitScope.ViewOnly only)
│       └─→ ScopeFactory.ViewScope(type, sourceInterface, isStatic) → different scope!
│       └─→ Check collision with classAllNames → PG_NAME_003/004
│       └─→ ctx.Renamer.ReserveMemberName → _memberDecisions[stableId][viewScope]
│       └─→ SAME member can have DIFFERENT names in class vs view scope
│
├─→ Step 5: Post-Reservation Audit (fail fast)
│   └─→ Audit.AuditReservationCompleteness → verify all EmitScope!=Omit have decision
│
└─→ Step 6: Apply Names to Graph (pure transform)
    └─→ Application.ApplyNamesToGraph
        └─→ ctx.Renamer.GetFinalTypeName → set type.TsEmitName
        └─→ ctx.Renamer.GetFinalMemberName → set member.TsEmitName
```

**Invariants**: Every emitted symbol has TsEmitName, every TsEmitName has RenameDecision, view members can differ from class members

## Phase 4: Plan

```
PlanPhase
├─→ ImportGraph.Build → collect foreign type references
├─→ ImportPlanner.PlanImports → create import statements, resolve alias collisions
├─→ EmitOrderPlanner.PlanOrder → topological sort, stable ordering
│
├─→ Phase 4.5: OverloadUnifier.UnifyOverloads
│   └─→ Group by TsEmitName, mark UnifiedDeclaration/UnifiedImplementation
│
├─→ Phase 4.6: InterfaceConstraintAuditor.Audit
│   └─→ Check constructor/base constraints → PG_CONSTRAINT_001/002
│
└─→ Phase 4.7: PhaseGate.Validate (20+ validations, 40+ codes)
```

## Phase 4.7: PhaseGate Validation (20+ functions)

```
PhaseGate.Validate → create ValidationContext

Core Validations:
├─→ ValidateTypeNames → TsEmitName set, valid identifier → PG_NAME_001, PG_IDENT_001
├─→ ValidateMemberNames → same for members
├─→ ValidateGenericParameters → PG_GEN_001
├─→ ValidateInterfaceConformance → PG_INT_001
├─→ ValidateInheritance → PG_INH_001
├─→ ValidateEmitScopes → PG_SCOPE_001
├─→ ValidateImports → PG_IMPORT_002
└─→ ValidatePolicyCompliance → PG_POLICY_001

Names Module:
├─→ ValidateIdentifiers → reserved words, special chars → PG_IDENT_002
├─→ ValidateOverloadCollisions → PG_OVERLOAD_002
└─→ ValidateClassSurfaceUniqueness → PG_NAME_005 (dedup failed)

Views Module (M5):
├─→ ValidateIntegrity (3 hard rules, FATAL errors)
│   ├─→ Rule 1: ViewOnly MUST have SourceInterface → PG_VIEW_001
│   ├─→ Rule 2: ViewOnly MUST have ClassSurface twin → PG_VIEW_002
│   └─→ Rule 3: Twins MUST have same CLR signature → PG_VIEW_003
└─→ ValidateMemberScoping → check view-vs-class collision → PG_NAME_003/004

Scopes Module (M5):
├─→ ValidateEmitScopeInvariants → ViewOnly needs SourceInterface, ClassSurface doesn't → PG_INT_002/003
└─→ ValidateScopeMismatches → verify Renamer decisions in correct scope → PG_SCOPE_003/004

Constraints Module (M4):
└─→ EmitDiagnostics → PG_CONSTRAINT_001/002

Finalization Module (M6):
└─→ Validate → 9 checks (PG_FIN_001-009)

Types Module (M7/M7a/M7b):
├─→ ValidateTypeMapCompliance → detect unsupported CLR types → PG_TYPEMAP_001
├─→ ValidateExternalTypeResolution → PG_LOAD_001 (type not in closure)
└─→ ValidatePrinterNameConsistency → simulate TypeRefPrinter chain → PG_PRINT_001

ImportExport Module (M8/M9/M10):
├─→ ValidatePublicApiSurface → prevent internal leaks → PG_API_001/002
├─→ ValidateImportCompleteness → PG_IMPORT_001
└─→ ValidateExportCompleteness → PG_EXPORT_001

Final:
├─→ Print diagnostic summary table (grouped, sorted by count)
├─→ If ErrorCount > 0: build fails
├─→ WriteDiagnosticsFile → .tests/phasegate-diagnostics.txt
└─→ WriteSummaryJson → .tests/phasegate-summary.json
```

**Order matters**: TypeMap → External Resolution → API Surface → Import Completeness

## Phase 5: Emit

```
EmitPhase
├─→ SupportTypesEmit.Emit → _support/types.d.ts (branded types, unsafe markers)
│
├─→ InternalIndexEmitter.Emit → namespace/internal/index.d.ts
│   ├─→ EmitFileHeader, EmitImports, EmitNamespaceDeclaration
│   └─→ For each type by EmitOrder:
│       ├─→ ClassPrinter.PrintClassDeclaration (class/struct)
│       │   ├─→ Print generics, extends, implements
│       │   ├─→ MethodPrinter.PrintConstructor/PrintMethod
│       │   ├─→ Print fields, properties, events (EmitScope.ClassSurface only)
│       │   └─→ Print views: TypeName_View_InterfaceName (EmitScope.ViewOnly, grouped by SourceInterface)
│       ├─→ ClassPrinter.PrintInterfaceDeclaration
│       ├─→ Print enum, delegate, static namespace
│       └─→ TypeRefPrinter.Print → TypeNameResolver.ResolveTypeName
│           └─→ ctx.Renamer.GetFinalTypeName
│
├─→ FacadeEmitter.Emit → namespace/index.d.ts (re-exports from internal)
├─→ MetadataEmitter.Emit → namespace/metadata.json (CLR info, omissions)
├─→ BindingEmitter.Emit → namespace/bindings.json (CLR→TS mappings)
└─→ ModuleStubEmitter.Emit → namespace/index.js (throws)
```

**Key**: Emit uses TsEmitName from graph, NO further name transformation

## Cross-Cutting Call Graphs

### SymbolRenamer

```
ReserveTypeName ← NameReservation, HiddenMemberPlanner, ClassSurfaceDeduplicator
ReserveMemberName ← Reservation (class + view), IndexerPlanner, HiddenMemberPlanner
GetFinalTypeName ← Application (Phase 3.5), TypeNameResolver (Phase 5), PhaseGate validations
GetFinalMemberName ← Application (Phase 3.5), ClassPrinter (Phase 5), Views validation
TryGetDecision ← NameReservation, Scopes validation, Audit

Data: _typeDecisions[StableId][Scope] = RenameDecision { Requested, Final, Context, Source }
      _memberDecisions[StableId][Scope] = RenameDecision
```

### DiagnosticBag

```
Error() ← AssemblyLoader (PG_LOAD_002/003), PhaseGate (40+ codes), BuildContext exceptions
Warning() ← AssemblyLoader, PhaseGate
Info() ← PhaseGate
GetAll() ← SinglePhaseBuilder (BuildResult), PhaseGate
HasErrors() ← SinglePhaseBuilder
```

### Policy

```
Emission.MemberNameTransform ← Shared.ComputeMemberRequestedBase (camelCase)
Omissions.OmitIndexers ← IndexerPlanner.Plan
Safety.RequireUnsafeMarkers ← TypeRefPrinter.Print (UnsafePointer/ByRef)
Validation.StrictVersionChecks ← AssemblyLoader.ValidateAssemblyIdentity
```

### BuildContext.Log

```
Called from all phases, all passes
Logs if: verboseLogging == true OR logCategories.Contains("category")
```

## Output Files Per Namespace

```
namespace/
├── internal/index.d.ts    # Full declarations (classes, views, members)
├── index.d.ts             # Public facade (re-exports internal)
├── index.js               # Module stub (throws)
├── metadata.json          # CLR info for Tsonic compiler
└── bindings.json          # CLR→TS name mappings

_support/types.d.ts        # Branded numerics, unsafe markers (once per build)
```

## Complete Example: List<T> Trace (Key Steps Only)

```
CLI: dotnet run -- generate --use-new-pipeline -a System.Collections.dll -o out
  → SinglePhaseBuilder.Build

Phase 1: Load
  → ReflectionReader.ReadType(List`1)
  → ReadMethod(Add) → CreateMethodSignature → "Add(T):System.Void"
  → Creates: MethodSymbol { ClrName: "Add", EmitScope: ClassSurface, Provenance: Original }

Phase 2: Normalize
  → graph.WithIndices() → TypeIndex["System.Collections.Generic.List`1"] = List`1

Phase 3: Shape (14 passes, List<T> mostly unchanged)
  → IndexerPlanner: Item[int] → EmitScope = Omit

Phase 3.5: Name Reservation
  → ComputeTypeRequestedBase("List`1") → "List"
  → Renamer.ReserveTypeName → Final = "List_1"
  → ComputeMemberRequestedBase("Add") → "Add"
  → Renamer.ReserveMemberName(instance scope) → Final = "Add"
  → Application.ApplyNamesToGraph → type.TsEmitName = "List_1", method.TsEmitName = "Add"

Phase 4: Plan
  → ImportGraph.Build → foreign types: System.Object
  → PhaseGate.Validate → all 20+ validations pass → ErrorCount = 0

Phase 5: Emit
  → InternalIndexEmitter: ClassPrinter.PrintClassDeclaration
    → Writes: export class List_1<T> extends Object implements IList_1<T> { ... }
    → MethodPrinter.PrintMethod(Add)
      → TypeRefPrinter.Print(T) → TypeNameResolver → "T"
      → Writes: Add(item: T): void;
  → File.WriteAllTextAsync("out/System.Collections.Generic/internal/index.d.ts")
  → FacadeEmitter, MetadataEmitter, BindingEmitter, ModuleStubEmitter

BuildResult: { Success: true, TypeCount: 1, Diagnostics: [] }
```

## Critical Invariants

1. **Phase purity**: Shape passes return new graph (pure), Renamer mutated (accumulates decisions)
2. **Name reservation**: Every emitted symbol has TsEmitName, every TsEmitName has RenameDecision
3. **View scoping**: View members can have different TsEmitName from class members (different scope)
4. **PhaseGate**: Runs before emission, fails fast on errors
5. **Emit reads only**: Phase 5 uses TsEmitName from graph, no transformation
6. **MetadataLoadContext**: Use name-based comparisons (type.FullName == "System.Boolean"), NOT typeof()
