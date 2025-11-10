# SinglePhase Pipeline Architecture Documentation

**Complete architectural documentation for the tsbindgen SinglePhase pipeline.**

## Documentation Index

### Core Architecture

- **[00-overview.md](00-overview.md)** - System overview, principles, objectives, BuildContext
- **[01-pipeline-flow.md](01-pipeline-flow.md)** - Phase sequence, data transformations, pipeline diagram

### Pipeline Phases (In Execution Order)

- **[02-phase-load.md](02-phase-load.md)** - Phase 1: Reflection and assembly loading
  - AssemblyLoader, ReflectionReader, TypeReferenceFactory, InterfaceMemberSubstitutor
  - Complete method documentation for all Load files

- **[03-phase-model.md](03-phase-model.md)** - Data structures (SymbolGraph, TypeSymbol, MemberSymbols, TypeReference)
  - All records and properties explained
  - StableId system, canonical signatures, EmitScope

- **[04-phase-shape.md](04-phase-shape.md)** - Phase 3: 14 transformation passes
  - GlobalInterfaceIndex through ConstraintCloser
  - Complete method documentation for all 16 Shape files
  - Transformation examples and pass ordering

- **[05-phase-normalize.md](05-phase-normalize.md)** - Phase 3.5: Name reservation and overload unification
  - NameReservation orchestration
  - Dual-scope naming algorithm (class vs view)
  - Collision detection and resolution
  - Complete method documentation for all Normalize files

- **[06-phase-plan.md](06-phase-plan.md)** - Phase 4: Import planning and validation setup
  - ImportPlanner, ImportGraph, EmitOrderPlanner
  - InterfaceConstraintAuditor, TsAssignability, TsErase
  - PhaseGate overview (detailed doc separate)

- **[07-phasegate.md](07-phasegate.md)** - **SUPER DETAILED** PhaseGate validation
  - **ALL 50+ validation rules** documented
  - **ALL 43 diagnostic codes** (TBG001-TBG883) with complete reference table
  - All 10 validation module files documented with every method
  - Diagnostic output formats (summary.json, diagnostics.txt)
  - This is the most comprehensive document

- **[08-phase-emit.md](08-phase-emit.md)** - Phase 5: File generation
  - FacadeEmitter, InternalIndexEmitter, MetadataEmitter
  - BindingEmitter, ModuleStubEmitter, SupportTypesEmitter
  - All Printer classes (ClassPrinter, MethodPrinter, TypeRefPrinter)
  - Output format examples for all 6 file types

### Infrastructure

- **[09-renaming.md](09-renaming.md)** - Complete renaming system
  - SymbolRenamer: Central naming authority
  - RenameScope, ScopeFactory: Scope identification
  - StableId, RenameDecision: Identity and decision records
  - NameReservationTable: Internal storage
  - Dual-scope algorithm, collision handling, reservation examples

- **[10-call-graphs.md](10-call-graphs.md)** - Complete call chains
  - Entry point through all phases
  - Who calls what (function → function)
  - Cross-cutting calls (Renamer, Diagnostics, Policy)
  - Complete end-to-end example (List&lt;T&gt; from CLI to file)

## Reading Guide

### For New Developers

**Start here:**
1. **00-overview.md** - Understand the big picture
2. **01-pipeline-flow.md** - See how phases connect
3. **10-call-graphs.md** - Trace execution flow

**Then dive into specific phases as needed.**

### For Understanding Validation

**PhaseGate is the key:**
- **07-phasegate.md** contains EVERY validation rule with examples
- All 43 diagnostic codes documented
- Shows what can go wrong and how it's detected

### For Understanding Naming

**Renaming system is critical:**
- **09-renaming.md** explains dual-scope naming
- Shows how class surface and view surface names are kept separate
- Collision detection and resolution algorithms

### For Understanding Transformations

**Shape phase is complex:**
- **04-phase-shape.md** documents all 14 passes
- Shows why pass ordering matters
- Examples of CLR → TypeScript transformations

### For Understanding Code Generation

**Emit phase produces output:**
- **08-phase-emit.md** shows how files are generated
- All output formats documented with examples
- Printer architecture explained

