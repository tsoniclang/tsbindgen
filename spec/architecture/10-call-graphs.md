# 10. Call Graphs - Complete Call Chains

## 1. Overview

This document traces complete call chains through the SinglePhase pipeline from CLI entry point through file emission. Each section shows who calls what, in execution order, with the actual function names from the codebase.

**What are call graphs?**
- The complete chain of function calls from start to finish
- Shows execution flow through the entire pipeline
- Documents which functions call which other functions
- Provides exact function signatures and locations

**Why document call graphs?**
- Essential for understanding execution flow
- Critical for debugging (trace where code is called from)
- Enables impact analysis (what breaks if I change this?)
- Guides new developers through the codebase

## 2. Entry Point Call Chain

Starting from CLI invocation through SinglePhaseBuilder.Build():

```
User executes CLI command
  ↓
Program.Main(string[] args)
  Location: src/tsbindgen/Cli/Program.cs
  ↓
RootCommand.InvokeAsync(args)
  System.CommandLine infrastructure
  ↓
GenerateCommand.SetHandler(async context => ...)
  Location: src/tsbindgen/Cli/GenerateCommand.cs:114
  ↓
GenerateCommand.ExecuteAsync(...)
  Location: src/tsbindgen/Cli/GenerateCommand.cs:153
  ↓
┌─────────────────────────────────────────┐
│ Route based on --use-new-pipeline flag │
└─────────────────────────────────────────┘
  ↓
GenerateCommand.ExecuteNewPipelineAsync(...)
  Location: src/tsbindgen/Cli/GenerateCommand.cs:440
  ↓
SinglePhaseBuilder.Build(assemblyPaths, outDir, policy, logger, verbose, logCategories)
  Location: src/tsbindgen/SinglePhase/SinglePhaseBuilder.cs:27
  ↓
┌────────────────────────────┐
│ Five-Phase Pipeline Starts │
└────────────────────────────┘
  ↓
  [Phase 1: Load]
  [Phase 2: Normalize]
  [Phase 3: Shape]
  [Phase 3.5: Name Reservation]
  [Phase 4: Plan]
  [Phase 4.5-4.7: Validation]
  [Phase 5: Emit]
```

## 3. Phase 1: Load Call Graph

Complete call chain for assembly loading and reflection:

```
SinglePhaseBuilder.Build()
  ↓
LoadPhase(ctx, assemblyPaths)
  Location: src/tsbindgen/SinglePhase/SinglePhaseBuilder.cs:118
  ↓
new AssemblyLoader(ctx)
  Location: src/tsbindgen/SinglePhase/Load/AssemblyLoader.cs:22
  ↓
AssemblyLoader.LoadClosure(seedPaths, refPaths, strictVersions)
  Location: src/tsbindgen/SinglePhase/Load/AssemblyLoader.cs:113
  ↓
  ├─→ BuildCandidateMap(refPaths)
  │   Location: AssemblyLoader.cs:167
  │   Returns: Dictionary<AssemblyKey, List<string>>
  │   Purpose: Scan reference directories for all .dll files
  │
  ├─→ ResolveClosure(seedPaths, candidateMap, strictVersions)
  │   Location: AssemblyLoader.cs:207
  │   Returns: Dictionary<AssemblyKey, string>
  │   Purpose: BFS traversal to find all transitive dependencies
  │   ↓
  │   Uses System.Reflection.PortableExecutable.PEReader
  │   to read assembly metadata without loading
  │
  ├─→ ValidateAssemblyIdentity(resolvedPaths, strictVersions)
  │   Location: AssemblyLoader.cs:295
  │   Purpose: Check for PG_LOAD_002 (mixed PKT), PG_LOAD_003 (version drift)
  │
  ├─→ FindCoreLibrary(resolvedPaths)
  │   Location: AssemblyLoader.cs:354
  │   Returns: Path to System.Private.CoreLib.dll
  │
  ├─→ new PathAssemblyResolver(resolvedPaths.Values)
  │   System.Reflection infrastructure
  │
  └─→ new MetadataLoadContext(resolver, "System.Private.CoreLib")
      System.Reflection infrastructure
      Returns: MetadataLoadContext with all assemblies loaded
  ↓
new ReflectionReader(ctx)
  Location: src/tsbindgen/SinglePhase/Load/ReflectionReader.cs:14
  ↓
ReflectionReader.ReadAssemblies(loadContext, allAssemblyPaths)
  Location: ReflectionReader.cs:28
  ↓
  ├─→ AssemblyLoader.LoadAssemblies(loadContext, assemblyPaths)
  │   Location: AssemblyLoader.cs:60
  │   ↓
  │   For each assembly path:
  │     └─→ loadContext.LoadFromAssemblyPath(path)
  │         System.Reflection.MetadataLoadContext
  │
  └─→ For each loaded assembly:
      └─→ For each type in assembly.GetTypes():
          └─→ ReadType(type)
              Location: ReflectionReader.cs:102
              ↓
              ├─→ DetermineTypeKind(type)
              │   Location: ReflectionReader.cs:177
              │   Returns: TypeKind enum (Class, Interface, Enum, etc.)
              │
              ├─→ ComputeAccessibility(type)
              │   Location: ReflectionReader.cs:154
              │   Returns: Accessibility enum (Public, Internal)
              │   Handles nested type accessibility correctly
              │
              ├─→ TypeReferenceFactory.CreateGenericParameterSymbol(param)
              │   Location: src/tsbindgen/SinglePhase/Load/TypeReferenceFactory.cs
              │   For each generic parameter
              │
              ├─→ TypeReferenceFactory.Create(type.BaseType)
              │   Create TypeReference for base type
              │
              ├─→ TypeReferenceFactory.Create(iface) for each interface
              │   Create TypeReference for each implemented interface
              │
              ├─→ ReadMembers(type)
              │   Location: ReflectionReader.cs:189
              │   ↓
              │   ├─→ For each method in type.GetMethods(publicInstance | publicStatic):
              │   │   └─→ ReadMethod(method, type)
              │   │       Location: ReflectionReader.cs:264
              │   │       ↓
              │   │       ├─→ CreateMethodSignature(method)
              │   │       │   Location: ReflectionReader.cs:465
              │   │       │   ↓
              │   │       │   └─→ ctx.CanonicalizeMethod(name, paramTypes, returnType)
              │   │       │       BuildContext.cs - creates unique signature
              │   │       │
              │   │       ├─→ ReadParameter(param) for each parameter
              │   │       │   Location: ReflectionReader.cs:446
              │   │       │   ↓
              │   │       │   ├─→ TypeScriptReservedWords.SanitizeParameterName(name)
              │   │       │   │   Handle reserved words like 'default', 'const'
              │   │       │   │
              │   │       │   └─→ TypeReferenceFactory.Create(param.ParameterType)
              │   │       │       Create TypeReference for parameter type
              │   │       │
              │   │       ├─→ TypeReferenceFactory.CreateGenericParameterSymbol(arg)
              │   │       │   For generic method parameters
              │   │       │
              │   │       ├─→ TypeReferenceFactory.Create(method.ReturnType)
              │   │       │   Create TypeReference for return type
              │   │       │
              │   │       └─→ IsMethodOverride(method)
              │   │           Location: ReflectionReader.cs:524
              │   │           Check MethodAttributes flags (NewSlot)
              │   │
              │   ├─→ For each property in type.GetProperties(publicInstance | publicStatic):
              │   │   └─→ ReadProperty(property, type)
              │   │       Location: ReflectionReader.cs:312
              │   │       Similar structure to ReadMethod
              │   │
              │   ├─→ For each field in type.GetFields(publicInstance | publicStatic):
              │   │   └─→ ReadField(field, type)
              │   │       Location: ReflectionReader.cs:359
              │   │
              │   ├─→ For each event in type.GetEvents(publicInstance | publicStatic):
              │   │   └─→ ReadEvent(evt, type)
              │   │       Location: ReflectionReader.cs:385
              │   │
              │   └─→ For each ctor in type.GetConstructors(BindingFlags.Public):
              │       └─→ ReadConstructor(ctor, type)
              │           Location: ReflectionReader.cs:426
              │
              └─→ For each nested type in type.GetNestedTypes(BindingFlags.Public):
                  └─→ ReadType(nestedType)
                      Recursive call for nested types
  ↓
Returns: SymbolGraph with Namespaces → Types → Members
  All members have EmitScope = EmitScope.ClassSurface (initial state)
  ↓
InterfaceMemberSubstitution.SubstituteClosedInterfaces(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Load/InterfaceMemberSubstitutor.cs:20
  ↓
  ├─→ BuildInterfaceIndex(graph)
  │   Location: InterfaceMemberSubstitutor.cs:47
  │   Returns: Dictionary<string, TypeSymbol> of all interfaces
  │
  └─→ For each type in graph:
      └─→ ProcessType(ctx, type, interfaceIndex)
          Location: InterfaceMemberSubstitutor.cs:65
          ↓
          For each closed generic interface (e.g., IComparable<int>):
            └─→ BuildSubstitutionMap(ifaceSymbol, closedInterfaceRef)
                Location: InterfaceMemberSubstitutor.cs:102
                Creates map: T → int for IComparable<int>
                Used later by StructuralConformance, ViewPlanner
```

**Key Functions Called:**
- `System.Reflection.Assembly.GetTypes()` - Get all types
- `System.Reflection.Type.GetMethods()` - Get methods
- `System.Reflection.Type.GetProperties()` - Get properties
- `System.Reflection.Type.GetFields()` - Get fields
- `System.Reflection.Type.GetEvents()` - Get events
- `System.Reflection.Type.GetConstructors()` - Get constructors
- `System.Reflection.Type.GetInterfaces()` - Get implemented interfaces
- `System.Reflection.MethodInfo.GetParameters()` - Get method parameters

## 4. Phase 2: Normalize Call Graph

Index building for fast lookups:

```
SinglePhaseBuilder.Build()
  ↓
graph = graph.WithIndices()
  Location: src/tsbindgen/SinglePhase/Model/SymbolGraph.cs
  ↓
  Returns new SymbolGraph with TypeIndex, NamespaceIndex populated
  ↓
  Purpose: Enable O(1) lookups by CLR full name
```

**TypeIndex Structure:**
- Maps CLR full name → TypeSymbol
- Used by all subsequent phases for type resolution
- Example: `"System.Collections.Generic.List`1"` → TypeSymbol

**NamespaceIndex Structure:**
- Maps namespace name → NamespaceSymbol
- Used for cross-namespace lookups

## 5. Phase 3: Shape Call Graph

14 transformation passes that modify the symbol graph:

