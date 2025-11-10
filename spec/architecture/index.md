# tsbindgen Architecture - SinglePhase Pipeline

This directory contains comprehensive architecture documentation for the tsbindgen SinglePhase pipeline.

## System Overview

tsbindgen is a .NET tool that generates TypeScript declaration files (.d.ts) and metadata sidecars (.metadata.json) from .NET assemblies using reflection. It enables TypeScript code in the Tsonic compiler to reference .NET BCL types with full IDE support and type safety.

## Documentation Organization

| Document | Purpose |
|----------|---------|
| [overview.md](overview.md) | System objectives, architectural principles, key design decisions |
| [pipeline.md](pipeline.md) | Pipeline flow, phase sequence, data transformations |
| [build-context.md](build-context.md) | BuildContext and shared services (Renamer, Diagnostics, Policy) |
| [phase-1-load.md](phase-1-load.md) | Load phase: Assembly loading, reflection, type reference creation |
| [phase-2-normalize.md](phase-2-normalize.md) | Normalize phase: Indexing, signature normalization, name reservation |
| [phase-3-shape.md](phase-3-shape.md) | Shape phase: 14 transformation passes for TypeScript compatibility |
| [phase-4-plan.md](phase-4-plan.md) | Plan phase: Import planning, emission ordering, PhaseGate validation |
| [phasegate.md](phasegate.md) | PhaseGate: Comprehensive validation with all error codes |
| [phase-5-emit.md](phase-5-emit.md) | Emit phase: File generation, printers, emitters |
| [model.md](model.md) | Model infrastructure: SymbolGraph, TypeSymbol, type references |
| [renaming.md](renaming.md) | Renaming infrastructure: SymbolRenamer, StableId, scope management |

## Pipeline Phases

The SinglePhase pipeline executes in strict sequential order:

```
Phase 1: LOAD
  Input:  Assembly file paths (string[])
  Output: SymbolGraph (pure CLR facts)
  Files:  4 (AssemblyLoader, ReflectionReader, TypeReferenceFactory, InterfaceMemberSubstitutor)

Phase 2: NORMALIZE
  Input:  SymbolGraph
  Output: SymbolGraph (with indices and normalized signatures)
  Files:  3 (NameReservation, SignatureNormalization, OverloadUnifier)

Phase 3: SHAPE
  Input:  SymbolGraph
  Output: SymbolGraph (transformed for TypeScript)
  Files:  14 transformation passes

Phase 4: PLAN
  Input:  SymbolGraph
  Output: EmissionPlan (validated, ordered)
  Files:  8 (ImportGraph, ImportPlanner, EmitOrderPlanner, PhaseGate, etc.)

Phase 5: EMIT
  Input:  EmissionPlan
  Output: File system writes (.d.ts, .json, .js)
  Files:  11 (emitters and printers)
```

## Key Statistics

- **Total Files**: 62 C# files
- **Main Entry**: `SinglePhaseBuilder.cs`
- **Pipeline Phases**: 5 sequential phases
- **Shape Passes**: 14 transformation passes
- **Validation Checks**: 26 validation methods
- **Error Codes**: 24 diagnostic codes (PG_*)
- **Model Records**: 20+ core symbol types

## Quick Navigation

