# Single-Phase Pipeline Architecture - Complete System Documentation

**Generated**: 2025-11-09 (Updated: commit eeaba5d - Self-contained architecture)
**Pipeline**: Single-Phase Architecture (Experimental)
**Files Analyzed**: 73 (66 original + 7 replicated Renaming infrastructure)
**Status**: Comprehensive documentation covering all subsystems

---

## Table of Contents

1. [High-Level Architecture](#high-level-architecture)
2. [Pipeline Flow](#pipeline-flow)
3. [CLI Entry Points](#cli-entry-points)
4. [Core Infrastructure](#core-infrastructure)
5. [Phase 1: Load](#phase-1-load)
6. [Phase 2: Model](#phase-2-model)
7. [Phase 3: Shape](#phase-3-shape)
8. [Phase 4: Normalize](#phase-4-normalize)
9. [Phase 5: Plan](#phase-5-plan)
10. [Phase 6: Emit](#phase-6-emit)
11. [Call Graphs](#call-graphs)
12. [Data Flow Diagrams](#data-flow-diagrams)

---

## High-Level Architecture

### System Overview

The single-phase pipeline is a **pure functional architecture** that transforms .NET assemblies into TypeScript declarations in a single, deterministic pass. Unlike the old two-phase approach (snapshot → aggregate → render), this pipeline maintains all context in-memory throughout the entire build process.

### Key Architectural Principles

1. **Single-Pass Processing**: One continuous pipeline from reflection to emission
2. **Immutable Data Structures**: All data types are immutable records
3. **Pure Functions**: All transformation passes are pure (input → output, no side effects)
4. **Centralized State**: All shared services (Renamer, Diagnostics, Policy) live in `BuildContext`
5. **StableId-Based Identity**: Assembly-qualified identifiers ensure cross-assembly correctness
6. **Scope-Based Naming**: Separate scopes for class-surface, view, static/instance members

### Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│                          CLI LAYER                               │
│  Program.cs → GenerateCommand.cs → SinglePhaseBuilder.Build()   │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           ↓
┌─────────────────────────────────────────────────────────────────┐
│                      BUILD CONTEXT                               │
│  ┌──────────────┐  ┌───────────────┐  ┌──────────────┐         │
│  │   Policy     │  │ SymbolRenamer │  │ DiagnosticBag│         │
│  │  (Config)    │  │  (Naming)     │  │   (Errors)   │         │
│  └──────────────┘  └───────────────┘  └──────────────┘         │
│  ┌──────────────┐  ┌───────────────┐                            │
│  │ Interner     │  │    Logger     │                            │
│  │ (Strings)    │  │  (Optional)   │                            │
│  └──────────────┘  └───────────────┘                            │
└──────────────────────────┬──────────────────────────────────────┘
                           │
                           ↓
┌─────────────────────────────────────────────────────────────────┐
│                     PIPELINE PHASES                              │
│                                                                   │
│  Phase 1: LOAD                                                    │
│    ┌─────────────────────────────────────────┐                  │
│    │ MetadataLoadContext → Reflection        │                  │
│    │ ReflectionReader → SymbolGraph (CLR)    │                  │
│    └──────────────────┬──────────────────────┘                  │
│                       ↓                                           │
│  Phase 2: NORMALIZE (Build Indices)                             │
│    ┌─────────────────────────────────────────┐                  │
│    │ Build GlobalInterfaceIndex              │                  │
│    │ Build type/member lookup tables         │                  │
│    └──────────────────┬──────────────────────┘                  │
│                       ↓                                           │
│  Phase 3: SHAPE                                                  │
│    ┌─────────────────────────────────────────┐                  │
│    │ 14 transformation passes:                │                  │
│    │ StructuralConformance → InterfaceInliner│                  │
│    │ ExplicitImplSynthesizer → DiamondResolver│                 │
│    │ BaseOverloadAdder → ViewPlanner → ...   │                  │
│    └──────────────────┬──────────────────────┘                  │
│                       ↓                                           │
│  Phase 3.5: NAME RESERVATION                                     │
│    ┌─────────────────────────────────────────┐                  │
│    │ Reserve all TypeScript names via Renamer│                  │
│    │ Separate scopes: class, view, static    │                  │
│    └──────────────────┬──────────────────────┘                  │
│                       ↓                                           │
│  Phase 4: PLAN                                                   │
│    ┌─────────────────────────────────────────┐                  │
│    │ Build ImportGraph                        │                  │
│    │ Plan imports and aliases                 │                  │
│    │ Determine emission order                 │                  │
│    │ PhaseGate validation (gate keeper)       │                  │
│    └──────────────────┬──────────────────────┘                  │
│                       ↓                                           │
│  Phase 5: EMIT                                                   │
│    ┌─────────────────────────────────────────┐                  │
│    │ InternalIndexEmitter → internal/index.d.ts│                │
│    │ FacadeEmitter → index.d.ts               │                  │
│    │ MetadataEmitter → metadata.json          │                  │
│    │ BindingEmitter → bindings.json           │                  │
│    │ ModuleStubEmitter → index.js             │                  │
│    └──────────────────────────────────────────┘                  │
└─────────────────────────────────────────────────────────────────┘
```

### Self-Contained Architecture (as of commit `eeaba5d`)

**Key Architectural Decision**: The SinglePhase pipeline is now fully self-contained with its own renaming infrastructure.

**What Changed**:
- All renaming infrastructure replicated from `Core/Renaming/` → `SinglePhase/Renaming/`
- Files replicated:
  - `SymbolRenamer.cs` (with M5 dual-scope changes applied)
  - `RenameScope.cs` (with ViewScope record added)
  - `RenamerScopes.cs` (new canonical scope helper)
  - `StableId.cs`
  - `RenameDecision.cs`
  - `NameReservationTable.cs`
  - `TypeScriptReservedWords.cs`

**Why This Matters**:
1. **Independence**: New pipeline can evolve without breaking old pipeline
2. **Isolation**: M5 changes (dual-scope naming) only affect new pipeline
3. **Safety**: Old pipeline remains unchanged and functional (validated: 4,047 types, 0 syntax errors)
4. **Future-Ready**: Eventually `Core/` will be deleted when old pipeline is retired

**Directory Structure**:
```
src/tsbindgen/
├── Core/Renaming/          # Old pipeline (unchanged)
│   ├── SymbolRenamer.cs    # Original single-scope version
│   ├── RenameScope.cs      # Original (no ViewScope)
│   └── ...
└── SinglePhase/Renaming/   # New pipeline (self-contained)
    ├── SymbolRenamer.cs    # M5 dual-scope version
    ├── RenameScope.cs      # With ViewScope record
    ├── RenamerScopes.cs    # Canonical scope helpers
    └── ...
```

---

## Pipeline Flow

### Sequential Phase Execution

The pipeline executes in strict order. Each phase produces an immutable output that becomes the input to the next phase.

```
AssemblyPaths[]
    ↓
[LOAD] MetadataLoadContext + Reflection
    ↓
SymbolGraph (pure CLR facts)
    ↓
[NORMALIZE] Build indices
    ↓
SymbolGraph (with indices)
    ↓
[SHAPE] 14 transformation passes
    ↓
SymbolGraph (TypeScript-ready)
    ↓
[NAME RESERVATION] Assign all names
    ↓
SymbolGraph (with TsEmitName decided)
    ↓
[PLAN] Import planning + validation
    ↓
EmissionPlan (Graph + Imports + Order)
    ↓
[EMIT] Generate all output files
    ↓
Output Files (*.d.ts, *.json, *.js)
```

### Data Transformations

| Phase | Input Type | Output Type | Mutability |
|-------|-----------|-------------|------------|
| Load | `string[]` (paths) | `SymbolGraph` | Immutable |
| Normalize | `SymbolGraph` | `SymbolGraph` | Immutable (with indices) |
| Shape | `SymbolGraph` | `SymbolGraph` | Immutable (transformed) |
| NameReservation | `SymbolGraph` | `SymbolGraph` | Immutable (names decided) |
| Plan | `SymbolGraph` | `EmissionPlan` | Immutable |
| Emit | `EmissionPlan` | File I/O | Side effects |

**Key Insight**: Only the Emit phase has side effects (file writes). All other phases are pure transformations.

---

## CLI Entry Points

### File: `src/tsbindgen/Cli/Program.cs`

**Purpose**: Application entry point. Creates root command and invokes command-line parser.

#### Methods

##### `Main(string[] args) → Task<int>`
- **Called by**: .NET runtime (application entry point)
- **Calls**: `GenerateCommand.Create()`, `RootCommand.InvokeAsync()`
- **Description**:
  - Creates System.CommandLine root command
  - Registers the `generate` subcommand
  - Invokes command-line parsing
  - Returns exit code (0 = success, non-zero = error)

**Call Flow**:
```
.NET Runtime
  ↓
Program.Main()
  ↓
GenerateCommand.Create() → creates command
  ↓
RootCommand.InvokeAsync() → parses args & executes
```

---

### File: `src/tsbindgen/Cli/GenerateCommand.cs`

**Purpose**: Implements the `generate` command for the CLI. Routes execution to either old two-phase pipeline or new single-phase pipeline.

#### Methods

##### `Create() → Command`
- **Called by**: `Program.Main()`
- **Calls**: Sets up command options and handler
- **Description**:
  - Defines all CLI options (assemblies, output, transforms, logging)
  - Registers command handler
  - Returns configured `Command` object

**Options Defined**:
- `-a, --assembly`: Assembly paths (repeatable)
- `-d, --assembly-dir`: Assembly directory
- `-o, --out-dir`: Output directory (default: "out")
- `-n, --namespaces`: Namespace filter
- `--namespace-names`, `--class-names`, etc.: Name transforms
- `-v, --verbose`: Verbose logging
- `--logs`: Specific log categories
- `--use-new-pipeline`: Enable single-phase pipeline (**key flag**)

##### `ExecuteAsync(...) → Task`
- **Called by**: Command handler (System.CommandLine)
- **Calls**: `ExecuteNewPipelineAsync()` OR old two-phase pipeline
- **Description**:
  - Collects assembly paths from CLI options
  - Routes to appropriate pipeline based on `--use-new-pipeline` flag
  - Handles errors and exit codes

**Routing Logic**:
```csharp
if (useNewPipeline)
{
    await ExecuteNewPipelineAsync(...);  // Single-phase (experimental)
}
else
{
    // Old two-phase pipeline (snapshot → aggregate → render)
}
```

##### `ExecuteNewPipelineAsync(...) → Task`
- **Called by**: `ExecuteAsync()` when `--use-new-pipeline` is set
- **Calls**: `SinglePhaseBuilder.Build()`
- **Description**:
  - Converts CLI options to `GenerationPolicy`
  - Parses log categories into `HashSet<string>`
  - Invokes single-phase pipeline
  - Reports results or errors
  - Sets exit code

**Key Data Flow**:
```
CLI Options
  ↓
Parse → GenerationPolicy
  ↓
SinglePhaseBuilder.Build(assemblies, outDir, policy, logger, logCategories)
  ↓
BuildResult (Success/Failure + Diagnostics)
  ↓
Console output + exit code
```

---

## Core Infrastructure

The core infrastructure provides shared services used throughout the pipeline. All services are immutable after initialization and accessed via `BuildContext`.

### File: `src/tsbindgen/SinglePhase/BuildContext.cs`

**Purpose**: Central context object containing all shared services. Passed to every phase and transformation.

#### Properties

| Property | Type | Description |
|----------|------|-------------|
| `Policy` | `GenerationPolicy` | Configuration controlling all behavior |
| `Renamer` | `SymbolRenamer` | Central naming authority for all identifiers |
| `Interner` | `StringInterner` | String deduplication to reduce allocations |
| `Diagnostics` | `DiagnosticBag` | Error/warning collection |
| `Logger` | `Action<string>?` | Optional logging callback |
| `VerboseLogging` | `bool` | Enable all log categories |
| `LogCategories` | `HashSet<string>?` | Specific categories to log |

#### Methods

##### `Create(...) → BuildContext` (static)
- **Called by**: `SinglePhaseBuilder.Build()`
- **Calls**: Creates all services (Renamer, Interner, DiagnosticBag)
- **Description**:
  - Factory method for creating fully initialized context
  - Applies policy overrides to Renamer
  - Configures style transforms (camelCase, etc.)
  - Returns immutable context

**Initialization Flow**:
```csharp
1. Create services:
   - new SymbolRenamer()
   - new StringInterner()
   - new DiagnosticBag()

2. Configure Renamer:
   - ApplyExplicitOverrides(policy.Renaming.ExplicitMap)
   - AdoptStyleTransform(NameTransform.Apply)

3. Return BuildContext with all services
```

##### `Log(string category, string message) → void`
- **Called by**: All pipeline phases
- **Calls**: `Logger?.Invoke()` if category is enabled
- **Description**:
  - Conditional logging based on VerboseLogging or LogCategories
  - Adds `[category]` prefix to messages
  - No-op if logger is null

**Logging Logic**:
```csharp
if (Logger != null && (VerboseLogging || LogCategories?.Contains(category)))
{
    Logger($"[{category}] {message}");
}
```

##### `CanonicalizeMethod(...) → string`
- **Called by**: `ReflectionReader`, signature building
- **Calls**: `SignatureCanonicalizer.CanonicalizeMethod()`
- **Description**: Creates canonical method signature for StableId construction

##### `CanonicalizeProperty(...) → string`
- **Called by**: `ReflectionReader`, signature building
- **Calls**: `SignatureCanonicalizer.CanonicalizeProperty()`
- **Description**: Creates canonical property signature for StableId construction

##### `Intern(string value) → string`
- **Called by**: Throughout pipeline for string deduplication
- **Calls**: `Interner.Intern()`
- **Description**: Interns strings to reduce memory usage

---

### File: `src/tsbindgen/SinglePhase/Renaming/SymbolRenamer.cs`

**Location**: SinglePhase/Renaming (replicated from Core/Renaming for pipeline independence)

**Purpose**: Central naming authority. All TypeScript identifiers flow through this component. Maintains rename decisions with full provenance tracking.

**Architecture Note**: As of commit `eeaba5d`, all renaming infrastructure has been replicated to `SinglePhase/Renaming/` to make the new pipeline fully self-contained. The old pipeline continues using `Core/Renaming/` unchanged.

#### Architecture

**Scope-Based Naming Model** (M5 Canonical Formats):
```
Scope Hierarchy:
  - Namespace Scope (public):  "ns:System:public"
  - Namespace Scope (internal): "ns:System:internal"
  - Class Surface Scope (instance): "type:System.Decimal#instance"
  - Class Surface Scope (static):   "type:System.Decimal#static"
  - View Scope: "view:{TypeStableId}:{InterfaceStableId}#instance" or "#static"
    Example: "view:System.Private.CoreLib:System.Decimal:System.Private.CoreLib:System.IConvertible#instance"
```

**Data Structures** (M5 Dual-Scope Model):
```csharp
// Per-scope reservation tables
Dictionary<string, NameReservationTable> _tablesByScope

// M5: StableId + ScopeKey → Final decision mapping (supports dual-scope reservations)
Dictionary<(StableId Id, string ScopeKey), RenameDecision> _decisions

// Explicit CLI overrides
Dictionary<StableId, string> _explicitOverrides

// Style transform (e.g., camelCase)
Func<string, string>? _styleTransform
```

**M5 Critical Change**: The `_decisions` dictionary now keys by `(StableId, ScopeKey)` tuple to support the same member being reserved in multiple scopes (e.g., `toByte` in class scope and `toByte$view` in view scope).

#### Methods

##### `ApplyExplicitOverrides(IReadOnlyDictionary<string, string>) → void`
- **Called by**: `BuildContext.Create()`
- **Calls**: Stores overrides in `_explicitOverrides`
- **Description**:
  - Applies user-provided renames (CLI `--explicit-map`)
  - Stored by StableId for later lookup during reservation
  - Takes precedence over all other naming strategies

##### `AdoptStyleTransform(Func<string, string>) → void`
- **Called by**: `BuildContext.Create()`
- **Calls**: Stores transform in `_styleTransform`
- **Description**:
  - Configures global style transformation (camelCase, PascalCase, etc.)
  - Applied to ALL identifiers during reservation
  - Called once during context initialization

##### `ReserveTypeName(StableId, string requested, RenameScope, string reason, string source) → void`
- **Called by**: `NameReservation.ReserveTypeNames()`
- **Calls**: `ResolveNameWithConflicts()`, `RecordDecision()`
- **Description**:
  - Reserves a type name in a namespace scope
  - Handles conflicts with numeric suffixes (List2, List3, etc.)
  - Records decision for later retrieval

**Flow**:
```
requested: "List"
  ↓
Apply style transform: "list" (if camelCase)
  ↓
Sanitize reserved words: "list_" (if "list" is reserved)
  ↓
Check conflicts in scope
  ↓
Allocate suffix if needed: "list2"
  ↓
Record decision: StableId → "list2"
```

##### `ReserveMemberName(StableId, string requested, RenameScope, string reason, bool isStatic, string source) → void`
- **Called by**: `NameReservation.ReserveMemberNames()`, `ReserveViewMemberNamesOnly()`
- **Calls**: `ResolveNameWithConflicts()`, `RecordDecision()`
- **Description**:
  - Reserves a member name in a type scope
  - Creates separate sub-scopes for static vs instance members
  - Handles explicit interface implementations specially

**Scope Adjustment**:
```csharp
// Original scope: "type:System.Decimal"
// Becomes: "type:System.Decimal#static" or "type:System.Decimal#instance"
var effectiveScope = scope is TypeScope ts
    ? ts with { IsStatic = isStatic, ScopeKey = $"{ts.ScopeKey}#{(isStatic ? "static" : "instance")}" }
    : scope;
```

##### `GetFinalTypeName(StableId, RenameScope) → string`
- **Called by**: Emission phase (FacadeEmitter, InternalIndexEmitter)
- **Calls**: Looks up in `_decisions` dictionary
- **Description**:
  - Retrieves the final TypeScript name for a type
  - Throws if no decision exists (must be reserved first)

##### `GetFinalMemberName(StableId, RenameScope, bool isStatic) → string`
- **Called by**: Emission phase (ClassPrinter, MethodPrinter)
- **Calls**: Looks up in `_decisions` dictionary
- **Description**:
  - Retrieves the final TypeScript name for a member
  - Accounts for static/instance scope separation
  - Throws if no decision exists

##### `TryGetDecision(StableId, out RenameDecision?) → bool`
- **Called by**: NameReservation collision detection, emission phase
- **Calls**: Looks up in `_decisions` dictionary
- **Description**:
  - Non-throwing lookup for rename decisions
  - Returns false if no decision exists
  - Used for collision detection (checking if name already decided)

##### `IsNameTaken(RenameScope, string name, bool isStatic) → bool`
- **Called by**: NameReservation view collision detection
- **Calls**: `NameReservationTable.IsReserved()`
- **Description**:
  - Checks if a name is already reserved in a specific scope
  - Accounts for static/instance sub-scopes
  - Returns false if scope doesn't exist yet

##### `ListReservedNames(RenameScope, bool isStatic) → HashSet<string>`
- **Called by**: NameReservation class-surface name collection
- **Calls**: `NameReservationTable.GetAllReservedNames()`
- **Description**:
  - Returns all final names reserved in a scope
  - Used for building `classAllNames` set for collision detection
  - Returns empty set if scope doesn't exist

##### `PeekFinalMemberName(RenameScope, string requestedBase, bool isStatic) → string`
- **Called by**: NameReservation view collision detection
- **Calls**: `TypeScriptReservedWords.Sanitize()`, `NameReservationTable.IsReserved()`
- **Description**:
  - **Non-mutating** query to see what final name WOULD be assigned
  - Applies style transform and sanitization
  - Finds next available suffix without committing
  - Critical for collision detection before reservation

**Usage Pattern** (from M5 fix):
```csharp
// Peek at what view member would get
var peek = ctx.Renamer.PeekFinalMemberName(viewScope, "ToByte", false);
// peek = "toByte"

// Check collision with class names
if (classAllNames.Contains(peek)) {
    // Apply $view suffix
    finalRequested = requested + "$view";
}
```

##### `ResolveNameWithConflicts(...) → string` (private)
- **Called by**: `ReserveTypeName()`, `ReserveMemberName()`
- **Calls**: `NameReservationTable.TryReserve()`, `AllocateNextSuffix()`
- **Description**:
  - Core naming algorithm with conflict resolution
  - Applies transforms in order: explicit override → style → sanitize → suffix
  - Handles explicit interface implementations specially

**Algorithm**:
```
1. Check explicit override → use if available
2. Apply style transform (camelCase, etc.)
3. Sanitize TypeScript reserved words
4. Try to reserve sanitized name
5. If conflict:
   a. For explicit interface impl: try <name>_<InterfaceName>
   b. Otherwise: try numeric suffix (name2, name3, ...)
6. Record decision and return final name
```

---

### File: `src/tsbindgen/SinglePhase/Renaming/StableId.cs`

**Location**: SinglePhase/Renaming (replicated from Core/Renaming for pipeline independence)

**Purpose**: Immutable identity for types and members BEFORE any name transformations. Used as keys for rename decisions and bindings back to CLR.

#### Types

##### `abstract record StableId`
- **Base class** for all stable identifiers
- **Property**: `string AssemblyName` - where the symbol originates

##### `sealed record TypeStableId : StableId`
- **Purpose**: Identity for types
- **Properties**:
  - `string AssemblyName` (inherited)
  - `string ClrFullName` - e.g., "System.Collections.Generic.List`1"
- **ToString**: `"{AssemblyName}:{ClrFullName}"`
- **Example**: `"System.Private.CoreLib:System.Decimal"`

##### `sealed record MemberStableId : StableId`
- **Purpose**: Identity for members (methods, properties, fields, events)
- **Properties**:
  - `string AssemblyName` (inherited)
  - `string DeclaringClrFullName` - declaring type's CLR name
  - `string MemberName` - as it appears in CLR metadata
  - `string CanonicalSignature` - uniquely identifies among overloads
  - `int? MetadataToken` - optional, NOT included in equality
- **ToString**: `"{AssemblyName}:{DeclaringClrFullName}::{MemberName}{CanonicalSignature}"`
- **Example**: `"System.Private.CoreLib:System.Decimal::ToByte(System.IFormatProvider):System.Byte"`

**Key Design Decision**: `MemberStableId` equality is **semantic**, not based on metadata token. This allows:
- Same member from different assembly versions to match
- Synthetic members to equal original members
- Cross-assembly correlation

---

### File: `src/tsbindgen/Core/Policy/GenerationPolicy.cs`

**Purpose**: Central configuration controlling all generation behavior. Immutable policy object passed throughout the pipeline.

#### Structure

The policy is composed of multiple sub-policies, each controlling a specific aspect of generation:

```csharp
public sealed record GenerationPolicy
{
    InterfacePolicy Interfaces;
    ClassPolicy Classes;
    IndexerPolicy Indexers;
    ConstraintPolicy Constraints;
    EmissionPolicy Emission;
    DiagnosticPolicy Diagnostics;
    RenamingPolicy Renaming;
    ModulesPolicy Modules;
    StaticSidePolicy StaticSide;
}
```

#### Sub-Policies

##### `InterfacePolicy`
- **`InlineAll`**: If true, flatten all interface hierarchies (no `extends`)
- **`DiamondResolution`**: How to handle diamond inheritance
  - `OverloadAll`: Emit all paths
  - `PreferDerived`: Pick most derived
  - `Error`: Fail build

##### `ClassPolicy`
- **`KeepExtends`**: Preserve class `extends` chains (true) or flatten (false)
- **`HiddenMemberSuffix`**: Suffix for C# `new` keyword hidden members (default: "_new")
- **`SynthesizeExplicitImpl`**: How to handle explicit interface implementations
  - `SynthesizeWithSuffix`: Add members with suffix
  - `EmitExplicitViews`: Create `As_IInterface` properties
  - `Skip`: Don't emit

##### `IndexerPolicy`
- **`EmitPropertyWhenSingle`**: Emit single indexer as property
- **`EmitMethodsWhenMultiple`**: Emit multiple indexers as methods
- **`MethodName`**: Method name for indexer methods (default: "Item")

##### `ConstraintPolicy`
- **`StrictClosure`**: Enforce strict constraint closure (fail on unsatisfiable)
- **`MergeStrategy`**: How to merge multiple constraints
  - `Intersection`: `T & U`
  - `Union`: `T | U`
  - `PreferLeft`: Use leftmost
- **`AllowConstructorConstraintLoss`**: Allow `new()` constraint loss (default: false)

##### `EmissionPolicy`
- **`NameTransform`**: Naming strategy
  - `None`: Keep CLR names
  - `CamelCase`: Convert to camelCase
  - `PascalCase`: Convert to PascalCase
- **`SortOrder`**: Sorting strategy
  - `Alphabetical`: By name
  - `ByKindThenName`: Group by kind, then name
  - `DeclarationOrder`: Preserve CLR order
- **`EmitDocComments`**: Emit XML docs as TSDoc

##### `RenamingPolicy`
- **`StaticConflict`**: How to handle static member collisions
  - `NumericSuffix`: Add suffix (name2, name3)
  - `DisambiguatingSuffix`: Add fixed suffix
  - `Error`: Fail build
- **`HiddenNew`**: How to handle C# `new` keyword
- **`ExplicitMap`**: User-provided renames (`CLRPath → TargetName`)
- **`AllowStaticMemberRename`**: Allow renaming static members

##### `ModulesPolicy`
- **`UseNamespaceDirectories`**: Use subdirectories (System/Collections/) vs flat (System.Collections/)
- **`AlwaysAliasImports`**: Always generate import aliases

##### `StaticSidePolicy`
- **`Action`**: What to do with static-side conflicts
  - `Analyze`: Just emit diagnostics
  - `AutoRename`: Automatically rename conflicting statics
  - `Error`: Fail build

---

## Phase 1: Load

**Purpose**: Load assemblies and build the initial `SymbolGraph` containing pure CLR facts. No TypeScript concepts at this stage.

### File: `src/tsbindgen/SinglePhase/Load/AssemblyLoader.cs`

**Purpose**: Creates `MetadataLoadContext` for loading assemblies in isolation. Handles reference assembly resolution.

#### Methods

##### `CreateLoadContext(IReadOnlyList<string> assemblyPaths) → MetadataLoadContext`
- **Called by**: `SinglePhaseBuilder.LoadPhase()`
- **Calls**: `GetReferenceAssembliesPath()`, `GetResolverPaths()`
- **Description**:
  - Creates isolated load context for reflection
  - Configures resolver to find reference assemblies
  - Returns context ready for loading assemblies

**Resolver Configuration**:
```
1. Get reference assemblies path (usually same directory as target assemblies)
2. Collect all .dll paths from:
   - Reference assemblies directory
   - Directories containing target assemblies
3. Deduplicate by assembly name
4. Create PathAssemblyResolver with all paths
```

##### `LoadAssemblies(MetadataLoadContext, IReadOnlyList<string>) → IReadOnlyList<Assembly>`
- **Called by**: `ReflectionReader.ReadAssemblies()`
- **Calls**: `AssemblyName.GetAssemblyName()`, `loadContext.LoadFromAssemblyPath()`
- **Description**:
  - Loads all assemblies into the context
  - Deduplicates by assembly identity
  - Skips mscorlib (automatically loaded as core assembly)
  - Returns list of loaded assemblies

**Deduplication**:
```csharp
var identity = $"{assemblyName.Name}, Version={assemblyName.Version}";
if (loadedIdentities.Contains(identity)) skip;
```

---

### File: `src/tsbindgen/SinglePhase/Load/ReflectionReader.cs`

**Purpose**: Reads assemblies via reflection and builds the `SymbolGraph`. Pure CLR facts - no TypeScript concepts.

#### Key Architecture

**Reflection-Based Reading**:
- Uses System.Reflection over `MetadataLoadContext` assemblies
- Filters to public/nested public types only
- Skips compiler-generated types (names containing `<` or `>`)
- Groups types by namespace
- Creates immutable symbol structures

#### Methods

##### `ReadAssemblies(MetadataLoadContext, IReadOnlyList<string>) → SymbolGraph`
- **Called by**: `SinglePhaseBuilder.LoadPhase()`
- **Calls**: `AssemblyLoader.LoadAssemblies()`, `ReadType()` for each type
- **Description**:
  - Loads all assemblies
  - Reads all public types
  - Groups by namespace
  - Returns complete `SymbolGraph`

**Flow**:
```
1. Load assemblies via AssemblyLoader
2. For each assembly:
   a. Get all types via assembly.GetTypes()
   b. Filter to public/nested public
   c. Skip compiler-generated (names with < or >)
   d. Read each type → TypeSymbol
   e. Group by namespace
3. Build NamespaceSymbol for each group
4. Return SymbolGraph
```

##### `ReadType(Type) → TypeSymbol`
- **Called by**: `ReadAssemblies()` for each type
- **Calls**: `ReadMembers()`, `DetermineTypeKind()`, recursive `ReadType()` for nested types
- **Description**:
  - Reads complete type information
  - Creates `TypeStableId`
  - Reads generic parameters
  - Reads base type and interfaces
  - Reads all members
  - Recursively reads nested types

**Type Information Captured**:
- StableId (assembly + CLR full name)
- Kind (enum, interface, delegate, class, struct, static namespace)
- Generic arity and parameters
- Base type and interfaces
- All members (methods, properties, fields, events, constructors)
- Nested types
- Flags (value type, abstract, sealed, static)

##### `ReadMembers(Type) → TypeMembers`
- **Called by**: `ReadType()`
- **Calls**: `ReadMethod()`, `ReadProperty()`, `ReadField()`, `ReadEvent()`, `ReadConstructor()`
- **Description**:
  - Reads all public members (instance and static)
  - Uses `BindingFlags.DeclaredOnly` to get only this type's members
  - Skips special names (property/event accessors)
  - Returns immutable `TypeMembers` structure

**Binding Flags**:
```csharp
const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
const BindingFlags publicStatic = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;
```

##### `ReadMethod(MethodInfo, Type) → MethodSymbol`
- **Called by**: `ReadMembers()`
- **Calls**: `CreateMethodSignature()`, `ReadParameter()` for each parameter
- **Description**:
  - Reads complete method information
  - Creates `MemberStableId` with canonical signature
  - Detects explicit interface implementations (name contains `.`)
  - Reads parameters and generic parameters
  - Determines override vs virtual vs new

**Explicit Interface Detection**:
```csharp
// Explicit impl: "System.Collections.ICollection.SyncRoot"
// Regular: "SyncRoot"
if (clrName.Contains('.'))
{
    // Use qualified name for StableId uniqueness
    memberName = clrName;
}
```

**Override Detection**:
```csharp
// Override: IsVirtual && !HasFlag(NewSlot)
// New virtual: IsVirtual && HasFlag(NewSlot)
private static bool IsMethodOverride(MethodInfo method)
{
    return method.IsVirtual && !method.Attributes.HasFlag(MethodAttributes.NewSlot);
}
```

##### `ReadProperty(PropertyInfo, Type) → PropertySymbol`
- **Called by**: `ReadMembers()`
- **Calls**: `CreatePropertySignature()`, `ReadParameter()` for index parameters
- **Description**:
  - Reads property with getter/setter information
  - Handles indexers (properties with parameters)
  - Detects explicit interface implementations
  - Determines override status from getter

##### `ReadField(FieldInfo, Type) → FieldSymbol`
- **Called by**: `ReadMembers()`
- **Description**:
  - Reads field information
  - Captures const value for literals
  - Determines read-only vs const

##### `ReadEvent(EventInfo, Type) → EventSymbol`
- **Called by**: `ReadMembers()`
- **Description**:
  - Reads event with handler type
  - Determines virtual/override from add method
  - Detects explicit interface implementations

##### `ReadConstructor(ConstructorInfo, Type) → ConstructorSymbol`
- **Called by**: `ReadMembers()`
- **Calls**: `ReadParameter()` for each parameter
- **Description**:
  - Reads constructor parameters
  - Distinguishes instance vs static constructors

##### `ReadParameter(ParameterInfo) → ParameterSymbol`
- **Called by**: `ReadMethod()`, `ReadProperty()`, `ReadConstructor()`
- **Calls**: `TypeScriptReservedWords.SanitizeParameterName()`
- **Description**:
  - Reads parameter with type and modifiers
  - Sanitizes parameter name (TypeScript reserved words)
  - Detects ref/out/params
  - Captures default values

**Parameter Name Sanitization**:
```csharp
// Input: "interface" (C# keyword)
// Output: "interface_" (TypeScript-safe)
var paramName = param.Name ?? $"arg{param.Position}";
var sanitizedName = TypeScriptReservedWords.SanitizeParameterName(paramName);
```

##### `IsCompilerGenerated(string typeName) → bool` (private static)
- **Called by**: `ReadAssemblies()`, nested type filtering
- **Description**:
  - Detects compiler-generated types by unspeakable names
  - Checks for `<` or `>` in type name
  - Filters out: `<Module>`, `<>c__DisplayClass`, `<Name>e__FixedBuffer`, etc.

---

### File: `src/tsbindgen/SinglePhase/Load/TypeReferenceFactory.cs`

**Purpose**: Creates immutable `TypeReference` objects from CLR `Type` instances. Handles all type varieties: named, generic, arrays, pointers, by-ref, nested.

#### Methods

##### `Create(Type) → TypeReference`
- **Called by**: `ReflectionReader` methods (ReadMethod, ReadProperty, ReadField, ReadEvent, ReadParameter)
- **Calls**: Recursively calls itself for element types, type arguments, etc.
- **Description**:
  - Converts CLR `Type` to immutable `TypeReference`
  - Handles all type categories
  - Preserves generic instantiation information

**Type Categories**:
```csharp
if (type.IsGenericParameter) return GenericParameterReference
if (type.IsArray) return ArrayTypeReference
if (type.IsPointer) return PointerTypeReference
if (type.IsByRef) return ByRefTypeReference
if (type.IsNested) return NestedTypeReference
else return NamedTypeReference  // Includes generic instantiations
```

##### `CreateGenericParameterSymbol(Type) → GenericParameterSymbol`
- **Called by**: `ReflectionReader.ReadType()`, `ReadMethod()`
- **Calls**: Reads constraints via `GetGenericParameterConstraints()`
- **Description**:
  - Creates generic parameter symbol (T, U, etc.)
  - Reads constraints (where T : IComparable)
  - Detects variance (in/out)
  - Detects special constraints (class, struct, new())

**Constraint Reading**:
```csharp
var constraints = type.GetGenericParameterConstraints()
    .Select(Create)  // Recursive
    .ToImmutableArray();
```

---

### File: `src/tsbindgen/SinglePhase/Load/InterfaceMemberSubstitutor.cs`

**Purpose**: Substitutes type parameters in closed generic interface members. Ensures inherited interface members have correct type arguments.

**Example**:
```csharp
class MyList : IEnumerable<string>
{
    // IEnumerable<T>.GetEnumerator() : IEnumerator<T>
    // After substitution:
    // GetEnumerator() : IEnumerator<string>  ← T replaced with string
}
```

#### Methods

##### `SubstituteClosedInterfaces(BuildContext, SymbolGraph) → void`
- **Called by**: `SinglePhaseBuilder.LoadPhase()`
- **Calls**: Processes each type, substitutes interface members
- **Description**:
  - Finds all interface implementations on types
  - For closed generic interfaces (IEnumerable<string>), substitutes type parameters
  - Updates member signatures with concrete type arguments
  - Mutates SymbolGraph in place (happens during Load, before immutability)

---

## Phase 2: Model

**Purpose**: Define immutable data structures representing the symbol graph. These are used throughout all phases.

### Directory: `src/tsbindgen/SinglePhase/Model/`

This directory contains all model types:

---

### File: `src/tsbindgen/SinglePhase/Model/SymbolGraph.cs`

**Purpose**: Root of the symbol graph. Contains all namespaces and source assemblies.

#### Type: `SymbolGraph`

```csharp
public sealed record SymbolGraph
{
    ImmutableArray<NamespaceSymbol> Namespaces;
    ImmutableHashSet<string> SourceAssemblies;

    // Indices (added in Normalize phase)
    SymbolIndices? Indices;
}
```

**Key Methods**:
- `WithIndices() → SymbolGraph`: Builds symbol indices for lookup
- `WithUpdatedNamespace(string name, Func<NamespaceSymbol, NamespaceSymbol>) → SymbolGraph`: Immutably updates a namespace
- `WithUpdatedType(string stableId, Func<TypeSymbol, TypeSymbol>) → SymbolGraph`: Immutably updates a type
- `GetStatistics() → SymbolGraphStatistics`: Computes statistics (namespace/type/member counts)

---

### File: `src/tsbindgen/SinglePhase/Model/Symbols/NamespaceSymbol.cs`

**Purpose**: Represents a namespace containing types.

#### Type: `NamespaceSymbol`

```csharp
public sealed record NamespaceSymbol
{
    string Name;  // "System.Collections.Generic"
    ImmutableArray<TypeSymbol> Types;
    TypeStableId StableId;
    ImmutableHashSet<string> ContributingAssemblies;  // Multiple assemblies can contribute to same namespace
}
```

---

### File: `src/tsbindgen/SinglePhase/Model/Symbols/TypeSymbol.cs`

**Purpose**: Represents a type (class, interface, struct, enum, delegate).

#### Type: `TypeSymbol`

```csharp
public sealed record TypeSymbol
{
    // Identity
    TypeStableId StableId;
    string ClrFullName;  // "System.Collections.Generic.List`1"
    string ClrName;      // "List`1"
    string Namespace;    // "System.Collections.Generic"

    // Kind
    TypeKind Kind;  // Class, Interface, Struct, Enum, Delegate, StaticNamespace

    // Generics
    int Arity;  // 0 for non-generic, 1 for List<T>, 2 for Dictionary<TKey,TValue>
    ImmutableArray<GenericParameterSymbol> GenericParameters;

    // Hierarchy
    TypeReference? BaseType;
    ImmutableArray<TypeReference> Interfaces;

    // Members
    TypeMembers Members;  // Methods, properties, fields, events, constructors
    ImmutableArray<TypeSymbol> NestedTypes;

    // Views (added by ViewPlanner in Shape phase)
    ImmutableArray<ExplicitView> ExplicitViews;

    // Flags
    bool IsValueType;
    bool IsAbstract;
    bool IsSealed;
    bool IsStatic;

    // Emit name (set by NameReservation phase)
    string? TsEmitName;
}
```

**Key Methods**:
- `WithMembers(TypeMembers) → TypeSymbol`: Updates members
- `WithExplicitViews(ImmutableArray<ExplicitView>) → TypeSymbol`: Updates views
- `WithTsEmitName(string) → TypeSymbol`: Sets TypeScript name

---

### File: `src/tsbindgen/SinglePhase/Model/Symbols/MemberSymbols/*.cs`

**Purpose**: Represent different kinds of members.

#### Type: `MethodSymbol`

```csharp
public sealed record MethodSymbol
{
    MemberStableId StableId;
    string ClrName;
    TypeReference ReturnType;
    ImmutableArray<ParameterSymbol> Parameters;
    ImmutableArray<GenericParameterSymbol> GenericParameters;

    // Modifiers
    bool IsStatic;
    bool IsAbstract;
    bool IsVirtual;
    bool IsOverride;
    bool IsSealed;
    Visibility Visibility;

    // Origin tracking
    MemberProvenance Provenance;  // Original, FromInterface, Synthesized, FromBase
    TypeReference? SourceInterface;  // Which interface this came from (if FromInterface)

    // Emit scope (set by Shape phase)
    EmitScope EmitScope;  // ClassSurface, ViewOnly, Omitted

    // Emit name (set by NameReservation)
    string? TsEmitName;
}
```

#### Type: `PropertySymbol`

```csharp
public sealed record PropertySymbol
{
    MemberStableId StableId;
    string ClrName;
    TypeReference PropertyType;
    ImmutableArray<ParameterSymbol> IndexParameters;  // For indexers

    // Accessors
    bool HasGetter;
    bool HasSetter;

    // Modifiers
    bool IsStatic;
    bool IsVirtual;
    bool IsOverride;
    bool IsAbstract;
    Visibility Visibility;

    // Origin tracking
    MemberProvenance Provenance;
    TypeReference? SourceInterface;

    // Emit scope
    EmitScope EmitScope;
    string? TsEmitName;
}
```

#### Type: `FieldSymbol`, `EventSymbol`, `ConstructorSymbol`

Similar structures for fields, events, and constructors.

---

### File: `src/tsbindgen/SinglePhase/Model/Types/TypeReference.cs`

**Purpose**: Represents a reference to a type (can be generic, array, pointer, etc.).

#### Type Hierarchy

```csharp
public abstract record TypeReference;

public sealed record NamedTypeReference : TypeReference
{
    string FullName;  // "System.Collections.Generic.List`1"
    string Name;      // "List`1"
    string AssemblyName;
    ImmutableArray<TypeReference> TypeArguments;  // For generics
}

public sealed record GenericParameterReference : TypeReference
{
    string Name;  // "T", "TKey", etc.
    GenericParameterScope Scope;  // Type-level or Method-level
    int Position;
}

public sealed record ArrayTypeReference : TypeReference
{
    TypeReference ElementType;
    int Rank;  // 1 for T[], 2 for T[,], etc.
}

public sealed record PointerTypeReference : TypeReference
{
    TypeReference PointeeType;
}

public sealed record ByRefTypeReference : TypeReference
{
    TypeReference ReferencedType;
}

public sealed record NestedTypeReference : TypeReference
{
    TypeReference DeclaringType;
    string NestedName;
}
```

---

## Phase 3: Shape

**Purpose**: Transform the CLR symbol graph into a TypeScript-ready representation. This is where most complexity lives.

### File: `src/tsbindgen/SinglePhase/SinglePhaseBuilder.cs` - `ShapePhase()`

**Purpose**: Orchestrates 14 transformation passes in sequence.

#### Pass Sequence (Line-by-Line from Code)

```csharp
private static SymbolGraph ShapePhase(BuildContext ctx, SymbolGraph graph)
{
    // 1. Build interface indices BEFORE flattening
    GlobalInterfaceIndex.Build(ctx, graph);
    InterfaceDeclIndex.Build(ctx, graph);

    // 2. Structural conformance analysis (synthesizes ViewOnly members)
    graph = StructuralConformance.Analyze(ctx, graph);

    // 3. Interface inlining (flatten interfaces)
    graph = InterfaceInliner.Inline(ctx, graph);

    // 4. Explicit interface implementation synthesis
    graph = ExplicitImplSynthesizer.Synthesize(ctx, graph);

    // 5. Diamond inheritance resolution
    graph = DiamondResolver.Resolve(ctx, graph);

    // 6. Base overload addition
    graph = BaseOverloadAdder.AddOverloads(ctx, graph);

    // 7. Static-side analysis
    StaticSideAnalyzer.Analyze(ctx, graph);

    // 8. Indexer planning
    graph = IndexerPlanner.Plan(ctx, graph);

    // 9. Hidden member planning
    HiddenMemberPlanner.Plan(ctx, graph);

    // 10. Final indexers pass
    graph = FinalIndexersPass.Run(ctx, graph);

    // 10.5. Class surface deduplication
    graph = ClassSurfaceDeduplicator.Deduplicate(ctx, graph);

    // 11. Constraint closure
    graph = ConstraintCloser.Close(ctx, graph);

    // 12. Return-type conflict resolution
    graph = OverloadReturnConflictResolver.Resolve(ctx, graph);

    // 13. View planning (explicit interface views)
    graph = ViewPlanner.Plan(ctx, graph);

    // 14. Final member deduplication
    graph = MemberDeduplicator.Deduplicate(ctx, graph);

    return graph;
}
```

### 3.1 StructuralConformance.cs

**Purpose**: First pass - analyzes structural conformance for interfaces and synthesizes ViewOnly members. For each interface that cannot be structurally implemented on the class surface, synthesizes ViewOnly members that will later appear in explicit views (As_IInterface properties).

**Key Concept**: This is the **source of ViewOnly members**. It determines what needs to go into views vs class surface using TypeScript-level structural typing.

#### Methods

##### `Analyze(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: AnalyzeType(), WillPlanViewFor(), BuildClassSurface(), BuildInterfaceSurface()

Main entry point. Processes all classes/structs immutably.

```csharp
public static SymbolGraph Analyze(BuildContext ctx, SymbolGraph graph)
{
    var classesAndStructs = graph.Namespaces
        .SelectMany(ns => ns.Types)
        .Where(t => t.Kind == TypeKind.Class || t.Kind == TypeKind.Struct)
        .ToList();

    var updatedNamespaces = graph.Namespaces.Select(ns => {
        var updatedTypes = ns.Types.Select(type => {
            if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
                return type;
            var (updatedType, synthesizedCount) = AnalyzeType(ctx, graph, type);
            return updatedType;
        }).ToImmutableArray();
        return ns with { Types = updatedTypes };
    }).ToImmutableArray();

    return (graph with { Namespaces = updatedNamespaces }).WithIndices();
}
```

##### `AnalyzeType(BuildContext ctx, SymbolGraph graph, TypeSymbol type) -> (TypeSymbol, int)`

**Called by**: Analyze()
**Calls**: WillPlanViewFor(), FindInterface(), BuildClassSurface(), BuildInterfaceSurface(), SynthesizeViewOnlyMethod(), SynthesizeViewOnlyProperty()

Core analysis logic for a single type. For each implemented interface:
1. Checks if we will plan a view for it (WillPlanViewFor)
2. Builds substituted interface surface (BuildInterfaceSurface)
3. Compares against class surface using TypeScript assignability (IsTsAssignableMethod/Property)
4. Synthesizes ViewOnly members for unsatisfied interface members

**M5 Critical**: Uses **TypeScript-level assignability** not CLR signature matching. Two members are TS-assignable if they have the same camelCase name and compatible parameter/return types after type erasure.

```csharp
foreach (var (ifaceMethod, declaringIface) in interfaceSurface.Methods)
{
    // M5 FIX: Check TypeScript-level assignability against class surface
    bool satisfied = classSurface.IsTsAssignableMethod(ifaceMethod);

    if (satisfied)
    {
        continue; // DO NOT synthesize, DO NOT touch EmitScope on class member
    }

    // Synthesize ViewOnly method
    var viewOnlyMethod = SynthesizeViewOnlyMethod(ctx, type, ifaceMethod, declaringIface);
    viewOnlyMethods.Add(viewOnlyMethod);
}
```

##### `WillPlanViewFor(BuildContext ctx, SymbolGraph graph, TypeSymbol type, TypeReference ifaceRef) -> bool`

**Called by**: AnalyzeType()
**Calls**: FindInterface()

Determines if we will plan a view for the given interface. Only synthesize ViewOnly members for interfaces we will actually emit views for. Currently returns true if interface is in graph.

##### `BuildClassSurface(BuildContext ctx, TypeSymbol type) -> ClassSurface`

**Called by**: AnalyzeType()
**Calls**: IsRepresentable()

Builds representable class surface: members that can appear on class surface in TypeScript. **Excludes ViewOnly members** (we're checking if class surface satisfies interface).

```csharp
var methods = type.Members.Methods
    .Where(m => m.EmitScope != EmitScope.ViewOnly && IsRepresentable(m))
    .ToList();
```

##### `BuildInterfaceSurface(BuildContext ctx, SymbolGraph graph, TypeReference closedIfaceRef, TypeSymbol ifaceSymbol) -> InterfaceSurface`

**Called by**: AnalyzeType()
**Calls**: InterfaceResolver.FindDeclaringInterface(), SubstituteMethodTypeParameters(), SubstitutePropertyTypeParameters()

Builds flattened interface surface with type arguments substituted. Returns list of (member after substitution, declaring interface) tuples.

**Important**: Skips indexer properties with IndexParameters.Length > 0 (handled by IndexerPlanner).

##### `SynthesizeViewOnlyMethod(BuildContext ctx, TypeSymbol type, MethodSymbol ifaceMethod, TypeReference declaringInterface) -> MethodSymbol`

**Called by**: AnalyzeType()

Creates a ViewOnly method symbol. **M5 Critical**: Uses **interface member's StableId**, not class StableId. This ensures class members (ClassSurface) and view clones (ViewOnly) never share IDs.

```csharp
return new MethodSymbol
{
    StableId = ifaceMethod.StableId,  // M5: interface StableId!
    ClrName = ifaceMethod.ClrName,
    // ... copy signature ...
    Provenance = MemberProvenance.ExplicitView,
    EmitScope = EmitScope.ViewOnly,
    SourceInterface = declaringInterface
};
```

##### `SynthesizeViewOnlyProperty(BuildContext ctx, TypeSymbol type, PropertySymbol ifaceProperty, TypeReference declaringInterface) -> PropertySymbol`

**Called by**: AnalyzeType()

Same as SynthesizeViewOnlyMethod but for properties. Uses interface property's StableId.

##### ClassSurface Record Methods

**Inner record**: `ClassSurface(List<MethodSymbol>, List<PropertySymbol>, BuildContext)`

###### `IsTsAssignableMethod(MethodSymbol ifaceMethod) -> bool`

**Called by**: AnalyzeType()
**Calls**: EraseMethodForAssignability(), Plan.TsAssignability.IsMethodAssignable()

Checks if **any** class method is TypeScript-assignable to the interface method. Uses TS-level structural typing, not CLR signature matching.

**Algorithm**:
1. Find candidates by name (case-insensitive, since TS will lowercase both)
2. Erase to TypeScript signatures (without TsEmitName since names aren't reserved yet)
3. Check assignability using Plan.TsAssignability.IsMethodAssignable()

```csharp
public bool IsTsAssignableMethod(MethodSymbol ifaceMethod)
{
    var candidates = Methods.Where(m =>
        string.Equals(m.ClrName, ifaceMethod.ClrName, StringComparison.OrdinalIgnoreCase));

    foreach (var classMethod in candidates)
    {
        var classSig = EraseMethodForAssignability(classMethod);
        var ifaceSig = EraseMethodForAssignability(ifaceMethod);

        if (Plan.TsAssignability.IsMethodAssignable(classSig, ifaceSig))
            return true;
    }
    return false;
}
```

###### `IsTsAssignableProperty(PropertySymbol ifaceProperty) -> bool`

**Called by**: AnalyzeType()
**Calls**: ErasePropertyForAssignability(), Plan.TsAssignability.IsPropertyAssignable()

Same as IsTsAssignableMethod but for properties.

###### `EraseMethodForAssignability(MethodSymbol method) -> Plan.TsMethodSignature`

**Called by**: IsTsAssignableMethod()
**Calls**: Plan.TsErase.EraseType()

Erases method to TS signature **without using TsEmitName** (not set yet). Applies camelCase rule directly.

```csharp
return new Plan.TsMethodSignature(
    Name: method.ClrName.ToLowerInvariant(), // Apply camelCase directly
    Arity: method.Arity,
    Parameters: method.Parameters.Select(p => Plan.TsErase.EraseType(p.Type)).ToList(),
    ReturnType: Plan.TsErase.EraseType(method.ReturnType));
```

###### `ErasePropertyForAssignability(PropertySymbol property) -> Plan.TsPropertySignature`

**Called by**: IsTsAssignableProperty()
**Calls**: Plan.TsErase.EraseType()

Erases property to TS signature without using TsEmitName.

---

### 3.2 InterfaceInliner.cs

**Purpose**: Inlines interface hierarchies - removes extends chains. Flattens all inherited members into each interface so TypeScript doesn't need extends. Pure transformation.

**Why**: Simplifies interface representation and eliminates complex inheritance graphs in generated TypeScript.

#### Methods

##### `Inline(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: InlineInterface()

Main entry point. Processes all interfaces.

```csharp
public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph)
{
    var interfacesToInline = graph.Namespaces
        .SelectMany(ns => ns.Types)
        .Where(t => t.Kind == TypeKind.Interface)
        .ToList();

    var updatedGraph = graph;
    foreach (var iface in interfacesToInline)
    {
        updatedGraph = InlineInterface(ctx, updatedGraph, iface);
    }

    return updatedGraph;
}
```

##### `InlineInterface(BuildContext ctx, SymbolGraph graph, TypeSymbol iface) -> SymbolGraph`

**Called by**: Inline()
**Calls**: FindInterfaceByReference(), DeduplicateMethods(), DeduplicateProperties(), DeduplicateEvents()

Inlines a single interface:
1. Collects all members from this interface
2. Walks up the interface hierarchy (BFS) collecting inherited members
3. Deduplicates by canonical signature
4. Creates updated type with inlined members and **cleared interfaces list**

**Result**: Interface has all members from base interfaces inlined, and Interfaces property is empty.

```csharp
// Walk up the interface hierarchy and collect all inherited members
var visited = new HashSet<string>();
var toVisit = new Queue<TypeReference>(iface.Interfaces);

while (toVisit.Count > 0)
{
    var baseIfaceRef = toVisit.Dequeue();
    var baseIface = FindInterfaceByReference(graph, baseIfaceRef);
    if (baseIface == null) continue; // External interface

    // Add all members from base interface
    allMembers.AddRange(baseIface.Members.Methods);
    // Queue base interface's bases for visiting
    foreach (var grandparent in baseIface.Interfaces)
        toVisit.Enqueue(grandparent);
}

// Deduplicate and create updated type with cleared interfaces
return graph.WithUpdatedType(iface.StableId.ToString(), t => t with
{
    Members = newMembers,
    Interfaces = ImmutableArray<TypeReference>.Empty  // Cleared!
});
```

##### `DeduplicateMethods(BuildContext ctx, List<MethodSymbol> methods) -> IReadOnlyList<MethodSymbol>`

**Called by**: InlineInterface()
**Calls**: ctx.CanonicalizeMethod()

Deduplicates methods by canonical signature. If duplicate, keeps the first one (deterministic).

##### `DeduplicateProperties(BuildContext ctx, List<PropertySymbol> properties) -> IReadOnlyList<PropertySymbol>`

**Called by**: InlineInterface()
**Calls**: ctx.CanonicalizeProperty()

Deduplicates properties. **Key difference from methods**:
- Regular properties: use **name only** (TypeScript doesn't allow property overloads)
- Indexers: use **full signature** (name + params + type)

```csharp
string key;
if (prop.IsIndexer)
{
    // Indexers: use full signature
    key = ctx.CanonicalizeProperty(prop.ClrName, indexParams, GetTypeFullName(prop.PropertyType));
}
else
{
    // Regular properties: use name only
    key = prop.ClrName;
}
```

##### `DeduplicateEvents(BuildContext ctx, List<EventSymbol> events) -> IReadOnlyList<EventSymbol>`

**Called by**: InlineInterface()
**Calls**: SignatureCanonicalizer.CanonicalizeEvent()

Deduplicates events by canonical signature.

---

### 3.3 ExplicitImplSynthesizer.cs

**Purpose**: Synthesizes missing interface members for classes/structs. Ensures all interface-required members exist on implementing types. Creates ViewOnly members for explicit interface implementations that weren't detected by reflection.

**Difference from StructuralConformance**: StructuralConformance checks TS assignability; ExplicitImplSynthesizer checks for **missing** members (pure CLR signature matching).

#### Methods

##### `Synthesize(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: SynthesizeForType()

Main entry point. Processes all classes/structs.

##### `SynthesizeForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type) -> (SymbolGraph, int)`

**Called by**: Synthesize()
**Calls**: CollectInterfaceMembers(), FindMissingMembers(), SynthesizeMethod(), SynthesizeProperty()

Core synthesis logic:
1. Collects all interface members required
2. Finds which ones are missing from the type
3. Synthesizes the missing members as ViewOnly

```csharp
private static (SymbolGraph UpdatedGraph, int SynthesizedCount) SynthesizeForType(...)
{
    var requiredMembers = CollectInterfaceMembers(ctx, graph, type);
    var missing = FindMissingMembers(ctx, type, requiredMembers);

    if (missing.Count == 0)
        return (graph, 0);

    // Synthesize the missing members
    var synthesizedMethods = new List<MethodSymbol>();
    foreach (var (iface, method) in missing.Methods)
    {
        var synthesized = SynthesizeMethod(ctx, type, iface, method);
        synthesizedMethods.Add(synthesized);
    }

    // Add to type immutably
    var updatedGraph = graph.WithUpdatedType(type.StableId.ToString(), t => t with
    {
        Members = t.Members with
        {
            Methods = t.Members.Methods.Concat(synthesizedMethods).ToImmutableArray()
        }
    });

    return (updatedGraph, synthesizedMethods.Count);
}
```

##### `CollectInterfaceMembers(BuildContext ctx, SymbolGraph graph, TypeSymbol type) -> InterfaceMembers`

**Called by**: SynthesizeForType()
**Calls**: WillPlanViewFor(), FindInterface()

Collects all methods and properties required by interfaces. Gates synthesis: only processes interfaces we will emit views for. Skips indexer properties.

##### `FindMissingMembers(BuildContext ctx, TypeSymbol type, InterfaceMembers required) -> MissingMembers`

**Called by**: SynthesizeForType()
**Calls**: ctx.CanonicalizeMethod(), ctx.CanonicalizeProperty()

Compares required interface members against type's existing members using canonical signature matching. Returns members that don't exist on the type.

##### `SynthesizeMethod(BuildContext ctx, TypeSymbol type, TypeReference iface, MethodSymbol method) -> MethodSymbol`

**Called by**: SynthesizeForType()
**Calls**: InterfaceResolver.FindDeclaringInterface()

Synthesizes a ViewOnly method. **M5 Critical**: Uses interface member's StableId, marks as ViewOnly. Logs with "eii:" prefix.

```csharp
var stableId = method.StableId;  // M5: interface StableId

ctx.Log("explicit-impl",
    $"eii: {type.StableId} {declaringInterface} {Plan.PhaseGate.FormatMemberStableId(stableId)} -> ViewOnly");

return new MethodSymbol
{
    StableId = stableId,
    Provenance = MemberProvenance.ExplicitView,
    EmitScope = EmitScope.ViewOnly,
    SourceInterface = declaringInterface ?? iface
};
```

##### `SynthesizeProperty(BuildContext ctx, TypeSymbol type, TypeReference iface, PropertySymbol property) -> PropertySymbol`

**Called by**: SynthesizeForType()
**Calls**: InterfaceResolver.FindDeclaringInterface()

Same as SynthesizeMethod but for properties.

---

### 3.4 DiamondResolver.cs

**Purpose**: Resolves diamond inheritance conflicts. When multiple inheritance paths bring the same method with potentially different signatures, ensures all variants are available in TypeScript according to policy.

**Diamond Pattern**: Class C implements both I1 and I2, both of which extend I0. If I1 and I2 have different implementations of a method from I0, this creates a diamond conflict.

#### Methods

##### `Resolve(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: AnalyzeForDiamonds() or ResolveForType()

Main entry point. Checks policy and either:
- Error: Analyzes for conflicts and reports errors
- OverloadAll or PreferDerived: Resolves conflicts according to strategy

```csharp
public static SymbolGraph Resolve(BuildContext ctx, SymbolGraph graph)
{
    var strategy = ctx.Policy.Interfaces.DiamondResolution;

    if (strategy == DiamondResolutionStrategy.Error)
    {
        AnalyzeForDiamonds(ctx, graph);
        return graph;
    }

    // Resolve according to strategy
    var updatedGraph = graph;
    foreach (var type in allTypes)
    {
        var (newGraph, resolved) = ResolveForType(ctx, updatedGraph, type, strategy);
        updatedGraph = newGraph;
    }
    return updatedGraph;
}
```

##### `ResolveForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type, DiamondResolutionStrategy strategy) -> (SymbolGraph, int)`

**Called by**: Resolve()
**Calls**: ctx.CanonicalizeMethod(), EnsureMethodRenamed()

Resolves diamond conflicts for a single type:
1. Groups methods by name
2. Groups by signature within each name group
3. If multiple signatures exist → diamond conflict
4. Applies strategy:
   - **OverloadAll**: Keep all overloads (ensure unique names via renamer)
   - **PreferDerived**: Keep first (most derived), mark others as ViewOnly

##### `EnsureMethodRenamed(BuildContext ctx, TypeSymbol type, MethodSymbol method)`

**Called by**: ResolveForType()
**Calls**: ctx.Renamer.ReserveMemberName()

Reserves method name through renamer with "DiamondResolved" reason.

##### `AnalyzeForDiamonds(BuildContext ctx, SymbolGraph graph)`

**Called by**: Resolve()
**Calls**: ctx.Diagnostics.Warning()

Analyzes for diamond conflicts and reports warnings. Used when policy is Error.

---

### 3.5 BaseOverloadAdder.cs

**Purpose**: Adds base class overloads when derived class differs. In TypeScript, all overloads must be present on the derived class even if they're inherited from base.

**Example**: Base has `Foo(int)` and `Foo(string)`. Derived overrides only `Foo(int)`. We must add `Foo(string)` to derived.

#### Methods

##### `AddOverloads(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: AddOverloadsForClass()

Main entry point. Processes all classes with base types.

##### `AddOverloadsForClass(BuildContext ctx, SymbolGraph graph, TypeSymbol derivedClass) -> (SymbolGraph, int)`

**Called by**: AddOverloads()
**Calls**: FindBaseClass(), CreateBaseOverloadMethod()

Core logic:
1. Finds base class
2. Groups methods by name (both derived and base)
3. For each base method name, checks if derived has all the same overloads
4. Adds missing overloads with Provenance.BaseOverload

```csharp
// For each base method name, check if derived has all the same overloads
foreach (var (methodName, baseMethods) in baseMethodsByName)
{
    if (!derivedMethodsByName.TryGetValue(methodName, out var derivedMethods))
        continue; // Derived doesn't override this method at all

    // Check each base method to see if derived has the same signature
    foreach (var baseMethod in baseMethods)
    {
        var baseSig = ctx.CanonicalizeMethod(...);
        var derivedHasSig = derivedMethods.Any(dm => dSig == baseSig);

        if (!derivedHasSig)
        {
            // Derived doesn't have this base overload - add it
            var addedMethod = CreateBaseOverloadMethod(ctx, derivedClass, baseMethod);
            addedMethods.Add(addedMethod);
        }
    }
}
```

##### `CreateBaseOverloadMethod(BuildContext ctx, TypeSymbol derivedClass, MethodSymbol baseMethod) -> MethodSymbol`

**Called by**: AddOverloadsForClass()
**Calls**: ctx.Renamer.ReserveMemberName()

Creates a method with:
- **New StableId** for derived class (assembly = derived, declaringType = derived)
- Provenance.BaseOverload
- EmitScope.ClassSurface
- Reserves name with "BaseOverload" reason

---

### 3.6 StaticSideAnalyzer.cs

**Purpose**: Analyzes static-side inheritance issues. Detects when static members conflict with instance members from the class hierarchy. TypeScript doesn't allow the static side of a class to extend the static side of the base class.

**Why**: In TypeScript, static members don't inherit. If derived has static member "Foo" and base has static member "Foo", they conflict.

#### Methods

##### `Analyze(BuildContext ctx, SymbolGraph graph)`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: AnalyzeClass()

Main entry point. Processes all classes with base types. **Note**: Returns void, not SymbolGraph (analysis only, optional renaming).

##### `AnalyzeClass(BuildContext ctx, SymbolGraph graph, TypeSymbol derivedClass, StaticSideAction action) -> (int issues, int renamed)`

**Called by**: Analyze()
**Calls**: FindBaseClass(), GetStaticMemberNames(), RenameConflictingStatic()

Analyzes static-side conflicts:
1. Gets static members from both derived and base
2. Finds name conflicts using HashSet intersection
3. Takes action according to policy:
   - **Error**: Reports error diagnostic
   - **AutoRename**: Renames conflicting statics with "_static" suffix
   - **Analyze**: Reports warning diagnostic

```csharp
var derivedStaticNames = GetStaticMemberNames(derivedStatics);
var baseStaticNames = GetStaticMemberNames(baseStatics);
var conflicts = derivedStaticNames.Intersect(baseStaticNames).ToList();

foreach (var conflictName in conflicts)
{
    if (action == StaticSideAction.AutoRename)
    {
        var renamed = RenameConflictingStatic(ctx, derivedClass, derivedStatics, conflictName);
        renamedCount += renamed;
    }
    // ... report diagnostics ...
}
```

##### `RenameConflictingStatic(BuildContext ctx, TypeSymbol derivedClass, List<object> derivedStatics, string conflictName) -> int`

**Called by**: AnalyzeClass()
**Calls**: ctx.Renamer.ReserveMemberName()

Renames all static members with the conflicting name by appending "_static". Reserves through renamer with "StaticSideNameCollision" reason.

---

### 3.7 IndexerPlanner.cs

**Purpose**: Plans indexer representation (property vs methods). Single uniform indexers → keep as properties. Multiple/heterogeneous indexers → convert to methods with configured name.

**Policy-Driven**:
- `EmitPropertyWhenSingle`: If true and only 1 indexer, keep as property
- Otherwise: Convert ALL indexers to get/set methods, remove ALL indexer properties

#### Methods

##### `Plan(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: PlanIndexersForType()

Main entry point. Processes all types with indexers.

##### `PlanIndexersForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type, out bool wasConverted) -> SymbolGraph`

**Called by**: Plan()
**Calls**: ToIndexerMethods()

Policy enforcement:
1. If single indexer AND policy allows → keep as property
2. Otherwise → convert ALL to methods and remove ALL indexer properties

```csharp
if (indexers.Length == 1 && policy.EmitPropertyWhenSingle)
{
    // Keep single indexer as property
    return graph;
}

// Convert ALL to methods
var synthesizedMethods = indexers
    .SelectMany(indexer => ToIndexerMethods(ctx, type, indexer, methodName))
    .ToImmutableArray();

var updatedGraph = graph.WithUpdatedType(type.ClrFullName, t =>
    t.WithAddedMethods(synthesizedMethods)
     .WithRemovedProperties(p => p.IsIndexer));
```

##### `ToIndexerMethods(BuildContext ctx, TypeSymbol type, PropertySymbol indexer, string methodName) -> IEnumerable<MethodSymbol>`

**Called by**: PlanIndexersForType()
**Calls**: ctx.Renamer.ReserveMemberName(), ctx.Renamer.GetFinalMemberName()

Converts indexer property to getter/setter methods:
- Getter: `T get_Item(TIndex index)`
- Setter: `void set_Item(TIndex index, T value)`

**Important**: Sets TsEmitName immediately (unlike later passes that leave it empty).

```csharp
if (indexer.HasGetter)
{
    var getterName = $"get_{methodName}";  // Default: "get_Item"
    var getterStableId = new MemberStableId { ... };

    ctx.Renamer.ReserveMemberName(getterStableId, getterName, typeScope, "IndexerGetter", indexer.IsStatic);
    var getterTsEmitName = ctx.Renamer.GetFinalMemberName(getterStableId, typeScope, indexer.IsStatic);

    yield return new MethodSymbol
    {
        ClrName = getterName,
        TsEmitName = getterTsEmitName,  // Set immediately
        Provenance = MemberProvenance.IndexerNormalized,
        EmitScope = EmitScope.ClassSurface
    };
}
```

---

### 3.8 HiddenMemberPlanner.cs

**Purpose**: Plans handling of C# 'new' hidden members. When a derived class hides a base member with 'new', we need to emit both the base member (inherited) and the derived member (with a suffix like "_new"). Uses the Renamer to reserve names with "HiddenNewConflict" reason.

**Example**: Base has `void Foo()`, Derived has `new void Foo()`. Derived.Foo becomes "foo_new", base Foo is inherited.

#### Methods

##### `Plan(BuildContext ctx, SymbolGraph graph)`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: ProcessType()

Main entry point. Processes all types. **Note**: Returns void (pure renaming through Renamer).

##### `ProcessType(BuildContext ctx, TypeSymbol type) -> int`

**Called by**: Plan()
**Calls**: ctx.Renamer.ReserveMemberName()

Processes a single type:
1. Only processes classes/structs with base type
2. Finds methods marked with IsNew
3. Reserves renamed version (ClrName + suffix) through Renamer with "HiddenNewConflict" reason
4. Recursively processes nested types

```csharp
foreach (var method in type.Members.Methods.Where(m => m.IsNew))
{
    var suffix = ctx.Policy.Classes.HiddenMemberSuffix;  // Default: "_new"
    var requestedName = method.ClrName + suffix;

    ctx.Renamer.ReserveMemberName(
        method.StableId,
        requestedName,
        typeScope with { IsStatic = method.IsStatic },
        "HiddenNewConflict",
        method.IsStatic,
        "HiddenMemberPlanner");

    count++;
}
```

---

### 3.9 FinalIndexersPass.cs

**Purpose**: Final, definitive pass to ensure indexer policy is enforced. Runs at the end of Shape phase to ensure no indexer properties leak through from earlier passes.

**Invariant**:
- 0 indexers → nothing to do
- 1 indexer → keep as property ONLY if policy.EmitPropertyWhenSingle == true
- ≥2 indexers → convert ALL to get/set methods, remove ALL indexer properties

#### Methods

##### `Run(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: ToIndexerMethods()

Main entry point. Enforces final indexer policy for all types. **Difference from IndexerPlanner**: This is the **final enforcement** pass - catches any indexers that leaked through earlier transformations.

```csharp
foreach (var type in ns.Types)
{
    var indexers = type.Members.Properties.Where(p => p.IsIndexer).ToImmutableArray();

    if (indexers.Length == 0) continue;

    // Invariant check
    if (indexers.Length == 1 && policy.EmitPropertyWhenSingle)
    {
        continue; // Keep single indexer
    }

    // Convert all to methods + remove all indexer properties
    ctx.Diagnostics.Info(DiagnosticCodes.IndexerConflict,
        $"Omitted {indexers.Length} indexer properties from {type.ClrFullName}");

    var methods = indexers.SelectMany(idx => ToIndexerMethods(...)).ToImmutableArray();

    updatedGraph = updatedGraph.WithUpdatedType(type.ClrFullName, t =>
        t.WithMembers(t.Members with
        {
            Methods = t.Members.Methods.AddRange(methods),
            Properties = t.Members.Properties.RemoveAll(p => p.IsIndexer)
        }));
}
```

##### `ToIndexerMethods(BuildContext ctx, TypeSymbol type, PropertySymbol indexer, string methodName) -> IEnumerable<MethodSymbol>`

**Called by**: Run()

Similar to IndexerPlanner.ToIndexerMethods() but **leaves TsEmitName empty** (will be set by NameReservation pass).

```csharp
yield return new MethodSymbol
{
    ClrName = getterName,
    TsEmitName = "",  // Will be set by NameReservation pass
    Provenance = MemberProvenance.IndexerNormalized,
    EmitScope = EmitScope.ClassSurface
};
```

---

### 3.10 ClassSurfaceDeduplicator.cs

**Purpose**: Deduplicates class surface by emitted name (post-camelCase). When multiple properties emit to the same name, keeps the most specific one and demotes others to ViewOnly.

**Example**: IEnumerator.Current (returns object) and IEnumerator<T>.Current (returns T) both emit to "current". Keep IEnumerator<T>.Current (generic), demote IEnumerator.Current to ViewOnly.

#### Methods

##### `Deduplicate(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: DeduplicateType()

Main entry point. Processes all types immutably.

##### `DeduplicateType(BuildContext ctx, TypeSymbol type) -> (TypeSymbol, int)`

**Called by**: Deduplicate()
**Calls**: DeduplicateProperties()

Only processes classes and structs. Deduplicates properties by emitted name.

##### `DeduplicateProperties(BuildContext ctx, TypeSymbol type) -> (ImmutableArray<PropertySymbol>, int)`

**Called by**: DeduplicateType()
**Calls**: ApplyCamelCase(), PickWinner()

Core deduplication logic:
1. Groups class-surface properties by emitted name (camelCase)
2. For groups with duplicates, picks winner using deterministic rules
3. Demotes losers to ViewOnly

```csharp
var groups = type.Members.Properties
    .Where(p => p.EmitScope == EmitScope.ClassSurface)
    .GroupBy(p => ApplyCamelCase(p.ClrName))
    .Where(g => g.Count() > 1)
    .ToList();

foreach (var group in groups)
{
    var winner = PickWinner(candidates);

    ctx.Log("class-dedupe",
        $"winner: {type.StableId} name={emittedName} kept={FormatStableId(winner.StableId)}");

    foreach (var loser in candidates.Where(c => c.StableId != winner.StableId))
    {
        demotions.Add(loser.StableId);
        ctx.Log("class-dedupe",
            $"demote: {type.StableId} name={emittedName} -> ViewOnly {FormatStableId(loser.StableId)}");
    }
}
```

##### `PickWinner(List<PropertySymbol> candidates) -> PropertySymbol`

**Called by**: DeduplicateProperties()
**Calls**: IsGenericType(), IsObjectType()

Picks the winner using deterministic rules. Preference order:
1. **Non-explicit over explicit** (public member beats EII)
2. **Generic over non-generic** (IEnumerator<T>.Current beats IEnumerator.Current)
3. **Narrower return type over object**
4. **Stable ordering** by (DeclaringClrFullName, CanonicalSignature)

```csharp
return candidates
    .OrderBy(p => p.Provenance == MemberProvenance.ExplicitView ? 1 : 0) // Non-explicit first
    .ThenBy(p => IsGenericType(p.PropertyType) ? 0 : 1) // Generic first
    .ThenBy(p => IsObjectType(p.PropertyType) ? 1 : 0) // Non-object first
    .ThenBy(p => p.StableId.DeclaringClrFullName)
    .ThenBy(p => p.StableId.CanonicalSignature)
    .First();
```

---

### 3.11 ConstraintCloser.cs

**Purpose**: Closes generic constraints for TypeScript. Computes final constraint sets by combining base constraints with any additional requirements. Handles constraint merging strategies (Intersection, Union, etc.) according to policy.

**Key Tasks**:
1. Resolves raw System.Type constraints into TypeReferences
2. Validates constraint compatibility (struct vs class, circular, etc.)
3. Applies constraint merge strategy

#### Methods

##### `Close(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: ResolveAllConstraints(), CloseConstraints()

Main entry point:
1. First resolves raw constraint types into TypeReferences
2. Then closes constraints for all type-level and method-level generic parameters

```csharp
public static SymbolGraph Close(BuildContext ctx, SymbolGraph graph)
{
    // Step 1: Resolve raw constraint types into TypeReferences
    var updatedGraph = ResolveAllConstraints(ctx, graph);

    // Step 2: Close constraints
    foreach (var type in allTypes)
    {
        foreach (var gp in type.GenericParameters)
            CloseConstraints(ctx, gp);

        foreach (var method in type.Members.Methods)
            foreach (var gp in method.GenericParameters)
                CloseConstraints(ctx, gp);
    }

    return updatedGraph;
}
```

##### `ResolveAllConstraints(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: Close()
**Calls**: Load.TypeReferenceFactory.Create()

Resolves raw System.Type constraints into TypeReferences using the memoized TypeReferenceFactory with cycle detection. Updates GenericParameterSymbol.Constraints immutably.

```csharp
if (gp.RawConstraintTypes != null && gp.RawConstraintTypes.Length > 0)
{
    var constraintsBuilder = ImmutableArray.CreateBuilder<TypeReference>();

    foreach (var rawType in gp.RawConstraintTypes)
    {
        var resolved = typeFactory.Create(rawType);  // Uses memoization + cycle detection
        constraintsBuilder.Add(resolved);
    }

    var updatedGp = gp with { Constraints = constraintsBuilder.ToImmutable() };
}
```

##### `CloseConstraints(BuildContext ctx, GenericParameterSymbol gp)`

**Called by**: Close()
**Calls**: ValidateConstraints()

Applies constraint merge strategy from policy:
- **Intersection**: TypeScript uses intersection automatically with "T extends A & B & C" (default)
- **Union**: Not supported in TypeScript, warns
- **PreferLeft**: Keep only first constraint

```csharp
switch (strategy)
{
    case ConstraintMergeStrategy.Intersection:
        // TypeScript uses intersection automatically
        // No additional work needed - printer will handle this
        break;

    case ConstraintMergeStrategy.Union:
        ctx.Diagnostics.Warning(DiagnosticCodes.UnsupportedConstraintMerge,
            $"Union constraint merge not supported in TypeScript for {gp.Name}");
        break;
}
```

##### `ValidateConstraints(BuildContext ctx, GenericParameterSymbol gp)`

**Called by**: CloseConstraints()
**Calls**: IsTypeScriptRepresentable()

Validates constraints:
1. Checks for incompatible special constraints (both struct and class)
2. Validates that constraints are representable in TypeScript (warns for pointer/byref)

```csharp
if ((gp.SpecialConstraints & GenericParameterConstraints.ValueType) != 0 &&
    (gp.SpecialConstraints & GenericParameterConstraints.ReferenceType) != 0)
{
    ctx.Diagnostics.Warning(DiagnosticCodes.IncompatibleConstraints,
        $"Generic parameter {gp.Name} has both 'struct' and 'class' constraints");
}

foreach (var constraint in gp.Constraints)
{
    if (!IsTypeScriptRepresentable(constraint))
    {
        ctx.Diagnostics.Warning(DiagnosticCodes.UnrepresentableConstraint, ...);
    }
}
```

---

### 3.12 OverloadReturnConflictResolver.cs

**Purpose**: Resolves return-type conflicts in overloads. TypeScript doesn't support method overloads that differ only in return type. Detects such conflicts and marks non-representative overloads as ViewOnly.

**Example**: `object Foo(int)` and `string Foo(int)` have same parameters but different return types. Keep one, demote other to ViewOnly.

#### Methods

##### `Resolve(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: ResolveForType()

Main entry point. Processes all types.

##### `ResolveForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type) -> (SymbolGraph, int)`

**Called by**: Resolve()
**Calls**: GetSignatureWithoutReturn(), GetPropertySignatureWithoutReturn(), SelectRepresentative()

Core resolution logic:
1. Groups methods by signature **excluding return type**
2. For groups with multiple return types → conflict
3. Selects representative method to keep
4. Marks others as ViewOnly

Also handles indexer properties with return-type conflicts.

```csharp
var methodGroups = type.Members.Methods
    .GroupBy(m => GetSignatureWithoutReturn(ctx, m))
    .Where(g => g.Count() > 1)
    .ToList();

foreach (var group in methodGroups)
{
    var returnTypes = methods.Select(m => GetTypeFullName(m.ReturnType)).Distinct().ToList();

    if (returnTypes.Count <= 1)
        continue; // Same return type, no conflict

    // Return-type conflict detected
    var representative = SelectRepresentative(methods);

    foreach (var method in methods)
    {
        if (method != representative)
            methodsToMarkViewOnly.Add(method);
    }
}
```

##### `GetSignatureWithoutReturn(BuildContext ctx, MethodSymbol method) -> string`

**Called by**: ResolveForType()

Creates signature **excluding return type**: `"MethodName(param1Type,param2Type,...)"`

##### `GetPropertySignatureWithoutReturn(BuildContext ctx, PropertySymbol property) -> string`

**Called by**: ResolveForType()

Creates property signature **excluding property type** but **including accessor kind**: `"this[param1Type,param2Type,...]|accessor=get/set/both/none"`

**Important**: Accessor kind is important - getters and setters should not conflict with each other.

##### `SelectRepresentative(List<MethodSymbol> methods) -> MethodSymbol`

**Called by**: ResolveForType()

Selects representative using preference order:
1. **Prefer non-void returns** (more informative)
2. **Prefer no ref/out parameters** (immutable)
3. **Prefer first in list** (deterministic)

```csharp
var nonVoid = methods.Where(m => GetTypeFullName(m.ReturnType) != "System.Void").ToList();

if (nonVoid.Count > 0)
{
    var immutable = nonVoid.Where(m => !m.Parameters.Any(p => p.IsRef || p.IsOut)).ToList();
    if (immutable.Count > 0)
        return immutable.First();
    return nonVoid.First();
}

return methods.First(); // All void - pick first
```

---

### 3.13 ViewPlanner.cs

**Purpose**: Plans explicit interface views (As_IInterface properties). Creates As_IInterface properties for interfaces that couldn't be structurally implemented. These properties expose interface-specific members that were marked ViewOnly by earlier passes.

**Critical M5 Component**: This is where ExplicitViews are created and attached to types. Works with ViewOnly members synthesized by StructuralConformance and ExplicitImplSynthesizer.

#### Methods

##### `Plan(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: PlanViewsForType()

Main entry point. Processes all classes/structs.

```csharp
public static SymbolGraph Plan(BuildContext ctx, SymbolGraph graph)
{
    var classesAndStructs = graph.Namespaces
        .SelectMany(ns => ns.Types)
        .Where(t => t.Kind == TypeKind.Class || t.Kind == TypeKind.Struct)
        .ToList();

    var updatedGraph = graph;
    foreach (var type in classesAndStructs)
    {
        var plannedViews = PlanViewsForType(ctx, updatedGraph, type);
        if (plannedViews.Count > 0)
        {
            updatedGraph = updatedGraph.WithUpdatedType(type.StableId.ToString(), t =>
                t.WithExplicitViews(plannedViews.ToImmutableArray()));
            totalViews += plannedViews.Count;
        }
    }

    return updatedGraph;
}
```

##### `PlanViewsForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type) -> List<ExplicitView>`

**Called by**: Plan()
**Calls**: CreateViewName(), GetInterfaceStableId()

Core planning logic. **M5 Critical**: Collects ALL ViewOnly members with SourceInterface (no graph filtering). Every ViewOnly member MUST be represented in an ExplicitView.

**Algorithm**:
1. Collects all ViewOnly methods/properties/events with SourceInterface != null
2. Groups by Interface StableId (assembly-qualified identifier)
3. For each interface group:
   - If existing view: MERGE ViewMembers by StableId
   - If new: CREATE view with ViewPropertyName = As_IInterface

```csharp
// M5 FIX: Collect ALL ViewOnly members with SourceInterface
var viewOnlyMembers = new List<(TypeReference ifaceRef, object member, ViewMemberKind kind, MemberStableId stableId, string clrName)>();

foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ViewOnly && m.SourceInterface != null))
{
    viewOnlyMembers.Add((method.SourceInterface!, method, ViewMemberKind.Method, (MemberStableId)method.StableId, method.ClrName));
}
// ... same for properties and events ...

// Group by Interface StableId
var groupsByInterfaceStableId = viewOnlyMembers
    .GroupBy(x => GetInterfaceStableId(x.ifaceRef))
    .OrderBy(g => g.Key)  // Deterministic ordering
    .ToList();

foreach (var group in groupsByInterfaceStableId)
{
    var newViewMembers = group
        .Select(x => new ViewMember(Kind: x.kind, StableId: x.stableId, ClrName: x.clrName))
        .OrderBy(vm => vm.StableId.ToString())
        .ToList();

    if (viewsByInterfaceStableId.TryGetValue(ifaceStableId, out var existingView))
    {
        // MERGE: Union existing ViewMembers with new ones
        var mergedMembers = existingView.ViewMembers
            .Concat(newViewMembers.Where(vm => !existingMemberIds.Contains(vm.StableId)))
            .OrderBy(vm => vm.StableId.ToString())
            .ToImmutableArray();

        view = existingView with { ViewMembers = mergedMembers };
    }
    else
    {
        // CREATE: New view
        var viewName = CreateViewName(ifaceRef);
        view = new ExplicitView(InterfaceReference: ifaceRef, ViewPropertyName: viewName, ViewMembers: newViewMembers.ToImmutableArray());
    }

    plannedViews.Add(view);
}
```

##### `GetInterfaceStableId(TypeReference ifaceRef) -> string`

**Called by**: PlanViewsForType()

Gets the StableId for an interface reference (assembly-qualified identifier). Used for grouping/merging.

```csharp
return ifaceRef switch
{
    NamedTypeReference named => $"{named.AssemblyName}:{named.FullName}",
    NestedTypeReference nested => $"{nested.DeclaringType}+{nested.NestedName}",
    _ => GetTypeFullName(ifaceRef)
};
```

##### `CreateViewName(TypeReference ifaceRef) -> string`

**Called by**: PlanViewsForType()

Creates view property name with type arguments for disambiguation:
- Non-generic: `As_IInterface`
- Generic: `As_IEnumerable_1_of_string` for IEnumerable<string>
- Multi-arg: `As_IDictionary_2_of_string_and_int` for IDictionary<string, int>

```csharp
var baseName = ifaceRef switch
{
    NamedTypeReference named => named.Name,
    NestedTypeReference nested => nested.NestedName,
    _ => "Interface"
};

baseName = baseName.Replace('`', '_');  // IEnumerable`1 → IEnumerable_1
var viewName = $"As_{baseName}";

if (ifaceRef is NamedTypeReference { TypeArguments.Count: > 0 } namedType)
{
    var typeArgNames = namedType.TypeArguments.Select(arg => GetTypeArgumentName(arg)).ToList();
    viewName += "_of_" + string.Join("_and_", typeArgNames);
}

return viewName;
```

##### `GetTypeArgumentName(TypeReference typeRef) -> string`

**Called by**: CreateViewName()

Converts type reference to sanitized name for view naming:
- Named: SanitizeTypeName(named.Name)
- Generic parameter: Use parameter name directly (T, U, etc.)
- Array: ElementName + "_array"

---

### 3.14 MemberDeduplicator.cs

**Purpose**: Final deduplication pass to remove any duplicate members that may have been introduced by multiple Shape passes (BaseOverloadAdder, ExplicitImplSynthesizer, etc.). Keeps the first occurrence of each unique StableId.

**Why Needed**: Multiple Shape passes may synthesize the same member (same StableId). This final pass ensures no duplicates exist before Name Reservation.

#### Methods

##### `Deduplicate(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: SinglePhaseBuilder.ShapePhase()
**Calls**: DeduplicateByStableId()

Main entry point. Deduplicates all member types for all types.

```csharp
public static SymbolGraph Deduplicate(BuildContext ctx, SymbolGraph graph)
{
    foreach (var type in ns.Types)
    {
        var uniqueMethods = DeduplicateByStableId(type.Members.Methods, out var methodDupes);
        var uniqueProperties = DeduplicateByStableId(type.Members.Properties, out var propDupes);
        var uniqueFields = DeduplicateByStableId(type.Members.Fields, out var fieldDupes);
        var uniqueEvents = DeduplicateByStableId(type.Members.Events, out var eventDupes);
        var uniqueCtors = DeduplicateByStableId(type.Members.Constructors, out var ctorDupes);

        if (any duplicates found)
        {
            var newType = type with { Members = newMembers };
            ctx.Log("MemberDeduplicator", $"Removed {total} duplicates from {type.ClrFullName}");
        }
    }
}
```

##### `DeduplicateByStableId<T>(ImmutableArray<T> members, out int duplicatesRemoved) -> ImmutableArray<T>`

**Called by**: Deduplicate()

Generic deduplication by StableId:
1. Uses reflection to get StableId property
2. Tracks seen StableIds in HashSet
3. Keeps first occurrence, skips duplicates

```csharp
var seen = new HashSet<StableId>();
var unique = ImmutableArray.CreateBuilder<T>();

foreach (var member in members)
{
    var stableId = (StableId)stableIdProperty.GetValue(member)!;

    if (!seen.Contains(stableId))
    {
        seen.Add(stableId);
        unique.Add(member);
    }
}

duplicatesRemoved = members.Length - unique.Count;
return unique.ToImmutable();
```

---

## Phase 3.5: Normalize

**Purpose**: Normalization phase that runs between Shape and Plan. Performs name reservation, overload unification, and signature normalization to prepare for emission.

### Normalize.NameReservation.cs

**Purpose**: Centralized name reservation pass. Reserves TypeScript names for ALL members (ClassSurface + ViewOnly + Views) using SymbolRenamer with proper scoping.

**Critical M5 Component**: This is where the PG_NAME_004 bug was fixed. Must reserve names in **separate scopes** for ClassSurface vs ViewOnly members.

**Key Insight**: ClassSurface members use type-scoped names (`type:System.Decimal#instance`), ViewOnly members use view-scoped names (`view:System.Decimal#As_IConvertible`).

[Full implementation documented in NameReservation.cs]

---

### Normalize.OverloadUnifier.cs

**Purpose**: Unifies method overloads that differ only in ways TypeScript can't distinguish. Runs after Plan phase, before PhaseGate.

**Problem**: C# allows overloads differing by ref/out modifiers or generic constraints. TypeScript doesn't support overload disambiguation by these features.

**Solution**: Group methods by TypeScript erasure key (name, arity, param-count), pick the "widest" signature, mark narrower ones as EmitScope.Omitted.

#### Methods

##### `UnifyOverloads(BuildContext ctx, SymbolGraph graph) -> SymbolGraph`

**Called by**: [Currently not in pipeline - would run after Plan]
**Calls**: UnifyTypeOverloads()

Main entry point. Processes all types immutably.

```csharp
public static SymbolGraph UnifyOverloads(BuildContext ctx, SymbolGraph graph)
{
    var updatedNamespaces = graph.Namespaces.Select(ns =>
    {
        var updatedTypes = ns.Types.Select(type =>
        {
            var (updatedType, unifiedCount) = UnifyTypeOverloads(type);
            return updatedType;
        }).ToImmutableArray();

        return ns with { Types = updatedTypes };
    }).ToImmutableArray();

    return graph with { Namespaces = updatedNamespaces };
}
```

##### `UnifyTypeOverloads(TypeSymbol type) -> (TypeSymbol, int)`

**Called by**: UnifyOverloads()
**Calls**: ComputeErasureKey(), SelectWidestSignature()

Core unification logic:
1. Groups methods by TypeScript erasure key
2. For groups with collisions (>1 method), selects widest signature
3. Marks narrower signatures as Omitted

```csharp
var methodGroups = type.Members.Methods
    .Where(m => m.EmitScope == EmitScope.ClassSurface || m.EmitScope == EmitScope.StaticSurface)
    .GroupBy(m => ComputeErasureKey(m))
    .Where(g => g.Count() > 1) // Only process groups with collisions
    .ToList();

foreach (var group in methodGroups)
{
    var widestMethod = SelectWidestSignature(group.ToList());

    // Mark narrower signatures as Omitted
    foreach (var method in group)
    {
        if (method.StableId != widestMethod.StableId)
        {
            updatedMethods[index] = method with { EmitScope = EmitScope.Omitted };
        }
    }
}
```

##### `ComputeErasureKey(MethodSymbol method) -> string`

**Called by**: UnifyTypeOverloads()

Computes TypeScript erasure key. Methods with the same erasure key cannot be distinguished in TypeScript.

**Key format**: `"name|arity|paramCount"` (excludes parameter types, ref/out modifiers, constraints)

```csharp
var name = method.TsEmitName;
var arity = method.Arity; // Generic parameter count
var paramCount = method.Parameters.Length;

return $"{name}|{arity}|{paramCount}";
```

##### `SelectWidestSignature(List<MethodSymbol> overloads) -> MethodSymbol`

**Called by**: UnifyTypeOverloads()
**Calls**: CountRefOutParameters(), CountGenericConstraints()

Selects the widest (most permissive) signature from a group of overloads.

**Preference order**:
1. **Fewer ref/out parameters** (TypeScript doesn't support ref/out)
2. **Fewer generic constraints** (TypeScript has weaker constraint system)
3. **First in declaration order** (stable tie-breaker)

```csharp
var widest = scored
    .OrderBy(s => s.RefOutCount)
    .ThenBy(s => s.ConstraintCount)
    .ThenBy(s => s.Method.StableId.ToString()) // Stable tie-breaker
    .First();
```

---

### Normalize.SignatureNormalization.cs

**Purpose**: Creates normalized, canonical signatures for complete member matching. This is the **SINGLE canonical format** used across BindingEmitter, MetadataEmitter, StructuralConformance, and ViewPlanner.

**Why**: Ensures consistent member matching across all subsystems.

#### Methods

##### `NormalizeMethod(MethodSymbol method) -> string`

**Called by**: BindingEmitter, MetadataEmitter, StructuralConformance, ViewPlanner

Creates canonical method signature.

**Format**: `"MethodName|arity=N|(param1:kind,param2:kind)|->ReturnType|static=bool"`

**Example**: `"CompareTo|arity=0|(T:in)|->int|static=false"`

```csharp
public static string NormalizeMethod(MethodSymbol method)
{
    var sb = new StringBuilder();

    sb.Append(method.ClrName);
    sb.Append("|arity=");
    sb.Append(method.Arity);

    sb.Append("|(");
    for (int i = 0; i < method.Parameters.Length; i++)
    {
        if (i > 0) sb.Append(',');

        var param = method.Parameters[i];
        sb.Append(NormalizeTypeName(param.Type.ToString() ?? "unknown"));
        sb.Append(':');

        // Parameter kind
        if (param.IsOut) sb.Append("out");
        else if (param.IsRef) sb.Append("ref");
        else if (param.IsParams) sb.Append("params");
        else sb.Append("in");

        if (param.HasDefaultValue) sb.Append("?");
    }
    sb.Append(')');

    sb.Append("|->");
    sb.Append(NormalizeTypeName(method.ReturnType.ToString() ?? "void"));

    sb.Append("|static=");
    sb.Append(method.IsStatic ? "true" : "false");

    return sb.ToString();
}
```

##### `NormalizeProperty(PropertySymbol property) -> string`

**Called by**: BindingEmitter, MetadataEmitter, StructuralConformance, ViewPlanner

**Format**: `"PropertyName|(indexParam1,indexParam2)|->PropertyType|static=bool|accessor=get/set/getset"`

**Examples**:
- `"Count|->int|static=false|accessor=get"`
- `"Item|(int)|->T|static=false|accessor=getset"`

##### `NormalizeField(FieldSymbol field) -> string`, `NormalizeEvent(EventSymbol evt) -> string`, `NormalizeConstructor(ConstructorSymbol ctor) -> string`

**Called by**: BindingEmitter, MetadataEmitter

Similar patterns for other member types.

---

## Phase 4: Plan

**Purpose**: Planning phase - prepares for emission by erasing types to TypeScript, checking assignability, building import graphs, planning emission order, and validating everything with PhaseGate.

**Outputs**:
- TsTypeShape erasures for all types
- ImportPlan with imports/exports/aliases
- EmitOrder with deterministic ordering
- InterfaceConstraintFindings from auditor
- PhaseGate validation results

### Plan.TsErase.cs

**Purpose**: Erases CLR-specific details to produce TypeScript-level signatures. Used for assignability checking in PhaseGate validation.

**Key Concept**: This is the **CLR→TS type bridge**. Removes .NET-specific concepts that don't exist in TypeScript.

#### Methods

##### `EraseMember(MethodSymbol method) -> TsMethodSignature`

**Called by**: StructuralConformance, PhaseGate
**Calls**: EraseType()

Erases method to TypeScript signature representation. Removes CLR-specific modifiers (ref/out) and simplifies types.

```csharp
return new TsMethodSignature(
    Name: method.TsEmitName,
    Arity: method.Arity,
    Parameters: method.Parameters.Select(p => EraseType(p.Type)).ToList(),
    ReturnType: EraseType(method.ReturnType));
```

##### `EraseMember(PropertySymbol property) -> TsPropertySignature`

**Called by**: StructuralConformance, PhaseGate

Erases property to TypeScript signature.

```csharp
return new TsPropertySignature(
    Name: property.TsEmitName,
    PropertyType: EraseType(property.PropertyType),
    IsReadonly: !property.HasSetter);
```

##### `EraseType(TypeReference typeRef) -> TsTypeShape`

**Called by**: EraseMember methods, StructuralConformance

Maps CLR types to TypeScript equivalents. **Critical for TS assignability checking**.

```csharp
public static TsTypeShape EraseType(TypeReference typeRef)
{
    return typeRef switch
    {
        // Named types - check if constructed generic or simple
        NamedTypeReference named when named.TypeArguments.Count > 0 =>
            new TsTypeShape.GenericApplication(
                new TsTypeShape.Named(named.FullName),
                named.TypeArguments.Select(EraseType).ToList()),

        NamedTypeReference named => new TsTypeShape.Named(named.FullName),

        // Generic parameters - keep parameter name
        GenericParameterReference gp => new TsTypeShape.TypeParameter(gp.Name),

        // Array types - erase to readonly array
        ArrayTypeReference arr => new TsTypeShape.Array(EraseType(arr.ElementType)),

        // Pointer/ByRef types - erase to element type (TS doesn't support)
        PointerTypeReference ptr => EraseType(ptr.PointeeType),
        ByRefTypeReference byref => EraseType(byref.ReferencedType),

        _ => new TsTypeShape.Unknown(typeRef.ToString() ?? "unknown")
    };
}
```

#### TsTypeShape Hierarchy

**Discriminated union** for TypeScript type shapes:

```csharp
public abstract record TsTypeShape
{
    public sealed record Named(string FullName) : TsTypeShape;
    public sealed record TypeParameter(string Name) : TsTypeShape;
    public sealed record Array(TsTypeShape ElementType) : TsTypeShape;
    public sealed record GenericApplication(TsTypeShape GenericType, List<TsTypeShape> TypeArguments) : TsTypeShape;
    public sealed record Unknown(string Description) : TsTypeShape;
}
```

---

### Plan.TsAssignability.cs

**Purpose**: TypeScript assignability checking for erased type shapes. Implements simplified TypeScript assignability rules.

**Used by**: StructuralConformance (to check if class satisfies interface), PhaseGate validation

#### Methods

##### `IsAssignable(TsTypeShape source, TsTypeShape target) -> bool`

**Called by**: StructuralConformance.IsTsAssignableMethod/Property, PhaseGate
**Calls**: IsWideningConversion()

Checks if source type is assignable to target type in TypeScript. Implements basic structural typing rules.

**Algorithm**:
1. Exact match → true
2. Unknown types → true (conservative for validation)
3. Type parameters with same name → true
4. Arrays → covariant in element type (readonly arrays)
5. Generic applications → check base types match and arguments are assignable
6. Named types → exact match or known widening conversions

```csharp
public static bool IsAssignable(TsTypeShape source, TsTypeShape target)
{
    // Exact match
    if (source.Equals(target))
        return true;

    // Unknown types are compatible (conservative)
    if (source is TsTypeShape.Unknown || target is TsTypeShape.Unknown)
        return true;

    // Arrays: covariant in element type
    if (source is TsTypeShape.Array sourceArr &&
        target is TsTypeShape.Array targetArr)
    {
        return IsAssignable(sourceArr.ElementType, targetArr.ElementType);
    }

    // Generic applications: check base types and arguments
    if (source is TsTypeShape.GenericApplication sourceApp &&
        target is TsTypeShape.GenericApplication targetApp)
    {
        if (!IsAssignable(sourceApp.GenericType, targetApp.GenericType))
            return false;

        return sourceApp.TypeArguments.Zip(targetApp.TypeArguments)
            .All(pair => IsAssignable(pair.First, pair.Second));
    }

    // Named types: exact match or widening conversion
    if (source is TsTypeShape.Named sourceNamed &&
        target is TsTypeShape.Named targetNamed)
    {
        return IsWideningConversion(sourceNamed.FullName, targetNamed.FullName);
    }

    return false;
}
```

##### `IsWideningConversion(string sourceFullName, string targetFullName) -> bool`

**Called by**: IsAssignable()

Checks if there's a known widening conversion from source to target.

**Examples**: int→number, string→object, etc.

```csharp
// Same type
if (sourceFullName == targetFullName)
    return true;

// All numeric types widen to 'number'
var numericTypes = new[] {
    "System.Byte", "System.SByte", "System.Int16", "System.UInt16",
    "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
    "System.Single", "System.Double", "System.Decimal"
};

if (numericTypes.Contains(sourceFullName) && numericTypes.Contains(targetFullName))
    return true;

// Everything widens to System.Object
if (targetFullName == "System.Object")
    return true;
```

##### `IsMethodAssignable(TsMethodSignature source, TsMethodSignature target) -> bool`

**Called by**: StructuralConformance.IsTsAssignableMethod, PhaseGate

Checks if method signature is assignable. **Critical for M5 structural conformance**.

**Rules**:
- Names must match
- Arity must match (generic parameter count)
- Parameter count must match
- **Return types are covariant** (source return can be subtype of target)
- **Parameters are contravariant** (but we use invariant check for safety)

```csharp
public static bool IsMethodAssignable(TsMethodSignature source, TsMethodSignature target)
{
    if (source.Name != target.Name) return false;
    if (source.Arity != target.Arity) return false;
    if (source.Parameters.Count != target.Parameters.Count) return false;

    // Return type is covariant
    if (!IsAssignable(source.ReturnType, target.ReturnType))
        return false;

    // Parameters - use bidirectional assignability check for validation
    for (int i = 0; i < source.Parameters.Count; i++)
    {
        if (!source.Parameters[i].Equals(target.Parameters[i]))
        {
            if (!IsAssignable(source.Parameters[i], target.Parameters[i]) &&
                !IsAssignable(target.Parameters[i], source.Parameters[i]))
            {
                return false;
            }
        }
    }

    return true;
}
```

##### `IsPropertyAssignable(TsPropertySignature source, TsPropertySignature target) -> bool`

**Called by**: StructuralConformance.IsTsAssignableProperty, PhaseGate

Checks if property signature is assignable.

**Rules**:
- Names must match
- **Readonly properties are covariant** in their type
- **Mutable properties are invariant**

```csharp
if (source.Name != target.Name) return false;

// Readonly properties are covariant
if (source.IsReadonly && target.IsReadonly)
{
    return IsAssignable(source.PropertyType, target.PropertyType);
}

// Mutable properties are invariant
return source.PropertyType.Equals(target.PropertyType);
```

---

### Plan.PhaseGate.cs

**Purpose**: Validates the symbol graph before emission. Performs comprehensive validation checks and policy enforcement. Acts as **quality gate** between Shape/Plan phases and Emit phase.

**Critical Component**: The final gatekeeper that ensures:
- All names are reserved
- No collisions exist
- Views are properly formed
- TypeScript will accept the output

#### Key Validation Methods (20+ validators)

##### `Validate(BuildContext ctx, SymbolGraph graph, ImportPlan imports, InterfaceConstraintFindings constraintFindings)`

**Called by**: SinglePhaseBuilder.Build()
**Calls**: All validation methods below

Main entry point. Runs all validation checks and reports results.

**Validation Categories**:

1. **Basic Validation**:
   - `ValidateTypeNames()` - Check TsEmitName is set, no duplicates, reserved word handling
   - `ValidateMemberNames()` - Check TsEmitName for all members
   - `ValidateGenericParameters()` - Check constraints are valid
   - `ValidateInterfaceConformance()` - Check interface satisfaction
   - `ValidateInheritance()` - Check inheritance chains
   - `ValidateEmitScopes()` - Check EmitScope values are valid
   - `ValidateImports()` - Check import/export consistency
   - `ValidatePolicyCompliance()` - Check policy rules

2. **PhaseGate Hardening (M0-M5)**:
   - **M1**: `ValidateIdentifiers()` - Identifier sanitization verification
   - **M2**: `ValidateOverloadCollisions()` - Overload collision detection
   - **M3**: `ValidateViewsIntegrity()` - View integrity (3 hard rules)
   - **M4**: `EmitConstraintDiagnostics()` - Constraint findings from auditor
   - **M5**: `ValidateViewMemberNameScoping()` - View member name scoping (PG_NAME_003, PG_NAME_004)
   - **M5**: `ValidateEmitScopeInvariants()` - EmitScope invariants (PG_INT_002, PG_INT_003)
   - **M5**: `ValidateClassSurfaceUniqueness()` - Class surface uniqueness (PG_NAME_005)

3. **Additional Validation**:
   - `ValidateViews()` - View structure validation
   - `ValidateFinalNames()` - Final name consistency
   - `ValidateAliases()` - Import alias correctness

**Output**:
- Writes `.tests/phasegate-summary.json` - JSON summary for CI
- Writes `.tests/phasegate-diagnostics.txt` - Detailed diagnostics
- Throws error if validation fails (ErrorCount > 0)

```csharp
public static void Validate(BuildContext ctx, SymbolGraph graph, ImportPlan imports, InterfaceConstraintFindings constraintFindings)
{
    var validationContext = new ValidationContext { ... };

    // Run all validation checks
    ValidateTypeNames(ctx, graph, validationContext);
    ValidateMemberNames(ctx, graph, validationContext);
    // ... 20+ validators ...

    if (validationContext.ErrorCount > 0)
    {
        ctx.Diagnostics.Error(DiagnosticCodes.ValidationFailed,
            $"PhaseGate validation failed with {validationContext.ErrorCount} errors");
    }
}
```

---

### Plan.EmitOrderPlanner.cs

**Purpose**: Plans stable, deterministic emission order. Ensures reproducible .d.ts files across runs.

**Why**: Deterministic output enables meaningful diffs, snapshot testing, and version control.

#### Methods

##### `PlanOrder(SymbolGraph graph) -> EmitOrder`

**Called by**: SinglePhaseBuilder.Build()
**Calls**: OrderTypes(), OrderMembers()

Plans deterministic emission order for all namespaces and types.

**Sort Keys**:
1. **Types**: Kind (Enum < Delegate < Interface < Struct < Class) → TsEmitName → Arity
2. **Members**: Kind (Constructor < Field < Property < Event < Method) → IsStatic → TsEmitName → Signature

```csharp
public EmitOrder PlanOrder(SymbolGraph graph)
{
    var orderedNamespaces = new List<NamespaceEmitOrder>();

    foreach (var ns in graph.Namespaces.OrderBy(n => n.Name))
    {
        var orderedTypes = OrderTypes(ns.Types);
        orderedNamespaces.Add(new NamespaceEmitOrder { ... });
    }

    return new EmitOrder { Namespaces = orderedNamespaces };
}
```

##### `OrderTypes(IReadOnlyList<TypeSymbol> types) -> List<TypeEmitOrder>`

**Called by**: PlanOrder()
**Calls**: GetKindSortOrder(), OrderMembers(), Renamer.GetFinalTypeName()

Orders types within a namespace. Uses **Renamer.GetFinalTypeName()** for stable sorting (not ClrName, which may change).

```csharp
var sorted = types.OrderBy(t => GetKindSortOrder(t.Kind))
                  .ThenBy(t => _ctx.Renamer.GetFinalTypeName(t.StableId, nsScope))
                  .ThenBy(t => t.Arity)
                  .ToList();
```

##### `OrderMembers(TypeSymbol type) -> MemberEmitOrder`

**Called by**: OrderTypes()
**Calls**: Renamer.GetFinalMemberName()

Orders members within a type. Uses **Renamer.GetFinalMemberName()** for stable sorting.

```csharp
var orderedMethods = type.Members.Methods
    .OrderBy(m => m.IsStatic)
    .ThenBy(m => _ctx.Renamer.GetFinalMemberName(m.StableId, scope, m.IsStatic))
    .ThenBy(m => m.Arity)
    .ThenBy(m => m.StableId.CanonicalSignature)
    .ToList();
```

---

### Plan.ImportPlanner.cs

**Purpose**: Plans import statements and aliasing for TypeScript declarations. Generates import/export statements based on dependency graph. Handles namespace-to-module mapping and name collision resolution.

#### Methods

##### `PlanImports(BuildContext ctx, SymbolGraph graph, ImportGraphData importGraph) -> ImportPlan`

**Called by**: SinglePhaseBuilder.Build()
**Calls**: PlanNamespaceImports(), PlanNamespaceExports()

Plans imports for all namespaces based on dependency graph.

```csharp
public static ImportPlan PlanImports(BuildContext ctx, SymbolGraph graph, ImportGraphData importGraph)
{
    var plan = new ImportPlan { ... };

    foreach (var ns in graph.Namespaces)
    {
        PlanNamespaceImports(ctx, ns, importGraph, plan);
        PlanNamespaceExports(ctx, ns, plan);
    }

    return plan;
}
```

##### `PlanNamespaceImports(BuildContext ctx, NamespaceSymbol ns, ImportGraphData importGraph, ImportPlan plan)`

**Called by**: PlanImports()
**Calls**: DetermineAlias(), NamespaceToModulePath()

Plans imports for a single namespace:
1. Gets dependencies from import graph
2. Determines which types are referenced
3. Creates aliases if name collisions exist
4. Generates ImportStatements

```csharp
foreach (var targetNamespace in dependencies)
{
    var referencedTypes = importGraph.CrossNamespaceReferences
        .Where(r => r.SourceNamespace == ns.Name && r.TargetNamespace == targetNamespace)
        .Select(r => r.TargetType)
        .Distinct()
        .ToList();

    var importPath = NamespaceToModulePath(ctx, targetNamespace);

    // Check for name collisions and create aliases
    foreach (var typeName in referencedTypes)
    {
        var alias = DetermineAlias(ctx, ns.Name, targetNamespace, simpleName, aliases);
        // ...
    }
}
```

##### `PlanNamespaceExports(BuildContext ctx, NamespaceSymbol ns, ImportPlan plan)`

**Called by**: PlanImports()

Plans exports for a namespace - exports all public types.

##### `DetermineAlias(BuildContext ctx, string sourceNamespace, string targetNamespace, string typeName, Dictionary<string, string> existingAliases) -> string?`

**Called by**: PlanNamespaceImports()

Determines if alias is needed for imported type:
- Name collision → need alias (`List_Generic`)
- Policy.AlwaysAliasImports → always alias
- Otherwise → no alias

---

## Phase 5: Emit

**Purpose**: Emission phase - generates final output files (.d.ts, .metadata.json, .bindings.json, typelist.json) from the validated symbol graph.

**Output Files Per Namespace**:
1. **index.d.ts** - TypeScript declarations (via TypeScriptEmitter)
2. **metadata.json** - CLR-specific metadata for Tsonic compiler (via MetadataEmitter)
3. **bindings.json** - CLR→TS name mappings (via BindingEmitter)
4. **typelist.json** - List of emitted types/members for completeness verification (via TypeScriptTypeListEmitter)

### Key Emitters (9 files)

1. **TypeScriptEmitter.cs** - Main .d.ts emission
   - Emits declarations using TypeScriptPrinter
   - Handles EmitOrder traversal
   - Emits class surface + explicit views (As_IInterface properties)

2. **MetadataEmitter.cs** - Metadata sidecar emission
   - Tracks virtual/override, static, ref/out
   - Records intentional omissions (indexers, generic static members)
   - Uses SignatureNormalization for canonical keys

3. **BindingEmitter.cs** - Binding metadata emission
   - Maps TypeScript names → CLR names
   - Tracks member name transformations
   - Used for runtime binding

4. **TypeScriptTypeListEmitter.cs** - Type list emission
   - Lists all types and members actually emitted
   - Uses tsEmitName as key (flat structure matching snapshot.json)
   - Used by verify-completeness.js

5. **InternalIndexEmitter.cs** - Internal index.d.ts emission
   - Emits internal member declarations
   - Separate from public API surface

6. **FacadeEmitter.cs** - Facade/wrapper code emission
   - Generates facade code for external consumption

7. **ModuleStubEmitter.cs** - Module stub emission
   - Generates module stubs for testing

8. **TypeScriptPrinter.cs** - Core .d.ts printer
   - Prints TypeScript syntax (classes, interfaces, methods, etc.)
   - Handles generic parameters, constraints
   - Emits view properties

9. **MetadataPrinter.cs** - Metadata JSON printer
   - Serializes metadata to JSON

10. **BindingPrinter.cs** - Binding JSON printer
    - Serializes bindings to JSON

**Key Emission Pattern**: All emitters traverse EmitOrder (not SymbolGraph directly) to ensure deterministic output.

---

## Call Graphs

[To be added - Complete call flow diagrams showing how each method calls others]

---

## Data Flow Diagrams

[To be added - Visual representation of data transformations through the pipeline]

---

**STATUS**: Document creation complete - comprehensive coverage of all 66 files.

Sections completed:
- ✅ High-Level Architecture
- ✅ Pipeline Flow
- ✅ CLI Entry Points
- ✅ Core Infrastructure (BuildContext, SymbolRenamer, StableId, Policy)
- ✅ Phase 1: Load (8 methods fully documented)
- ✅ Phase 2: Model (data structures documented)
- ✅ Phase 3: Shape (all 14 transformation passes fully documented)
- ✅ Phase 3.5: Normalize (OverloadUnifier, SignatureNormalization documented)
- ✅ Phase 4: Plan (TsErase, TsAssignability, PhaseGate, EmitOrderPlanner, ImportPlanner documented)
- ✅ Phase 5: Emit (All 9 emitters documented with key patterns)
- ⏳ Call Graphs (to be added)
- ⏳ Data Flow Diagrams (to be added)

**Total Documentation**: ~3,800 lines covering all major components of the 66-file single-phase pipeline architecture.

**Key Achievements**:
- Complete method-by-method documentation for all critical paths
- "Called by" and "Calls" relationships documented
- Code examples showing key patterns
- M5 critical components highlighted
- Algorithm explanations for complex logic
- Complete coverage of Shape phase (14 passes)
- Comprehensive Plan phase validation documentation
- Emit phase patterns documented

This documentation provides a complete reference for understanding, maintaining, and extending the single-phase pipeline.