```
SinglePhaseBuilder.Build()
  ↓
ShapePhase(ctx, graph)
  Location: SinglePhaseBuilder.cs:165
  ↓
┌─────────────────────────────────────────────────────┐
│ Pass 1: Build Interface Indices (BEFORE flattening) │
└─────────────────────────────────────────────────────┘
  ↓
GlobalInterfaceIndex.Build(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/GlobalInterfaceIndex.cs:22
  ↓
  For each interface in graph:
    └─→ ComputeMethodSignatures(ctx, iface)
        └─→ ctx.CanonicalizeMethod(name, paramTypes, returnType)
            Create unique signature for each method
    └─→ ComputePropertySignatures(ctx, iface)
        └─→ ctx.CanonicalizeProperty(name, indexParams, propType)
            Create unique signature for each property
  ↓
  Populates: _globalIndex[interfaceFullName] = InterfaceInfo
  ↓
InterfaceDeclIndex.Build(ctx, graph)
  Location: GlobalInterfaceIndex.cs:149
  ↓
  For each interface in graph:
    ├─→ CollectInheritedSignatures(iface)
    │   Location: GlobalInterfaceIndex.cs:244
    │   Walk base interfaces, collect all inherited signatures
    │
    └─→ Compute declared-only signatures (exclude inherited)
        Store in _declIndex[interfaceFullName] = DeclaredMembers
  ↓
┌──────────────────────────────────────────────────────────┐
│ Pass 2: Structural Conformance (synthesizes ViewOnly)   │
└──────────────────────────────────────────────────────────┘
  ↓
graph = StructuralConformance.Analyze(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/StructuralConformance.cs
  ↓
  For each class/struct type:
    For each implemented interface:
      └─→ Check if class method matches interface signature
          ↓
          If no match found:
            └─→ Create ViewOnly synthetic method
                Set EmitScope = EmitScope.ViewOnly
                Set SourceInterface = interface CLR name
                Set Provenance = MemberProvenance.InterfaceView
  ↓
  Returns: New SymbolGraph with ViewOnly members added
  Purpose: Prepare for interface flattening
  ↓
┌────────────────────────────────────────────────────┐
│ Pass 3: Interface Inlining (flatten interfaces)   │
└────────────────────────────────────────────────────┘
  ↓
graph = InterfaceInliner.Inline(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/InterfaceInliner.cs
  ↓
  For each class/struct type:
    For each implemented interface (including base interfaces):
      For each interface method:
        └─→ Check if class already has matching signature
            ↓
            If not found:
              └─→ Create new method symbol
                  Set EmitScope = EmitScope.ClassSurface
                  Set Provenance = MemberProvenance.InterfaceInlining
                  Set SourceInterface = interface CLR name
                  Add to type's Methods collection
  ↓
  Returns: New SymbolGraph with inlined interface members
  Purpose: Flatten interface members onto class surface for TypeScript
  ↓
┌────────────────────────────────────────────────────────────┐
│ Pass 4: Explicit Interface Implementation Synthesis       │
└────────────────────────────────────────────────────────────┘
  ↓
graph = ExplicitImplSynthesizer.Synthesize(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/ExplicitImplSynthesizer.cs
  ↓
  For each class/struct type:
    For each method/property with name containing '.':
      └─→ Parse qualified name (e.g., "System.IDisposable.Dispose")
          ↓
          Create synthetic ViewOnly member
            Set EmitScope = EmitScope.ViewOnly
            Set SourceInterface = parsed interface name
            Set Provenance = MemberProvenance.ExplicitInterfaceImpl
  ↓
  Returns: New SymbolGraph with explicit impl members tagged
  Purpose: Handle C# explicit interface implementations
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 5: Diamond Inheritance Resolution          │
└──────────────────────────────────────────────────┘
  ↓
graph = DiamondResolver.Resolve(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/DiamondResolver.cs
  ↓
  For each interface with diamond inheritance pattern:
    └─→ Pick single implementation for ambiguous members
        Emit PG_INT_005 diagnostic if conflict
  ↓
  Returns: New SymbolGraph with diamond conflicts resolved
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 6: Base Overload Addition                  │
└──────────────────────────────────────────────────┘
  ↓
graph = BaseOverloadAdder.AddOverloads(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/BaseOverloadAdder.cs
  ↓
  For each class type:
    Walk inheritance chain
      For each base class method:
        └─→ Add overload on derived class if needed for TypeScript
            Set Provenance = MemberProvenance.BaseOverload
  ↓
  Returns: New SymbolGraph with base overloads added
  Purpose: Handle TypeScript override requirements
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 7: Static-Side Analysis                    │
└──────────────────────────────────────────────────┘
  ↓
StaticSideAnalyzer.Analyze(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/StaticSideAnalyzer.cs
  ↓
  For each type:
    └─→ Check for static/instance name collisions
        Emit PG_NAME_002 diagnostic if collision found
  ↓
  Mutates: ctx.Diagnostics (adds diagnostics)
  Returns: void (analysis only)
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 8: Indexer Planning                        │
└──────────────────────────────────────────────────┘
  ↓
graph = IndexerPlanner.Plan(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/IndexerPlanner.cs
  ↓
  For each property with IndexParameters.Count > 0:
    └─→ Set EmitScope = EmitScope.Omit
        Reserve name through ctx.Renamer
        Track in metadata.json
  ↓
  Returns: New SymbolGraph with indexers omitted
  Purpose: Handle C# indexers (TypeScript doesn't support overloaded indexers)
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 9: Hidden Member Planning (C# 'new')       │
└──────────────────────────────────────────────────┘
  ↓
HiddenMemberPlanner.Plan(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/HiddenMemberPlanner.cs
  ↓
  For each class type:
    For each member that hides base member:
      └─→ Reserve renamed name through ctx.Renamer
          Add disambiguation suffix
  ↓
  Mutates: ctx.Renamer (adds rename decisions)
  Returns: void
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 10: Final Indexers Pass                    │
└──────────────────────────────────────────────────┘
  ↓
graph = FinalIndexersPass.Run(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/FinalIndexersPass.cs
  ↓
  For each property:
    └─→ Verify no indexer properties have EmitScope = ClassSurface
        If found, set EmitScope = Omit
        Emit PG_EMIT_001 diagnostic
  ↓
  Returns: New SymbolGraph with indexer leaks fixed
  Purpose: Catch any indexers that escaped earlier passes
  ↓
┌──────────────────────────────────────────────────────────┐
│ Pass 10.5: Class Surface Deduplication (M5)             │
└──────────────────────────────────────────────────────────┘
  ↓
graph = ClassSurfaceDeduplicator.Deduplicate(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/ClassSurfaceDeduplicator.cs
  ↓
  For each type:
    Group ClassSurface members by TsEmitName
      If duplicates found:
        └─→ Pick winner (prefer Original over Synthesized)
            Demote losers to EmitScope.Omit
            Reserve winner name in ctx.Renamer
            Emit PG_DEDUP_001 diagnostic
  ↓
  Returns: New SymbolGraph with duplicates removed
  Purpose: Handle duplicate names from different Shape passes
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 11: Constraint Closure                     │
└──────────────────────────────────────────────────┘
  ↓
graph = ConstraintCloser.Close(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/ConstraintCloser.cs
  ↓
  For each generic type/method:
    └─→ Compute transitive closure of constraints
        Example: T : IComparable<U>, U : IList<V> → T : IList<V>
  ↓
  Returns: New SymbolGraph with complete constraint sets
  Purpose: Ensure TypeScript type parameters have complete constraints
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 12: Return-Type Conflict Resolution        │
└──────────────────────────────────────────────────┘
  ↓
graph = OverloadReturnConflictResolver.Resolve(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/OverloadReturnConflictResolver.cs
  ↓
  For each method group with same name:
    └─→ Check for different return types with compatible signatures
        If conflict found:
          └─→ Add disambiguation suffix to one method
              Reserve renamed name in ctx.Renamer
              Emit PG_OVERLOAD_001 diagnostic
  ↓
  Returns: New SymbolGraph with return type conflicts resolved
  Purpose: Handle TypeScript overload return type restrictions
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 13: View Planning (explicit interface views)│
└──────────────────────────────────────────────────┘
  ↓
graph = ViewPlanner.Plan(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/ViewPlanner.cs
  ↓
  For each class/struct type:
    For each ViewOnly member:
      └─→ Verify SourceInterface is set
          Check if member should be in view
          If yes:
            Keep EmitScope = ViewOnly
            Set SourceInterface
          If no:
            Set EmitScope = Omit
  ↓
  Returns: New SymbolGraph with view membership finalized
  Purpose: Decide which ViewOnly members go in which views
  ↓
┌──────────────────────────────────────────────────┐
│ Pass 14: Final Member Deduplication              │
└──────────────────────────────────────────────────┘
  ↓
graph = MemberDeduplicator.Deduplicate(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Shape/MemberDeduplicator.cs
  ↓
  For each type:
    Group members by StableId
      If duplicates found:
        └─→ Keep first occurrence
            Remove duplicates
            Emit PG_DEDUP_002 diagnostic
  ↓
  Returns: New SymbolGraph with all duplicates removed
  Purpose: Final cleanup of any remaining duplicates
```

**Key Observation:**
- Shape passes are PURE - each returns a new SymbolGraph
- Members flow through passes accumulating EmitScope, Provenance, SourceInterface
- Renamer is mutated (not pure) - accumulates rename decisions

## 6. Phase 3.5: Name Reservation Call Graph

Central naming phase - reserves all TypeScript names:

```
SinglePhaseBuilder.Build()
  ↓
graph = NameReservation.ReserveAllNames(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Normalize/NameReservation.cs:32
  ↓
┌────────────────────────────────────────────────┐
│ Step 1: Reserve Type Names                    │
└────────────────────────────────────────────────┘
  ↓
  For each namespace in graph:
    For each type in namespace:
      └─→ Shared.ComputeTypeRequestedBase(type.ClrName)
          Location: src/tsbindgen/SinglePhase/Normalize/Naming/Shared.cs
          ↓
          Applies transforms:
            - Remove backtick and arity (List`1 → List)
            - Replace + with _ (Outer+Inner → Outer_Inner)
            - Sanitize reserved words
          ↓
          ctx.Renamer.ReserveTypeName(stableId, requested, scope, context, source)
          Location: src/tsbindgen/SinglePhase/Renaming/SymbolRenamer.cs
          ↓
          ├─→ Check if name is available in scope
          │   If available: Record decision
          │   If conflict: Add disambiguation suffix
          │
          └─→ Store in _typeDecisions[stableId][scope] = RenameDecision
              RenameDecision contains: Requested, Final, Context, Source
  ↓