### Core Infrastructure
- [BuildContext](build-context.md) - Central services and configuration
- [SymbolGraph](model.md#symbolgraph) - Core data structure
- [SymbolRenamer](renaming.md#symbolrenamer) - Naming authority

### Pipeline Entry Points
- [SinglePhaseBuilder.Build()](pipeline.md#singlephasebuilder) - Main orchestrator
- [PhaseGate.Validate()](phasegate.md#validate-method) - Quality gate

### Key Transformations
- [InterfaceInliner](phase-3-shape.md#interfaceinliner) - Flatten interface hierarchies
- [ViewPlanner](phase-3-shape.md#viewplanner) - Plan explicit interface views
- [ClassSurfaceDeduplicator](phase-3-shape.md#classsurfacededuplicator) - Resolve name collisions

### Validation
- [PhaseGate Error Codes](phasegate.md#error-codes) - All 24 diagnostic codes
- [Validation Flow](phasegate.md#validation-flow) - Check execution order

## Design Principles

1. **Pure Functional Architecture**: Immutable data structures, pure functions, no side effects (except I/O)
2. **Single-Pass Processing**: One continuous pipeline from reflection to emission
3. **StableId-Based Identity**: Assembly-qualified identifiers ensure cross-assembly correctness
4. **Centralized Naming Authority**: All TypeScript identifiers flow through SymbolRenamer
5. **Scope-Based Naming**: Separate scopes for class surface, views, static/instance members
6. **Comprehensive Validation**: PhaseGate ensures correctness before emission

## Data Flow Summary

```
Assembly DLLs
    ↓
[LOAD] Reflection via MetadataLoadContext
    ↓
SymbolGraph (CLR facts: types, members, signatures)
    ↓
[NORMALIZE] Build indices, normalize signatures, reserve names
    ↓
SymbolGraph (with indices and canonical signatures)
    ↓
[SHAPE] 14 transformation passes
    ↓
SymbolGraph (TypeScript-ready)
    ↓
[PLAN] Import planning, ordering, validation
    ↓
EmissionPlan (validated, ordered, ready to emit)
    ↓
[EMIT] Generate .d.ts, .metadata.json, .bindings.json
    ↓
Output Files
```

## Reading Order

For understanding the system:

1. Start with [overview.md](overview.md) for context and objectives
2. Read [pipeline.md](pipeline.md) for high-level flow
3. Understand [build-context.md](build-context.md) for shared services
4. Follow phases sequentially: [phase-1-load.md](phase-1-load.md) → [phase-2-normalize.md](phase-2-normalize.md) → [phase-3-shape.md](phase-3-shape.md) → [phase-4-plan.md](phase-4-plan.md) → [phase-5-emit.md](phase-5-emit.md)
5. Dive into [phasegate.md](phasegate.md) for validation details
6. Reference [model.md](model.md) and [renaming.md](renaming.md) for infrastructure

For debugging issues:

1. Check [phasegate.md](phasegate.md) for error code explanations
2. Review the relevant phase documentation for transformation details
3. Examine [renaming.md](renaming.md) for naming-related issues

## Architectural Highlights

### Immutability

All core data structures are immutable records:
- `SymbolGraph` - Top-level graph
- `TypeSymbol` - Type representation
- `MethodSymbol`, `PropertySymbol` - Member representations
- Transformations create new copies via wither methods

### Static Classes

All logic implemented as static classes with pure functions:
- No instance state
- Input → Output transformations
- Side effects only in Emit phase

### Centralized Services

BuildContext provides shared services:
- **SymbolRenamer**: Naming authority (all identifiers)
- **DiagnosticBag**: Error/warning collection
- **Policy**: Configuration and behavior control
- **Interner**: String deduplication

### Scope Management

SymbolRenamer uses separate scopes:
- **Namespace scopes**: Public vs internal type names
- **Class scopes**: Instance vs static members
- **View scopes**: Interface-specific members

### Stable Identifiers

- `TypeStableId`: `AssemblyName:ClrFullName`
- `MemberStableId`: `AssemblyName:DeclaringType::MemberName{CanonicalSignature}`
- Used as keys for rename decisions and cross-assembly references

### Validation Strategy

PhaseGate runs 26 validation methods in strict order:
- 8 core validations (types, members, generics, interfaces, inheritance, scopes, imports, policy)
- 18 hardening validations (M1-M10 phases with specific error codes)
- All diagnostics recorded with PG_* error codes

## Metrics (Recent BCL Generation)

- **130 BCL namespaces** generated
- **4,047 types** emitted
- **Zero syntax errors** (TS1xxx)
- **12 semantic errors** (TS2417 - property covariance, expected)
- **100% type coverage** - All reflected types accounted for
- **241 indexers** intentionally omitted (tracked in metadata)