## Key Concepts Reference

### StableId
Assembly-qualified identifiers that uniquely identify types and members:
- **TypeStableId**: `AssemblyName::Namespace.TypeName\`Arity`
- **MemberStableId**: `DeclaringClrFullName::CanonicalSignature (MetadataToken)`
- Documented in: **03-phase-model.md**, **09-renaming.md**

### EmitScope
Placement decisions for members:
- **ClassSurface**: Emit on class directly
- **ViewOnly**: Emit only in view (explicit interface implementation)
- **Omitted**: Don't emit (intentionally skipped)
- **Unspecified**: Invalid state (caught by PhaseGate)
- Documented in: **03-phase-model.md**, **04-phase-shape.md**, **07-phasegate.md**

### Dual-Scope Naming
Separate naming scopes to avoid collisions:
- **Class Surface**: Direct members on the class
- **View Surface**: Interface implementation members
- Static vs instance kept separate within each surface
- Documented in: **05-phase-normalize.md**, **09-renaming.md**

### ViewPlanner
Explicit interface implementation support:
- Synthesizes `As_IInterfaceName` properties
- ViewOnly members with `$view` suffix if needed
- Enables TypeScript to access interface members
- Documented in: **04-phase-shape.md**, **05-phase-normalize.md**

### PhaseGate
Final validation before emission:
- 50+ validation rules checking invariants
- 43 diagnostic codes (TBG001-TBG883)
- Stops emission if errors found
- Comprehensive documentation in **07-phasegate.md**

## Statistics

- **76 source files** in SinglePhase/ directory
- **11 architecture documents** (this collection)
- **50+ validation rules** in PhaseGate
- **43 diagnostic codes** (TBG001-TBG883)
- **14 transformation passes** in Shape phase
- **6 output file types** (d.ts, json, js)

## Document Sizes

| Document | Lines | Focus |
|----------|-------|-------|
| 00-overview.md | ~850 | System overview |
| 01-pipeline-flow.md | ~650 | Phase sequence |
| 02-phase-load.md | ~1,400 | Load phase |
| 03-phase-model.md | ~1,500 | Data structures |
| 04-phase-shape.md | ~3,000 | Shape transformations |
| 05-phase-normalize.md | ~1,800 | Name reservation |
| 06-phase-plan.md | ~1,900 | Import planning |
| 07-phasegate.md | ~3,500 | **SUPER DETAILED validation** |
| 08-phase-emit.md | ~2,900 | File generation |
| 09-renaming.md | ~2,000 | Naming system |
| 10-call-graphs.md | ~1,800 | Call chains |
| **Total** | **~21,300 lines** | Complete architecture |

## Coverage

This documentation covers:
- ✅ Every file in SinglePhase/ (76 files)
- ✅ Every public method with full signatures
- ✅ Every private method with algorithms
- ✅ Every validation rule (50+)
- ✅ Every diagnostic code (43 codes)
- ✅ Every transformation pass (14 passes)
- ✅ Complete call chains (entry to output)
- ✅ All data structures (records, enums)
- ✅ Key algorithms (dual-scope naming, collision detection, etc.)

## Navigation

**By Phase:**
- Phase 1 (Load): [02-phase-load.md](02-phase-load.md)
- Phase 2 (Model): [03-phase-model.md](03-phase-model.md)
- Phase 3 (Shape): [04-phase-shape.md](04-phase-shape.md)
- Phase 3.5 (Normalize): [05-phase-normalize.md](05-phase-normalize.md)
- Phase 4 (Plan): [06-phase-plan.md](06-phase-plan.md)
- Phase 4.7 (PhaseGate): [07-phasegate.md](07-phasegate.md)
- Phase 5 (Emit): [08-phase-emit.md](08-phase-emit.md)

**By Topic:**
- Infrastructure: [00-overview.md](00-overview.md)
- Naming: [09-renaming.md](09-renaming.md)
- Validation: [07-phasegate.md](07-phasegate.md)
- Call Flow: [10-call-graphs.md](10-call-graphs.md)

---

**Note**: This documentation describes the **SinglePhase pipeline** only. The old two-phase pipeline (snapshot → render) is deprecated and not documented here.