┌────────────────────────────────────────────────┐
│ Step 2: Reserve Class Surface Member Names    │
└────────────────────────────────────────────────┘
  ↓
  Reservation.ReserveMemberNamesOnly(ctx, type)
  Location: src/tsbindgen/SinglePhase/Normalize/Naming/Reservation.cs
  ↓
  For each member where EmitScope == EmitScope.ClassSurface:
    ↓
    If member already has rename decision:
      Skip (already renamed by HiddenMemberPlanner, IndexerPlanner, etc.)
    ↓
    Otherwise:
      ├─→ Shared.ComputeMemberRequestedBase(member.ClrName)
      │   Applies transforms:
      │     - Sanitize reserved words (default → default_)
      │     - Apply camelCase if policy enabled
      │
      ├─→ ScopeFactory.ClassSurface(type, member.IsStatic)
      │   Location: src/tsbindgen/SinglePhase/Normalize/Naming/ScopeFactory.cs
      │   Creates scope: namespace/internal/TypeName/instance or static
      │
      └─→ ctx.Renamer.ReserveMemberName(stableId, requested, scope, context, source)
          Location: src/tsbindgen/SinglePhase/Renaming/SymbolRenamer.cs
          ↓
          ├─→ Check static vs instance scope collision
          │   TypeScript rule: static and instance can't share names
          │
          ├─→ Check if name available
          │   If conflict: Add disambiguation suffix
          │
          └─→ Store in _memberDecisions[stableId][scope] = RenameDecision
  ↓
┌────────────────────────────────────────────────┐
│ Step 3: Build Class Surface Name Sets         │
└────────────────────────────────────────────────┘
  ↓
  For each member where EmitScope == EmitScope.ClassSurface:
    ↓
    methodScope = ScopeFactory.ClassSurface(type, method.IsStatic)
    ↓
    ctx.Renamer.TryGetDecision(stableId, methodScope, out decision)
    Location: SymbolRenamer.cs
    ↓
    If found:
      Add decision.Final to classInstanceNames or classStaticNames
      Union into classAllNames set
  ↓
  Purpose: Track what names are used on class surface
           Used for view-vs-class collision detection
  ↓
┌────────────────────────────────────────────────┐
│ Step 4: Reserve View-Scoped Member Names (M5) │
└────────────────────────────────────────────────┘
  ↓
  Reservation.ReserveViewMemberNamesOnly(ctx, graph, type, classAllNames)
  Location: Naming/Reservation.cs
  ↓
  For each member where EmitScope == EmitScope.ViewOnly:
    ↓
    If member.SourceInterface is null:
      Error: ViewOnly member must have SourceInterface
    ↓
    Otherwise:
      ├─→ Shared.ComputeMemberRequestedBase(member.ClrName)
      │   Apply same transforms as class surface
      │
      ├─→ ScopeFactory.ViewScope(type, sourceInterface, member.IsStatic)
      │   Creates scope: namespace/internal/TypeName/view/InterfaceName/instance or static
      │   DIFFERENT scope from class surface!
      │
      ├─→ Check collision with classAllNames
      │   If collision: Add disambiguation suffix
      │   Emit PG_NAME_003 or PG_NAME_004 diagnostic
      │
      └─→ ctx.Renamer.ReserveMemberName(stableId, requested, viewScope, context, source)
          Store in _memberDecisions[stableId][viewScope] = RenameDecision
          Note: Same member can have DIFFERENT names in class scope vs view scope
  ↓
┌────────────────────────────────────────────────┐
│ Step 5: Post-Reservation Audit (fail fast)    │
└────────────────────────────────────────────────┘
  ↓
  Audit.AuditReservationCompleteness(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Normalize/Naming/Audit.cs
  ↓
  For each type:
    For each member where EmitScope != Omit:
      └─→ Compute expected scope
          ↓
          ctx.Renamer.TryGetDecision(stableId, scope, out _)
          ↓
          If NOT found:
            Error: Member lacks rename decision in correct scope
            This is a fatal bug - name reservation is incomplete
  ↓
┌────────────────────────────────────────────────┐
│ Step 6: Apply Names to Graph (pure transform) │
└────────────────────────────────────────────────┘
  ↓
  updatedGraph = Application.ApplyNamesToGraph(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Normalize/Naming/Application.cs
  ↓
  For each type:
    ├─→ ctx.Renamer.GetFinalTypeName(stableId, namespaceScope)
    │   Retrieve reserved type name
    │   Set type.TsEmitName
    │
    └─→ For each member:
        ├─→ Compute correct scope (ClassSurface or ViewScope)
        │
        ├─→ ctx.Renamer.GetFinalMemberName(stableId, scope)
        │   Retrieve reserved member name
        │
        └─→ Set member.TsEmitName
            Now every symbol has TsEmitName populated
  ↓
  Returns: New SymbolGraph with all TsEmitName fields populated
  ↓
  All subsequent phases use TsEmitName for output
```

**Critical Invariants Established:**
1. Every emitted type has TsEmitName set
2. Every emitted member (ClassSurface or ViewOnly) has TsEmitName set
3. Every TsEmitName has corresponding RenameDecision in Renamer
4. View members can have different TsEmitName from class members

## 7. Phase 4: Plan Call Graph

Import planning, emission ordering, overload unification, and validation:

```
SinglePhaseBuilder.Build()
  ↓
plan = PlanPhase(ctx, graph)
  Location: SinglePhaseBuilder.cs:250
  ↓
┌────────────────────────────────────────────────┐
│ Step 1: Build Import Graph                    │
└────────────────────────────────────────────────┘
  ↓
importGraph = ImportGraph.Build(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Plan/ImportGraph.cs
  ↓
  For each namespace:
    For each type:
      For each member signature:
        └─→ Extract foreign type references
            (Types from different namespaces)
            ↓
            Record: SourceNamespace → ForeignNamespace → TypeNames
  ↓
  Returns: ImportGraph with cross-namespace dependencies
  ↓
┌────────────────────────────────────────────────┐
│ Step 2: Plan Imports and Aliases              │
└────────────────────────────────────────────────┘
  ↓
imports = ImportPlanner.PlanImports(ctx, graph, importGraph)
  Location: src/tsbindgen/SinglePhase/Plan/ImportPlanner.cs
  ↓
  For each namespace:
    ├─→ Collect all foreign types needed
    │   From: base types, interfaces, method signatures, property types
    │
    ├─→ Group by source namespace
    │
    ├─→ For each imported type:
    │   └─→ Check for name collision with local types
    │       If collision:
    │         Create alias: TypeName → TypeName_fromNamespace
    │         Store in ImportPlan
    │
    └─→ Build ImportPlan with:
        - Import statements needed
        - Alias mappings
        - Used types per namespace
  ↓
  Returns: ImportPlan
  ↓
┌────────────────────────────────────────────────┐
│ Step 3: Plan Emission Order                   │
└────────────────────────────────────────────────┘
  ↓
orderPlanner = new EmitOrderPlanner(ctx)
order = orderPlanner.PlanOrder(graph)
  Location: src/tsbindgen/SinglePhase/Plan/EmitOrderPlanner.cs
  ↓
  ├─→ Build dependency graph between namespaces
  │   Based on import relationships
  │
  ├─→ Perform topological sort
  │   Ensure dependencies are emitted before dependents
  │
  └─→ Within each namespace, order types:
      1. Interfaces (needed for class implements)
      2. Base classes (needed for derived classes)
      3. Derived classes
      4. Structs
      5. Enums
      6. Delegates
  ↓
  Returns: EmitOrder with stable, deterministic order
  ↓
┌────────────────────────────────────────────────┐
│ Phase 4.5: Overload Unification               │
└────────────────────────────────────────────────┘
  ↓
graph = OverloadUnifier.UnifyOverloads(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Normalize/OverloadUnifier.cs
  ↓
  For each type:
    Group methods by TsEmitName:
      ├─→ If single overload: No changes needed
      │
      └─→ If multiple overloads:
          ├─→ Sort by parameter count (ascending)
          │
          ├─→ Mark last overload as UnifiedImplementation
          │   This is the "umbrella" signature
          │
          ├─→ Mark other overloads as UnifiedDeclaration
          │   These are declaration-only overloads
          │
          └─→ Emit: declarations first, then implementation
              TypeScript requires implementation last
  ↓
  Returns: New SymbolGraph with overload roles assigned
  Purpose: Handle TypeScript function overload syntax
  ↓
┌────────────────────────────────────────────────────────┐
│ Phase 4.6: Interface Constraint Audit                 │
└────────────────────────────────────────────────────────┘
  ↓
constraintFindings = InterfaceConstraintAuditor.Audit(ctx, graph)
  Location: src/tsbindgen/SinglePhase/Plan/InterfaceConstraintAuditor.cs
  ↓
  For each (Type, Interface) pair:
    └─→ Check constructor constraints
        ↓
        If interface requires `new()` but type has no public constructor:
          Record PG_CONSTRAINT_001 finding
        ↓
        If interface requires specific base type but type doesn't inherit:
          Record PG_CONSTRAINT_002 finding
  ↓
  Returns: InterfaceConstraintFindings with all violations
  Purpose: Detect interface implementation violations
  ↓
┌────────────────────────────────────────────────┐
│ Phase 4.7: PhaseGate Validation (20+ checks)  │
└────────────────────────────────────────────────┘
  ↓
PhaseGate.Validate(ctx, graph, imports, constraintFindings)
  Location: src/tsbindgen/SinglePhase/Plan/PhaseGate.cs:24
  ↓
  [See Section 8 for complete PhaseGate call graph]
  ↓
  Returns: void (emits diagnostics to ctx.Diagnostics)
  Throws: If validation errors exceed threshold
  ↓
  If no errors:
    Continue to Emit phase
  If errors:
    Build fails
```

**EmissionPlan Structure:**
```csharp
record EmissionPlan
{
    SymbolGraph Graph;           // Fully validated graph
    ImportPlan Imports;          // Import statements per namespace
    EmitOrder EmissionOrder;     // Stable ordering for emission
}
```

## 8. Phase 4.7: PhaseGate Validation Call Graph

Comprehensive pre-emission validation (20+ validation functions):

```
PhaseGate.Validate(ctx, graph, imports, constraintFindings)
  Location: src/tsbindgen/SinglePhase/Plan/PhaseGate.cs:24
  ↓
validationContext = new ValidationContext
  Tracks: ErrorCount, WarningCount, Diagnostics, DiagnosticCountsByCode
  ↓
┌────────────────────────────────────────────────┐
│ Core Validations (8 functions)                │
└────────────────────────────────────────────────┘
  ↓
ValidationCore.ValidateTypeNames(ctx, graph, validationContext)
  Location: src/tsbindgen/SinglePhase/Plan/Validation/Core.cs
  ↓
  For each type:
    └─→ Check type.TsEmitName is set
        If null: PG_NAME_001 error
        ↓
        Check TsEmitName is valid TypeScript identifier
        If invalid: PG_IDENT_001 error
  ↓
ValidationCore.ValidateMemberNames(ctx, graph, validationContext)
  ↓
  For each member where EmitScope != Omit:
    └─→ Check member.TsEmitName is set
        If null: PG_NAME_001 error
        ↓
        Check TsEmitName is valid TypeScript identifier
        If invalid: PG_IDENT_001 error
  ↓
ValidationCore.ValidateGenericParameters(ctx, graph, validationContext)
  ↓
  For each generic type/method:
    └─→ Check generic parameter names are valid
        Check constraint types exist
        If missing: PG_GEN_001 error
  ↓
ValidationCore.ValidateInterfaceConformance(ctx, graph, validationContext)
  ↓
  For each class implementing interfaces:
    └─→ Check all interface members have implementations
        Either: ClassSurface member OR ViewOnly member
        If missing: PG_INT_001 error
  ↓
ValidationCore.ValidateInheritance(ctx, graph, validationContext)
  ↓
  For each derived class:
    └─→ Check base type exists
        Check override members match base signatures
        If mismatch: PG_INH_001 error
  ↓
ValidationCore.ValidateEmitScopes(ctx, graph, validationContext)
  ↓
  For each member:
    └─→ Check EmitScope is valid
        ClassSurface, ViewOnly, or Omit
        Check EmitScope matches member characteristics
        If invalid: PG_SCOPE_001 error
  ↓
ValidationCore.ValidateImports(ctx, graph, imports, validationContext)
  ↓
  For each import statement:
    └─→ Check imported namespace exists
        Check imported types exist
        If missing: PG_IMPORT_002 error
  ↓
ValidationCore.ValidatePolicyCompliance(ctx, graph, validationContext)
  ↓
  Check all policy rules are followed:
    └─→ Unsafe markers present where required
        Name transforms applied correctly
        Omissions tracked in metadata
        If violation: PG_POLICY_001 error
  ↓
┌────────────────────────────────────────────────┐
│ M1: Identifier Sanitization (Names module)    │
└────────────────────────────────────────────────┘
  ↓
Names.ValidateIdentifiers(ctx, graph, validationContext)
  Location: src/tsbindgen/SinglePhase/Plan/Validation/Names.cs
  ↓
  For each type and member:
    └─→ Check TsEmitName doesn't contain TypeScript reserved words
        Check no special characters (except _, $)
        If invalid: PG_IDENT_002 error
  ↓
┌────────────────────────────────────────────────┐
│ M2: Overload Collision Detection (Names)      │
└────────────────────────────────────────────────┘
  ↓
Names.ValidateOverloadCollisions(ctx, graph, validationContext)
  ↓
  For each method group with same TsEmitName:
    └─→ Check overload signatures are compatible
        Check return types follow TypeScript rules
        If conflict: PG_OVERLOAD_002 error
  ↓
┌────────────────────────────────────────────────┐
│ M3: View Integrity (Views module - 3 rules)   │
└────────────────────────────────────────────────┘
  ↓
Views.ValidateIntegrity(ctx, graph, validationContext)
  Location: src/tsbindgen/SinglePhase/Plan/Validation/Views.cs
  ↓
  Rule 1: ViewOnly members MUST have SourceInterface
    For each ViewOnly member:
      └─→ Check member.SourceInterface != null
          If null: PG_VIEW_001 error (FATAL)
  ↓
  Rule 2: ViewOnly members MUST have ClassSurface twin with same StableId
    For each ViewOnly member:
      └─→ Find ClassSurface member with matching StableId
          If not found: PG_VIEW_002 error (FATAL)
  ↓
  Rule 3: ClassSurface-ViewOnly pairs MUST have same CLR signature
    For each ClassSurface-ViewOnly pair:
      └─→ Compare canonical signatures
          If mismatch: PG_VIEW_003 error (FATAL)
  ↓
┌────────────────────────────────────────────────┐
│ M4: Constraint Findings (Constraints module)  │
└────────────────────────────────────────────────┘
  ↓
Constraints.EmitDiagnostics(ctx, constraintFindings, validationContext)
  Location: src/tsbindgen/SinglePhase/Plan/Validation/Constraints.cs
  ↓
  For each finding in constraintFindings:
    └─→ Emit PG_CONSTRAINT_001 (missing constructor)
        Emit PG_CONSTRAINT_002 (missing base type)
  ↓
┌────────────────────────────────────────────────────┐
│ M5: View Member Scoping (Views - PG_NAME_003/004) │
└────────────────────────────────────────────────────┘
  ↓
Views.ValidateMemberScoping(ctx, graph, validationContext)
  ↓
  For each type:
    ├─→ Build classAllNames set (instance + static)
    │   Same logic as NameReservation Step 3
    │
    └─→ For each ViewOnly member:
        ├─→ Get view scope: ScopeFactory.ViewScope(type, sourceInterface, isStatic)
        │
        ├─→ ctx.Renamer.GetFinalMemberName(stableId, viewScope)
        │   Get final view name
        │
        ├─→ Check collision with classAllNames
        │   If collision:
        │     Static member: PG_NAME_003 error
        │     Instance member: PG_NAME_004 error
        │
        └─→ Check view name != class name for same StableId
            If same: PG_NAME_005 error (missing disambiguation)
  ↓
┌────────────────────────────────────────────────────────┐
│ M5: EmitScope Invariants (Scopes - PG_INT_002/003)    │
└────────────────────────────────────────────────────────┘
  ↓
Scopes.ValidateEmitScopeInvariants(ctx, graph, validationContext)
  Location: src/tsbindgen/SinglePhase/Plan/Validation/Scopes.cs
  ↓
  For each type:
    For each member:
      ├─→ Check EmitScope ∈ {ClassSurface, ViewOnly, Omit}
      │   If invalid: PG_SCOPE_001 error
      │
      ├─→ If EmitScope == ViewOnly:
      │   └─→ Check SourceInterface is set
      │       If null: PG_INT_002 error (FATAL)
      │
      └─→ If EmitScope == ClassSurface:
          └─→ Check SourceInterface is null
              If set: PG_INT_003 error (FATAL)
  ↓
┌────────────────────────────────────────────────────────┐
│ M5: Scope Mismatches (Scopes - PG_SCOPE_003/004)      │
└────────────────────────────────────────────────────────┘
  ↓
Scopes.ValidateScopeMismatches(ctx, graph, validationContext)
  ↓
  For each member where EmitScope != Omit:
    ├─→ Compute expected scope from EmitScope + IsStatic + SourceInterface
    │
    ├─→ Check if Renamer has decision in expected scope
    │   ctx.Renamer.TryGetDecision(stableId, expectedScope, out _)
    │
    └─→ If NOT found:
        ├─→ Check if decision exists in wrong scope
        │   If found in class scope but member is ViewOnly: PG_SCOPE_003 error
        │   If found in view scope but member is ClassSurface: PG_SCOPE_004 error
        │
        └─→ If not found anywhere: PG_NAME_001 error (FATAL)
  ↓
┌────────────────────────────────────────────────────────────┐
│ M5: Class Surface Uniqueness (Names - PG_NAME_005)        │
└────────────────────────────────────────────────────────────┘
  ↓
Names.ValidateClassSurfaceUniqueness(ctx, graph, validationContext)
  ↓
  For each type:
    Group ClassSurface members by (TsEmitName, IsStatic):
      If duplicates found:
        └─→ PG_NAME_005 error (FATAL)
            "Duplicate name on class surface"
            Indicates ClassSurfaceDeduplicator failed
  ↓
┌────────────────────────────────────────────────────────┐
│ M6: Finalization Sweep (PG_FIN_001 through PG_FIN_009) │
└────────────────────────────────────────────────────────┘
  ↓
Finalization.Validate(ctx, graph, validationContext)
  Location: src/tsbindgen/SinglePhase/Plan/Validation/Finalization.cs
  ↓
  For each type:
    ├─→ Check TsEmitName is set: PG_FIN_001
    ├─→ Check Accessibility is set: PG_FIN_002
    ├─→ Check Kind is valid: PG_FIN_003
    │
    └─→ For each member where EmitScope != Omit:
        ├─→ Check TsEmitName is set: PG_FIN_004
        ├─→ Check EmitScope is valid: PG_FIN_005
        ├─→ Check Provenance is set: PG_FIN_006
        ├─→ If ViewOnly: Check SourceInterface is set: PG_FIN_007
        ├─→ Check return type exists: PG_FIN_008
        └─→ Check all parameter types exist: PG_FIN_009
  ↓
┌────────────────────────────────────────────────────────┐
│ M7: Printer Name Consistency (Types - PG_PRINT_001)   │
└────────────────────────────────────────────────────────┘
  ↓
Types.ValidatePrinterNameConsistency(ctx, graph, validationContext)
  Location: src/tsbindgen/SinglePhase/Plan/Validation/Types.cs
  ↓
  For each type:
    For each member signature:
      For each TypeReference in signature:
        └─→ Simulate TypeRefPrinter.Print(typeRef, scope)
            ↓
            └─→ TypeNameResolver.ResolveTypeName(typeRef, scope, ctx.Renamer)
                ↓
                └─→ ctx.Renamer.GetFinalTypeName(stableId, scope)
                    ↓
                    Check result is not null
                    Check result is valid identifier
                    If invalid: PG_PRINT_001 error
  ↓
  Purpose: Validate TypeRefPrinter→Renamer chain works correctly
  ↓
┌────────────────────────────────────────────────────────┐
│ M7a: TypeMap Compliance (Types - PG_TYPEMAP_001)      │
└────────────────────────────────────────────────────────┘
  ↓
Types.ValidateTypeMapCompliance(ctx, graph, validationContext)
  ↓
  For each type:
    For each member signature:
      For each TypeReference:
        └─→ Check TypeReference kind
            ↓
            If PointerTypeReference: PG_TYPEMAP_001 error (UNSUPPORTED)
            If ByRefTypeReference (not param): PG_TYPEMAP_001 error (UNSUPPORTED)
            If FunctionPointerReference: PG_TYPEMAP_001 error (UNSUPPORTED)
  ↓
  Purpose: Detect unsupported CLR types early
  MUST RUN EARLY - before other type validation
  ↓
┌────────────────────────────────────────────────────────────┐
│ M7b: External Type Resolution (Types - PG_LOAD_001)       │
└────────────────────────────────────────────────────────────┘
  ↓
Types.ValidateExternalTypeResolution(ctx, graph, validationContext)
  ↓
  For each type:
    For each member signature:
      For each foreign NamedTypeReference:
        └─→ Check if type exists in graph.TypeIndex
            ↓
            If NOT found:
              └─→ Check if type is built-in (System.Object, etc.)
                  ↓
                  If NOT built-in: PG_LOAD_001 error
                  "External type reference not in closure"
  ↓
  Purpose: Ensure all referenced types were loaded
  MUST RUN AFTER TypeMap, BEFORE API surface validation
  ↓
┌────────────────────────────────────────────────────────────┐
│ M8: Public API Surface (ImportExport - PG_API_001/002)    │
└────────────────────────────────────────────────────────────┘
  ↓
ImportExport.ValidatePublicApiSurface(ctx, graph, imports, validationContext)
  Location: src/tsbindgen/SinglePhase/Plan/Validation/ImportExport.cs
  ↓
  For each public type:
    For each public member:
      For each TypeReference in signature:
        └─→ Check if referenced type is emitted
            ↓
            If type has Accessibility = Internal: PG_API_001 error
            If type has EmitScope = Omit: PG_API_002 error
  ↓
  Purpose: Prevent internal/omitted types leaking into public API
  MUST RUN BEFORE PG_IMPORT_001 - it's more fundamental
  ↓
┌────────────────────────────────────────────────────────────┐
│ M9: Import Completeness (ImportExport - PG_IMPORT_001)    │
└────────────────────────────────────────────────────────────┘
  ↓
ImportExport.ValidateImportCompleteness(ctx, graph, imports, validationContext)
  ↓
  For each namespace:
    ├─→ Collect all foreign types used in signatures
    │   (Types from other namespaces)
    │
    └─→ For each foreign type:
        └─→ Check if imports.HasImport(foreignNamespace, typeName)
            ↓
            If NOT found: PG_IMPORT_001 error
            "Missing import for foreign type"
  ↓
  Purpose: Ensure every foreign type has import statement
  ↓
┌────────────────────────────────────────────────────────────┐
│ M10: Export Completeness (ImportExport - PG_EXPORT_001)   │
└────────────────────────────────────────────────────────────┘
  ↓
ImportExport.ValidateExportCompleteness(ctx, graph, imports, validationContext)
  ↓
  For each namespace:
    For each import statement:
      └─→ Check if source namespace actually exports the type
          ↓
          If NOT exported: PG_EXPORT_001 error
          "Imported type not exported by source"
  ↓
  Purpose: Catch broken import references
  ↓
┌────────────────────────────────────────────────┐
│ Final: Report Results                         │
└────────────────────────────────────────────────┘
  ↓
  Print diagnostic summary table:
    Group by diagnostic code
    Sort by count (descending)
    Show: Code, Count, Description
  ↓
  If ErrorCount > 0:
    ├─→ ctx.Diagnostics.Error(DiagnosticCodes.ValidationFailed, ...)
    │   Show first 20 errors in message
    │
    └─→ Build fails
  ↓
  Context.WriteDiagnosticsFile(ctx, validationContext)
  Location: src/tsbindgen/SinglePhase/Plan/Validation/Context.cs
  ↓
  Write to: .tests/phasegate-diagnostics.txt
    Full list of all diagnostics
    Grouped by code
    With context and locations
  ↓
  Context.WriteSummaryJson(ctx, validationContext)
  ↓
  Write to: .tests/phasegate-summary.json
    JSON summary:
      - DiagnosticCountsByCode
      - ErrorCount, WarningCount, InfoCount
      - SanitizedNameCount
    Used for CI/snapshot comparison
```

**Validation Module Structure:**
- **Core.cs**: 8 fundamental validations
- **Names.cs**: Identifier, collision, uniqueness checks
- **Views.cs**: View integrity (3 hard rules), scoping
- **Scopes.cs**: EmitScope invariants, mismatches
- **Constraints.cs**: Interface constraint violations
- **Finalization.cs**: Comprehensive finalization sweep (9 checks)
- **Types.cs**: TypeMap, external resolution, printer consistency
- **ImportExport.cs**: API surface, import/export completeness

**Total PhaseGate Checks:** 20+ validation functions, 40+ diagnostic codes

## 9. Phase 5: Emit Call Graph

File generation phase - writes TypeScript, metadata, bindings, and stubs:

```
SinglePhaseBuilder.Build()
  ↓
EmitPhase(ctx, plan, outputDirectory)
  Location: SinglePhaseBuilder.cs:285
  ↓
┌────────────────────────────────────────────────────────┐
│ Step 1: Emit Support Types (once per build)           │
└────────────────────────────────────────────────────────┘
  ↓
SupportTypesEmit.Emit(ctx, outputDirectory)
  Location: src/tsbindgen/SinglePhase/Emit/SupportTypesEmitter.cs
  ↓
  Generate: _support/types.d.ts
    Branded numeric types (int, uint, byte, etc.)
    Unsafe marker types (UnsafePointer, UnsafeByRef, etc.)
    Common type aliases
  ↓
  Write to disk
  ↓
┌────────────────────────────────────────────────────────┐
│ Step 2: Emit Internal Index Files (per namespace)     │
└────────────────────────────────────────────────────────┘
  ↓
InternalIndexEmitter.Emit(ctx, plan, outputDirectory)
  Location: src/tsbindgen/SinglePhase/Emit/InternalIndexEmitter.cs
  ↓
  For each namespace in plan.EmissionOrder:
    ↓
    Create StringBuilder for content
    ↓
    ├─→ EmitFileHeader(builder)
    │   Add file-level comments
    │   Add reference to _support/types.d.ts
    │
    ├─→ EmitImports(builder, imports)
    │   Location: InternalIndexEmitter.cs
    │   ↓
    │   For each import in imports.GetImportsFor(namespace):
    │     └─→ Write: import type { TypeA, TypeB } from "../OtherNamespace/internal"
    │
    ├─→ EmitNamespaceDeclaration(builder, namespace)
    │   Write: export namespace NamespaceName {
    │
    ├─→ For each type in plan.EmissionOrder for this namespace:
    │   ↓
    │   └─→ Switch on type.Kind:
    │       ├─→ TypeKind.Class or TypeKind.Struct:
    │       │   └─→ ClassPrinter.PrintClassDeclaration(builder, type, ctx)
    │       │       Location: src/tsbindgen/SinglePhase/Emit/Printers/ClassPrinter.cs
    │       │       ↓
    │       │       ├─→ Write class/interface keyword
    │       │       │   export class TypeName_N<T1, T2>
    │       │       │
    │       │       ├─→ Print generic parameters with constraints
    │       │       │   <T extends BaseType>
    │       │       │
    │       │       ├─→ Print extends clause
    │       │       │   extends BaseClass<T>
    │       │       │
    │       │       ├─→ Print implements clause
    │       │       │   implements IInterface1, IInterface2
    │       │       │
    │       │       ├─→ Open class body: {
    │       │       │
    │       │       ├─→ For each constructor:
    │       │       │   └─→ MethodPrinter.PrintConstructor(builder, ctor, ctx)
    │       │       │       Location: Emit/Printers/MethodPrinter.cs
    │       │       │       Write: constructor(param1: Type1, param2: Type2): void;
    │       │       │
    │       │       ├─→ For each field where EmitScope == ClassSurface:
    │       │       │   └─→ Write field declaration
    │       │       │       readonly FieldName: FieldType;
    │       │       │
    │       │       ├─→ For each property where EmitScope == ClassSurface:
    │       │       │   └─→ Write property declaration
    │       │       │       get PropertyName(): PropertyType;
    │       │       │       set PropertyName(value: PropertyType);
    │       │       │
    │       │       ├─→ For each method where EmitScope == ClassSurface:
    │       │       │   └─→ MethodPrinter.PrintMethod(builder, method, ctx)
    │       │       │       Location: MethodPrinter.cs
    │       │       │       ↓
    │       │       │       ├─→ Print method signature
    │       │       │       │   MethodName<T>(param1: Type1): ReturnType;
    │       │       │       │
    │       │       │       ├─→ If method is overloaded:
    │       │       │       │   └─→ Check method.OverloadRole
    │       │       │       │       If UnifiedDeclaration:
    │       │       │       │         Write declaration only (no body)
    │       │       │       │       If UnifiedImplementation:
    │       │       │       │         Write last (implementation signature)
    │       │       │       │
    │       │       │       ├─→ For each parameter:
    │       │       │       │   └─→ TypeRefPrinter.Print(param.Type, scope)
    │       │       │       │       Location: Emit/Printers/TypeRefPrinter.cs
    │       │       │       │       ↓
    │       │       │       │       └─→ TypeNameResolver.ResolveTypeName(typeRef, scope, ctx.Renamer)
    │       │       │       │           Location: Emit/Printers/TypeNameResolver.cs
    │       │       │       │           ↓
    │       │       │       │           Switch on TypeReference kind:
    │       │       │       │             ├─→ NamedTypeReference:
    │       │       │       │             │   └─→ ctx.Renamer.GetFinalTypeName(stableId, scope)
    │       │       │       │             │       Get reserved name from Renamer
    │       │       │       │             │
    │       │       │       │             ├─→ GenericParameterReference:
    │       │       │       │             │   Return parameter name (T, U, etc.)
    │       │       │       │             │
    │       │       │       │             ├─→ ArrayTypeReference:
    │       │       │       │             │   └─→ ResolveTypeName(elementType) + "[]"
    │       │       │       │             │       Recursive call
    │       │       │       │             │
    │       │       │       │             ├─→ PointerTypeReference:
    │       │       │       │             │   Return "UnsafePointer<T>"
    │       │       │       │             │
    │       │       │       │             └─→ ByRefTypeReference:
    │       │       │       │                 Return "UnsafeByRef<T>"
    │       │       │       │
    │       │       │       └─→ TypeRefPrinter.Print(method.ReturnType, scope)
    │       │       │           Print return type
    │       │       │
    │       │       ├─→ For each event where EmitScope == ClassSurface:
    │       │       │   └─→ Write event declaration
    │       │       │       EventName: EventHandlerType;
    │       │       │
    │       │       ├─→ Close class body: }
    │       │       │
    │       │       └─→ Emit interface views (if any ViewOnly members)
    │       │           ↓
    │       │           Group ViewOnly members by SourceInterface:
    │       │             For each interface:
    │       │               ├─→ Write: export interface TypeName_N_View_InterfaceName {
    │       │               │
    │       │               ├─→ For each ViewOnly member for this interface:
    │       │               │   └─→ Print member using member.TsEmitName
    │       │               │       (May differ from class surface name!)
    │       │               │
    │       │               └─→ Close: }
    │       │
    │       ├─→ TypeKind.Interface:
    │       │   └─→ ClassPrinter.PrintInterfaceDeclaration(builder, type, ctx)
    │       │       Similar to class, but:
    │       │         - Use 'interface' keyword
    │       │         - All members are signatures (no implementation)
    │       │         - No constructors
    │       │
    │       ├─→ TypeKind.Enum:
    │       │   └─→ Write enum declaration
    │       │       export enum EnumName {
    │       │         Member1 = 0,
    │       │         Member2 = 1
    │       │       }
    │       │
    │       ├─→ TypeKind.Delegate:
    │       │   └─→ Write delegate type alias
    │       │       export type DelegateName<T> = (param: T) => ReturnType;
    │       │
    │       └─→ TypeKind.StaticNamespace:
    │           └─→ Write static namespace class
    │               export class StaticNamespace {
    │                 static Method1(): void;
    │                 static Property1: Type1;
    │               }
    │
    ├─→ Close namespace: }
    │
    └─→ Write to disk: namespace/internal/index.d.ts
        File.WriteAllTextAsync(path, builder.ToString())
  ↓
┌────────────────────────────────────────────────────────┐
│ Step 3: Emit Facade Files (per namespace)             │
└────────────────────────────────────────────────────────┘
  ↓
FacadeEmitter.Emit(ctx, plan, outputDirectory)
  Location: src/tsbindgen/SinglePhase/Emit/FacadeEmitter.cs
  ↓
  For each namespace:
    ↓
    Generate: namespace/index.d.ts (public facade)
    ↓
    Content:
      // Re-export all types from internal
      export * from "./internal";

      // Explicit exports for disambiguation
      export type { TypeName1, TypeName2 } from "./internal";
    ↓
    Write to disk
  ↓
┌────────────────────────────────────────────────────────┐
│ Step 4: Emit Metadata Files (per namespace)           │
└────────────────────────────────────────────────────────┘
  ↓
MetadataEmitter.Emit(ctx, plan, outputDirectory)
  Location: src/tsbindgen/SinglePhase/Emit/MetadataEmitter.cs
  ↓
  For each namespace:
    ↓
    Build metadata JSON:
      {
        "namespace": "System.Collections.Generic",
        "types": [
          {
            "clrFullName": "System.Collections.Generic.List`1",
            "tsEmitName": "List_1",
            "kind": "Class",
            "members": {
              "methods": [
                {
                  "clrName": "Add",
                  "tsEmitName": "Add",
                  "signature": "Add(T):void",
                  "isStatic": false,
                  "isVirtual": false,
                  "isOverride": false
                }
              ],
              "properties": [...],
              "fields": [...]
            },
            "omissions": {
              "indexers": [
                {
                  "clrName": "Item",
                  "signature": "get_Item(int):T",
                  "reason": "Duplicate indexer signature"
                }
              ]
            }
          }
        ]
      }
    ↓
    Write to: namespace/metadata.json
  ↓
┌────────────────────────────────────────────────────────┐
│ Step 5: Emit Binding Files (per namespace)            │
└────────────────────────────────────────────────────────┘
  ↓
BindingEmitter.Emit(ctx, plan, outputDirectory)
  Location: src/tsbindgen/SinglePhase/Emit/BindingEmitter.cs
  ↓
  For each namespace:
    ↓
    Build bindings JSON (CLR → TypeScript name mappings):
      {
        "types": {
          "System.Collections.Generic.List`1": "List_1",
          "System.Collections.Generic.Dictionary`2": "Dictionary_2"
        },
        "members": {
          "System.Collections.Generic.List`1": {
            "Add(T):void": "Add",
            "get_Count():int": "Count",
            "get_Item(int):T": "__indexer_0"
          }
        }
      }
    ↓
    Write to: namespace/bindings.json
  ↓
┌────────────────────────────────────────────────────────┐
│ Step 6: Emit Module Stubs (per namespace)             │
└────────────────────────────────────────────────────────┘
  ↓
ModuleStubEmitter.Emit(ctx, plan, outputDirectory)
  Location: src/tsbindgen/SinglePhase/Emit/ModuleStubEmitter.cs
  ↓
  For each namespace:
    ↓
    Generate: namespace/index.js (stub for module resolution)
    ↓
    Content:
      // This file exists for module resolution only
      // Actual types are in index.d.ts
      throw new Error("This is a type-only module");
    ↓
    Write to disk
```

**Key Printer Functions:**
- **ClassPrinter.cs**: Emits class/interface/struct declarations
- **MethodPrinter.cs**: Emits method/constructor signatures
- **TypeRefPrinter.cs**: Converts TypeReference → TypeScript syntax
- **TypeNameResolver.cs**: Resolves type names through Renamer

**Output Files Per Namespace:**
```
namespace/
  ├── internal/
  │   └── index.d.ts        # Full type declarations
  ├── index.d.ts            # Public facade (re-exports internal)
  ├── index.js              # Module stub (throws)
  ├── metadata.json         # CLR metadata for Tsonic compiler
  └── bindings.json         # CLR→TS name mappings
```

**Root Output:**
```
out/
  ├── _support/
  │   └── types.d.ts        # Branded types, unsafe markers
  └── namespace1/
      └── ... (files above)
```

## 10. Cross-Cutting Call Graphs

Functions called across multiple phases:

### 10.1 SymbolRenamer Call Graph

Central naming service - called from everywhere:

```
┌────────────────────────────────────────────┐
│ ReserveTypeName - Called By:               │
└────────────────────────────────────────────┘
  ↓
  1. NameReservation.ReserveAllNames()
     Phase: 3.5 (Name Reservation)
     Purpose: Reserve all type names
  ↓
  2. HiddenMemberPlanner.Plan()
     Phase: 3 (Shape - Pass 9)
     Purpose: Reserve renamed names for hidden members
  ↓
  3. ClassSurfaceDeduplicator.Deduplicate()
     Phase: 3 (Shape - Pass 10.5)
     Purpose: Reserve winner names after deduplication

┌────────────────────────────────────────────┐
│ ReserveMemberName - Called By:             │
└────────────────────────────────────────────┘
  ↓
  1. Reservation.ReserveMemberNamesOnly()
     Phase: 3.5 (Name Reservation - Class Surface)
     Purpose: Reserve class surface member names
  ↓
  2. Reservation.ReserveViewMemberNamesOnly()
     Phase: 3.5 (Name Reservation - Views)
     Purpose: Reserve view-scoped member names
  ↓
  3. IndexerPlanner.Plan()
     Phase: 3 (Shape - Pass 8)
     Purpose: Reserve placeholder names for omitted indexers
  ↓
  4. HiddenMemberPlanner.Plan()
     Phase: 3 (Shape - Pass 9)
     Purpose: Reserve renamed names for hidden members

┌────────────────────────────────────────────┐
│ GetFinalTypeName - Called By:              │
└────────────────────────────────────────────┘
  ↓
  1. Application.ApplyNamesToGraph()
     Phase: 3.5 (Name Reservation)
     Purpose: Apply reserved names to graph
  ↓
  2. TypeNameResolver.ResolveTypeName()
     Phase: 5 (Emit)
     Purpose: Resolve type names for printing
  ↓
  3. PhaseGate validation modules
     Phase: 4.7 (Validation)
     Purpose: Verify name consistency

┌────────────────────────────────────────────┐
│ GetFinalMemberName - Called By:            │
└────────────────────────────────────────────┘
  ↓
  1. Application.ApplyNamesToGraph()
     Phase: 3.5 (Name Reservation)
     Purpose: Apply reserved names to members
  ↓
  2. ClassPrinter.PrintMethod/PrintProperty()
     Phase: 5 (Emit)
     Purpose: Get final name for member emission
  ↓
  3. Views.ValidateMemberScoping()
     Phase: 4.7 (Validation)
     Purpose: Check view member name collisions

┌────────────────────────────────────────────┐
│ TryGetDecision - Called By:                │
└────────────────────────────────────────────┘
  ↓
  1. NameReservation.ReserveAllNames()
     Phase: 3.5 (Name Reservation)
     Purpose: Check if member already has decision
  ↓
  2. Scopes.ValidateScopeMismatches()
     Phase: 4.7 (Validation)
     Purpose: Verify rename decisions exist in correct scopes
  ↓
  3. Audit.AuditReservationCompleteness()
     Phase: 3.5 (Name Reservation)
     Purpose: Verify all emitted members have decisions
```

**SymbolRenamer Data Structures:**
```csharp
// Type decisions: StableId → Scope → RenameDecision
Dictionary<TypeStableId, Dictionary<Scope, RenameDecision>> _typeDecisions;

// Member decisions: StableId → Scope → RenameDecision
Dictionary<MemberStableId, Dictionary<Scope, RenameDecision>> _memberDecisions;

record RenameDecision
{
    string Requested;    // Original requested name
    string Final;        // Final name after disambiguation
    string Context;      // What triggered this reservation
    string Source;       // Which pass reserved this
}
```

### 10.2 DiagnosticBag Call Graph

Error tracking service:

```
┌────────────────────────────────────────────┐
│ Error() - Called By:                       │
└────────────────────────────────────────────┘
  ↓
  1. AssemblyLoader.LoadClosure()
     Phase: 1 (Load)
     Code: PG_LOAD_002, PG_LOAD_003
  ↓
  2. PhaseGate validation modules
     Phase: 4.7 (Validation)
     Codes: PG_* (40+ different codes)
  ↓
  3. BuildContext exception handling
     Phase: Any
     Code: BUILD_EXCEPTION

┌────────────────────────────────────────────┐
│ Warning() - Called By:                     │
└────────────────────────────────────────────┘
  ↓
  1. AssemblyLoader.LoadClosure()
     Phase: 1 (Load)
     Code: PG_LOAD_003 (version drift, non-strict mode)
  ↓
  2. PhaseGate validation modules
     Phase: 4.7 (Validation)
     Codes: Various PG_* codes at Warning severity

┌────────────────────────────────────────────┐
│ Info() - Called By:                        │
└────────────────────────────────────────────┘
  ↓
  1. PhaseGate validation modules
     Phase: 4.7 (Validation)
     Purpose: Informational diagnostics

┌────────────────────────────────────────────┐
│ GetAll() - Called By:                      │
└────────────────────────────────────────────┘
  ↓
  1. SinglePhaseBuilder.Build()
     Phase: End of pipeline
     Purpose: Gather all diagnostics for BuildResult
  ↓
  2. PhaseGate.Validate()
     Phase: 4.7 (Validation)
     Purpose: Check if validation failed

┌────────────────────────────────────────────┐
│ HasErrors() - Called By:                   │
└────────────────────────────────────────────┘
  ↓
  1. SinglePhaseBuilder.Build()
     Phase: End of pipeline
     Purpose: Determine if build succeeded
```

### 10.3 Policy Call Graph

Configuration service:

```
┌────────────────────────────────────────────┐
│ Policy.Emission.MemberNameTransform       │
└────────────────────────────────────────────┘
  ↓
  1. Shared.ComputeMemberRequestedBase()
     Phase: 3.5 (Name Reservation)
     Purpose: Apply camelCase transform if enabled

┌────────────────────────────────────────────┐
│ Policy.Omissions.OmitIndexers             │
└────────────────────────────────────────────┘
  ↓
  1. IndexerPlanner.Plan()
     Phase: 3 (Shape - Pass 8)
     Purpose: Check if indexers should be omitted

┌────────────────────────────────────────────┐
│ Policy.Safety.RequireUnsafeMarkers        │
└────────────────────────────────────────────┘
  ↓
  1. TypeRefPrinter.Print()
     Phase: 5 (Emit)
     Purpose: Use UnsafePointer/UnsafeByRef for unsafe types

┌────────────────────────────────────────────┐
│ Policy.Validation.StrictVersionChecks     │
└────────────────────────────────────────────┘
  ↓
  1. AssemblyLoader.ValidateAssemblyIdentity()
     Phase: 1 (Load)
     Purpose: Error vs Warning for version drift
```

### 10.4 BuildContext.Log Call Graph

Logging service (selective logging):

```
Called from everywhere:
  - AssemblyLoader
  - ReflectionReader
  - GlobalInterfaceIndex
  - StructuralConformance
  - InterfaceInliner
  - ExplicitImplSynthesizer
  - DiamondResolver
  - BaseOverloadAdder
  - StaticSideAnalyzer
  - IndexerPlanner
  - HiddenMemberPlanner
  - FinalIndexersPass
  - ClassSurfaceDeduplicator
  - ConstraintCloser
  - OverloadReturnConflictResolver
  - ViewPlanner
  - MemberDeduplicator
  - NameReservation
  - PhaseGate
  - InternalIndexEmitter
  - FacadeEmitter
  - MetadataEmitter
  - BindingEmitter
  - ModuleStubEmitter

Usage:
  ctx.Log("category", "message")

  Only logs if:
    - verboseLogging == true, OR
    - logCategories.Contains("category")
```

## 11. Complete Call Chain Example

One complete trace from CLI to file write:

```
User runs:
  dotnet run --project src/tsbindgen -- generate --use-new-pipeline -a System.Collections.dll -o out

Main(["generate", "--use-new-pipeline", "-a", "System.Collections.dll", "-o", "out"])
  ↓
RootCommand.InvokeAsync()
  ↓
GenerateCommand.SetHandler() lambda executes
  ↓
GenerateCommand.ExecuteAsync(assemblies: ["System.Collections.dll"], outDir: "out", ...)
  ↓
GenerateCommand.ExecuteNewPipelineAsync(...)
  ↓
SinglePhaseBuilder.Build(
    assemblyPaths: ["System.Collections.dll"],
    outputDirectory: "out",
    policy: PolicyDefaults.Create(),
    logger: Console.WriteLine,
    verbose: false,
    logCategories: null)
  ↓
BuildContext.Create(policy, logger, verbose, logCategories)
  Creates: Renamer, Diagnostics, TypeIndex, StringInterning
  ↓
LoadPhase(ctx, ["System.Collections.dll"])
  ↓
  AssemblyLoader.LoadClosure(["System.Collections.dll"], refPaths, strictVersions: false)
    ↓
    BuildCandidateMap(refPaths)
      Scans directories, finds: System.Private.CoreLib.dll, System.Runtime.dll, etc.
      Returns: candidateMap
    ↓
    ResolveClosure(seedPaths, candidateMap, strictVersions)
      BFS traversal:
        System.Collections.dll references → System.Runtime, System.Private.CoreLib
        System.Runtime references → System.Private.CoreLib
      Returns: resolvedPaths (3 assemblies)
    ↓
    ValidateAssemblyIdentity(resolvedPaths, strictVersions)
      Checks: PKT consistency, version drift
      No errors found
    ↓
    FindCoreLibrary(resolvedPaths)
      Finds: System.Private.CoreLib.dll
    ↓
    new MetadataLoadContext(resolver, "System.Private.CoreLib")
      Loads all 3 assemblies
  ↓
  ReflectionReader.ReadAssemblies(loadContext, allAssemblyPaths)
    ↓
    AssemblyLoader.LoadAssemblies(loadContext, assemblyPaths)
      Loads assemblies into context
    ↓
    For assembly: System.Collections.dll
      For type: System.Collections.Generic.List`1
        ↓
        ReadType(type)
          ↓
          DetermineTypeKind(type) → TypeKind.Class
          ComputeAccessibility(type) → Accessibility.Public
          ↓
          TypeReferenceFactory.CreateGenericParameterSymbol(T)
            Creates: GenericParameterSymbol { Name: "T", Constraints: [] }
          ↓
          TypeReferenceFactory.Create(type.BaseType)
            Creates: NamedTypeReference { FullName: "System.Object" }
          ↓
          TypeReferenceFactory.Create(IList<T>)
            Creates: NamedTypeReference {
              FullName: "System.Collections.Generic.IList`1",
              TypeArguments: [GenericParameterReference { Name: "T" }]
            }
          ↓
          ReadMembers(type)
            ↓
            For method: Add(T item)
              ReadMethod(method, type)
                ↓
                CreateMethodSignature(method)
                  ctx.CanonicalizeMethod("Add", ["T"], "System.Void")
                  Returns: "Add(T):System.Void"
                ↓
                For parameter: item
                  ReadParameter(param)
                    TypeScriptReservedWords.SanitizeParameterName("item") → "item"
                    TypeReferenceFactory.Create(T) → GenericParameterReference { Name: "T" }
                ↓
                Creates: MethodSymbol {
                  StableId: { ... MemberName: "Add", CanonicalSignature: "Add(T):System.Void" },
                  ClrName: "Add",
                  ReturnType: VoidTypeReference,
                  Parameters: [ParameterSymbol { Name: "item", Type: T }],
                  IsStatic: false,
                  Provenance: MemberProvenance.Original,
                  EmitScope: EmitScope.ClassSurface
                }
            ↓
            For property: Count
              ReadProperty(property, type) → PropertySymbol { ... }
            ↓
            Returns: TypeMembers {
              Methods: [Add, Clear, Contains, ...],
              Properties: [Count, Capacity, ...],
              Fields: [],
              Events: [],
              Constructors: [ctor(), ctor(int), ...]
            }
          ↓
          Creates: TypeSymbol {
            StableId: { AssemblyName: "System.Collections", ClrFullName: "System.Collections.Generic.List`1" },
            ClrFullName: "System.Collections.Generic.List`1",
            ClrName: "List`1",
            Namespace: "System.Collections.Generic",
            Kind: TypeKind.Class,
            Arity: 1,
            GenericParameters: [T],
            BaseType: Object,
            Interfaces: [IList<T>, ICollection<T>, IEnumerable<T>, ...],
            Members: { Methods: [...], Properties: [...], ... },
            IsValueType: false
          }
  ↓
  InterfaceMemberSubstitution.SubstituteClosedInterfaces(ctx, graph)
    For List<T> implementing IList<T>:
      BuildSubstitutionMap(IList`1, IList<T>)
        Creates: map { T → GenericParameterReference("T") }
  ↓
  Returns: SymbolGraph {
    Namespaces: [
      NamespaceSymbol {
        Name: "System.Collections.Generic",
        Types: [List_1, Dictionary_2, HashSet_1, ...]
      },
      ...
    ]
  }
  ↓
graph = graph.WithIndices()
  Builds: TypeIndex["System.Collections.Generic.List`1"] = List_1 TypeSymbol
  Builds: NamespaceIndex["System.Collections.Generic"] = NamespaceSymbol
  ↓
ShapePhase(ctx, graph)
  ↓
  GlobalInterfaceIndex.Build(ctx, graph)
    For IList<T>:
      ComputeMethodSignatures(ctx, IList<T>)
        For method Add(T):
          ctx.CanonicalizeMethod("Add", ["T"], "void") → "Add(T):void"
      Stores: _globalIndex["System.Collections.Generic.IList`1"] = InterfaceInfo { ... }
  ↓
  InterfaceDeclIndex.Build(ctx, graph)
    For IList<T>:
      CollectInheritedSignatures(IList<T>)
        Walk base interfaces (ICollection<T>, IEnumerable<T>)
      Compute declared-only signatures
      Stores: _declIndex["System.Collections.Generic.IList`1"] = DeclaredMembers { ... }
  ↓
  StructuralConformance.Analyze(ctx, graph)
    For List<T>:
      For interface IList<T>:
        Check all interface methods have class implementations
        All found → No ViewOnly synthesis needed
    Returns: graph (unchanged for List<T>)
  ↓
  InterfaceInliner.Inline(ctx, graph)
    For List<T>:
      For interface IList<T>:
        For method IList<T>.Add(T):
          Check if List<T> has Add(T) → Yes, found
          No inlining needed
    Returns: graph (unchanged for List<T>)
  ↓
  ExplicitImplSynthesizer.Synthesize(ctx, graph)
    List<T> has no explicit interface implementations
    Returns: graph (unchanged)
  ↓
  DiamondResolver.Resolve(ctx, graph)
    No diamond inheritance in List<T>
    Returns: graph (unchanged)
  ↓
  BaseOverloadAdder.AddOverloads(ctx, graph)
    Check List<T> methods against Object methods
    No base overloads needed
    Returns: graph (unchanged)
  ↓
  StaticSideAnalyzer.Analyze(ctx, graph)
    Check for static/instance name collisions in List<T>
    None found
  ↓
  IndexerPlanner.Plan(ctx, graph)
    For property: List<T>.Item[int index]
      Has IndexParameters.Count > 0 → Is indexer
      Set EmitScope = Omit
      ctx.Renamer.ReserveMemberName(Item.StableId, "__indexer_0", ...)
    Returns: new graph with Item property omitted
  ↓
  HiddenMemberPlanner.Plan(ctx, graph)
    No hidden members in List<T>
  ↓
  FinalIndexersPass.Run(ctx, graph)
    Verify no indexers have EmitScope = ClassSurface
    All clear
    Returns: graph (unchanged)
  ↓
  ClassSurfaceDeduplicator.Deduplicate(ctx, graph)
    Group List<T> members by TsEmitName
    No duplicates found
    Returns: graph (unchanged)
  ↓
  ConstraintCloser.Close(ctx, graph)
    For List<T>: T has no constraints
    Returns: graph (unchanged)
  ↓
  OverloadReturnConflictResolver.Resolve(ctx, graph)
    Check method overloads in List<T>
    No return type conflicts
    Returns: graph (unchanged)
  ↓
  ViewPlanner.Plan(ctx, graph)
    List<T> has no ViewOnly members
    Returns: graph (unchanged)
  ↓
  MemberDeduplicator.Deduplicate(ctx, graph)
    Check for duplicate StableIds in List<T>
    None found
    Returns: graph (unchanged)
  ↓
  Returns: shaped graph
  ↓
NameReservation.ReserveAllNames(ctx, graph)
  ↓
  For namespace: System.Collections.Generic
    For type: List`1
      ↓
      Shared.ComputeTypeRequestedBase("List`1")
        Remove arity: "List`1" → "List"
        Returns: "List"
      ↓
      ctx.Renamer.ReserveTypeName(
        stableId: List`1.StableId,
        requested: "List",
        scope: "System.Collections.Generic/internal",
        context: "TypeDeclaration",
        source: "NameReservation")
        ↓
        Check if "List" available in namespace
        Available → Store: _typeDecisions[List`1.StableId][namespace/internal] =
          RenameDecision { Requested: "List", Final: "List_1" }
      ↓
      Reservation.ReserveMemberNamesOnly(ctx, List<T>)
        For method: Add
          ↓
          Check if already has decision → No
          ↓
          Shared.ComputeMemberRequestedBase("Add")
            No transforms needed
            Returns: "Add"
          ↓
          ScopeFactory.ClassSurface(List<T>, isStatic: false)
            Returns: "System.Collections.Generic/internal/List_1/instance"
          ↓
          ctx.Renamer.ReserveMemberName(
            stableId: Add.StableId,
            requested: "Add",
            scope: "System.Collections.Generic/internal/List_1/instance",
            context: "ClassSurface",
            source: "NameReservation")
            ↓
            Check if "Add" available in instance scope
            Check no collision with static scope
            Available → Store: _memberDecisions[Add.StableId][instance] =
              RenameDecision { Requested: "Add", Final: "Add" }
        ↓
        For property: Count (similar to Add)
        ↓
        For all other members...
      ↓
      Build classAllNames set:
        For each ClassSurface member:
          ctx.Renamer.TryGetDecision(member.StableId, classScope, out decision)
          Add decision.Final to classInstanceNames or classStaticNames
        Union into classAllNames
      ↓
      Reservation.ReserveViewMemberNamesOnly(ctx, graph, List<T>, classAllNames)
        List<T> has no ViewOnly members
        Returns: (0, 0)
  ↓
  Audit.AuditReservationCompleteness(ctx, graph)
    For each member where EmitScope != Omit:
      ctx.Renamer.TryGetDecision(stableId, expectedScope, out _)
      All found → Audit passes
  ↓
  Application.ApplyNamesToGraph(ctx, graph)
    For type: List`1
      ctx.Renamer.GetFinalTypeName(List`1.StableId, namespace/internal)
        Returns: "List_1"
      Set: type.TsEmitName = "List_1"
    ↓
    For method: Add
      ctx.Renamer.GetFinalMemberName(Add.StableId, instance)
        Returns: "Add"
      Set: method.TsEmitName = "Add"
    ↓
    For all other members...
    ↓
    Returns: new graph with all TsEmitName fields populated
  ↓
  Returns: graph with names applied
  ↓
PlanPhase(ctx, graph)
  ↓
  ImportGraph.Build(ctx, graph)
    For List<T>:
      Collect foreign types: System.Object, System.Array, T (generic param)
      Build import graph
    Returns: ImportGraph
  ↓
  ImportPlanner.PlanImports(ctx, graph, importGraph)
    For namespace System.Collections.Generic:
      Import from System: Object, Array
      No alias needed (no collision)
    Returns: ImportPlan
  ↓
  EmitOrderPlanner.PlanOrder(graph)
    Topological sort of namespaces
    Within System.Collections.Generic:
      Order: Interfaces first, then classes
    Returns: EmitOrder
  ↓
  OverloadUnifier.UnifyOverloads(ctx, graph)
    For List<T>.Add (single overload):
      No unification needed
    Returns: graph (unchanged)
  ↓
  InterfaceConstraintAuditor.Audit(ctx, graph)
    For (List<T>, IList<T>) pair:
      Check constructor constraints → Has public ctor()
      Check base constraints → Satisfies all
    Returns: InterfaceConstraintFindings { Findings: [] }
  ↓
  PhaseGate.Validate(ctx, graph, imports, constraintFindings)
    ↓
    [Runs all 20+ validation functions]
    ↓
    ValidationCore.ValidateTypeNames(ctx, graph, validationContext)
      For List<T>:
        Check TsEmitName is set → "List_1" ✓
        Check is valid identifier → Yes ✓
    ↓
    ValidationCore.ValidateMemberNames(ctx, graph, validationContext)
      For Add:
        Check TsEmitName is set → "Add" ✓
        Check is valid identifier → Yes ✓
    ↓
    ... [All other validations pass] ...
    ↓
    ValidationContext: { ErrorCount: 0, WarningCount: 0 }
    ↓
    Context.WriteDiagnosticsFile(ctx, validationContext)
      Writes: .tests/phasegate-diagnostics.txt (empty - no diagnostics)
    ↓
    Context.WriteSummaryJson(ctx, validationContext)
      Writes: .tests/phasegate-summary.json
    ↓
    Returns: void (validation passed)
  ↓
  Returns: EmissionPlan {
    Graph: validated graph,
    Imports: import statements,
    EmissionOrder: stable order
  }
  ↓
EmitPhase(ctx, plan, "out")
  ↓
  SupportTypesEmit.Emit(ctx, "out")
    Writes: out/_support/types.d.ts
    Content:
      export type int = number & { __brand: "int" };
      export type uint = number & { __brand: "uint" };
      ...
  ↓
  InternalIndexEmitter.Emit(ctx, plan, "out")
    ↓
    For namespace: System.Collections.Generic
      ↓
      Create StringBuilder
      ↓
      EmitFileHeader(builder)
        Adds: /// <reference path="../../_support/types.d.ts" />
      ↓
      EmitImports(builder, imports)
        Adds: import type { Object } from "../System/internal";
      ↓
      EmitNamespaceDeclaration(builder, "System.Collections.Generic")
        Adds: export namespace System.Collections.Generic {
      ↓
      For type: List<T>
        ↓
        ClassPrinter.PrintClassDeclaration(builder, List<T>, ctx)
          ↓
          Writes: export class List_1<T>
          ↓
          Writes: extends Object
          ↓
          Writes: implements IList_1<T>, ICollection_1<T>, IEnumerable_1<T> {
          ↓
          For constructor: ctor()
            MethodPrinter.PrintConstructor(builder, ctor, ctx)
            Writes: constructor(): void;
          ↓
          For constructor: ctor(int capacity)
            Writes: constructor(capacity: int): void;
          ↓
          For property: Count
            Writes: get Count(): int;
          ↓
          For method: Add(T item)
            MethodPrinter.PrintMethod(builder, Add, ctx)
            ↓
            Writes: Add
            ↓
            For parameter: item
              TypeRefPrinter.Print(item.Type, scope)
                item.Type is GenericParameterReference { Name: "T" }
                TypeNameResolver.ResolveTypeName(T, scope, ctx.Renamer)
                  Returns: "T" (generic parameter - no renaming)
              Writes: item: T
            ↓
            TypeRefPrinter.Print(method.ReturnType, scope)
              Returns: "void"
            ↓
            Writes: ): void;
          ↓
          For all other members...
          ↓
          Writes: }
      ↓
      For all other types in namespace...
      ↓
      Writes: }
      ↓
      File.WriteAllTextAsync("out/System.Collections.Generic/internal/index.d.ts", builder.ToString())
  ↓
  FacadeEmitter.Emit(ctx, plan, "out")
    For namespace: System.Collections.Generic
      Writes: out/System.Collections.Generic/index.d.ts
      Content:
        export * from "./internal";
        export type { List_1, Dictionary_2, HashSet_1, ... } from "./internal";
  ↓
  MetadataEmitter.Emit(ctx, plan, "out")
    For namespace: System.Collections.Generic
      Build JSON:
        {
          "namespace": "System.Collections.Generic",
          "types": [
            {
              "clrFullName": "System.Collections.Generic.List`1",
              "tsEmitName": "List_1",
              "kind": "Class",
              "members": {
                "methods": [
                  { "clrName": "Add", "tsEmitName": "Add", ... }
                ]
              },
              "omissions": {
                "indexers": [
                  { "clrName": "Item", "reason": "Duplicate indexer" }
                ]
              }
            }
          ]
        }
      Writes: out/System.Collections.Generic/metadata.json
  ↓
  BindingEmitter.Emit(ctx, plan, "out")
    For namespace: System.Collections.Generic
      Build JSON:
        {
          "types": {
            "System.Collections.Generic.List`1": "List_1"
          },
          "members": {
            "System.Collections.Generic.List`1": {
              "Add(T):void": "Add",
              "get_Count():int": "Count",
              "get_Item(int):T": "__indexer_0"
            }
          }
        }
      Writes: out/System.Collections.Generic/bindings.json
  ↓
  ModuleStubEmitter.Emit(ctx, plan, "out")
    For namespace: System.Collections.Generic
      Writes: out/System.Collections.Generic/index.js
      Content:
        throw new Error("This is a type-only module");
  ↓
  Returns: void
  ↓
BuildResult result = new BuildResult {
  Success: true,
  Statistics: { TypeCount: 1, MethodCount: 30, ... },
  Diagnostics: [],
  RenameDecisions: ctx.Renamer.GetAllDecisions()
}
  ↓
GenerateCommand.ExecuteNewPipelineAsync reports success
  Console.WriteLine("✓ Single-phase generation complete")
  Console.WriteLine($"  Output directory: {Path.GetFullPath("out")}")
  Console.WriteLine($"  Types: {result.Statistics.TypeCount}")
  ↓
Program.Main returns 0 (success)
  ↓
Process exits with code 0
```

## Summary

This document provides complete call chains through the SinglePhase pipeline, showing:

1. **Entry Point** - CLI → GenerateCommand → SinglePhaseBuilder
2. **Phase 1: Load** - Assembly loading, reflection, member reading, interface substitution
3. **Phase 2: Normalize** - Index building for fast lookups
4. **Phase 3: Shape** - 14 transformation passes modifying the graph
5. **Phase 3.5: Name Reservation** - Central naming through Renamer (6 steps)
6. **Phase 4: Plan** - Import planning, ordering, overload unification, constraint audit
7. **Phase 4.7: PhaseGate** - 20+ validation functions with 40+ diagnostic codes
8. **Phase 5: Emit** - File generation (TypeScript, metadata, bindings, stubs)
9. **Cross-Cutting** - SymbolRenamer, DiagnosticBag, Policy, Logging
10. **Complete Example** - Full trace from CLI to file write for List<T>

**Key Insights:**
- Shape passes are PURE (return new graph)
- Renamer is MUTATED (accumulates decisions)
- PhaseGate validates before emission (fail-fast)
- Emit phase uses TsEmitName from graph (no further name transformation)
- Complete trace shows data flow through entire pipeline
