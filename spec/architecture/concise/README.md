# Architecture Documentation (Concise)

Compressed architecture reference for tsbindgen. Each file is 300-400 lines covering one major aspect.

## Contents

| File | Topic | Lines |
|------|-------|-------|
| [00-overview.md](00-overview.md) | Project overview, pipeline, design principles | ~350 |
| [01-reflection.md](01-reflection.md) | Phase 1: Reflection pipeline, MetadataLoadContext | ~400 |
| [02-snapshot.md](02-snapshot.md) | Phase 2: Aggregation, namespace bundling | ~300 |
| [03-transform.md](03-transform.md) | Phase 3: CLR→TS transform, name mapping | ~400 |
| [04-emit.md](04-emit.md) | Phase 4: TypeScript/metadata emission | ~350 |
| [05-type-mapping.md](05-type-mapping.md) | Type system mapping, branded types, covariance | ~400 |
| [06-validation.md](06-validation.md) | Validation system, PhaseGate, 50+ rules | ~400 |
| [07-diagnostics.md](07-diagnostics.md) | 43 diagnostic codes, severity levels | ~350 |
| [08-verification.md](08-verification.md) | Completeness verification, data integrity | ~300 |
| [09-codebase.md](09-codebase.md) | File organization, 76 files mapped | ~400 |
| [10-workflows.md](10-workflows.md) | Development workflows, debugging | ~350 |

**Total: ~4,000 lines** (vs. 15,000+ in original docs)

## Quick Start

**New developers:**
1. Read 00-overview.md for pipeline and principles
2. Read 09-codebase.md for file navigation
3. Read specific phase docs (01-04) as needed

**Fixing bugs:**
1. Check 07-diagnostics.md for error codes
2. Check phase docs for affected component
3. Check 06-validation.md for validation rules

**Adding features:**
1. Read 00-overview.md for design constraints
2. Read relevant phase docs (01-04)
3. Update 06-validation.md if adding rules

## Key Concepts

### Pipeline Phases
- **Phase 1 (Reflection)**: .NET DLL → AssemblySnapshot (CLR only) → `01-reflection.md`
- **Phase 2 (Aggregation)**: Multiple snapshots → NamespaceBundle → `02-snapshot.md`
- **Phase 3 (Transform)**: CLR → NamespaceModel (adds TsEmitName) → `03-transform.md`
- **Phase 4 (Emit)**: Model → .d.ts/.metadata.json → `04-emit.md`

### Core Systems
- **Type Mapping**: CLR types → TS types, branded primitives → `05-type-mapping.md`
- **Validation**: 50+ rules, PhaseGate guards → `06-validation.md`
- **Diagnostics**: 43 codes, ERROR/WARNING/INFO → `07-diagnostics.md`
- **Verification**: 100% data integrity checks → `08-verification.md`

### Name Transformation
- **CLR Name**: `List`1` (backtick for arity) → `03-transform.md`
- **TS Emit Name**: `List_1` (underscore for arity) → `03-transform.md`
- Created in Phase 3 via `NameTransformation.Apply()` → `03-transform.md`

### Critical Constraints
- **No typeof()**: MetadataLoadContext requires name comparisons → `01-reflection.md`
- **No weakening**: Type safety must not degrade → `05-type-mapping.md`
- **No data loss**: 100% completeness required → `08-verification.md`
- **Functional style**: Pure functions, immutable data → `00-overview.md`

### Output Files
- **index.d.ts**: TypeScript declarations → `04-emit.md`
- **metadata.json**: CLR-specific data for Tsonic → `04-emit.md`
- **bindings.json**: CLR name → TS name mappings → `04-emit.md`
- **typelist.json**: Emitted types for verification → `08-verification.md`
- **snapshot.json**: Post-transform state for verification → `08-verification.md`

### Known Limitations
- **Property covariance**: 12 TS2417 errors, C#/TS mismatch → `05-type-mapping.md`
- **Generic statics**: TypeScript doesn't support → `05-type-mapping.md`
- **Indexers**: Duplicate identifiers, omitted → `05-type-mapping.md`

## Statistics

### Codebase
- **76 C# files** organized into 8 subsystems → `09-codebase.md`
- **4 pipeline phases** with strict boundaries → `00-overview.md`
- **4 emit modules** for different output types → `04-emit.md`

### Validation
- **50+ validation rules** across 7 categories → `06-validation.md`
- **43 diagnostic codes** with severity levels → `07-diagnostics.md`
- **PhaseGate guards** prevent invalid progression → `06-validation.md`

### Output
- **130 BCL namespaces** generated
- **4,047 types** emitted
- **75,977 members** reflected
- **37,863 members** emitted (after filtering)
- **241 indexers** intentionally omitted
- **Zero syntax errors** (TS1xxx)
- **12 semantic errors** (TS2417, expected)

## Cross-References

### By Phase
- Phase 1 → `01-reflection.md`, validation → `06-validation.md` (PG_LOAD_*)
- Phase 2 → `02-snapshot.md`, validation → `06-validation.md` (PG_AGG_*)
- Phase 3 → `03-transform.md`, types → `05-type-mapping.md`
- Phase 4 → `04-emit.md`, verification → `08-verification.md`

### By Concern
- **Names**: Transform (`03-transform.md`), Emit (`04-emit.md`)
- **Types**: Mapping (`05-type-mapping.md`), Reflection (`01-reflection.md`)
- **Quality**: Validation (`06-validation.md`), Diagnostics (`07-diagnostics.md`)
- **Integrity**: Verification (`08-verification.md`), Snapshot (`02-snapshot.md`)

### By File Type
- **Models**: Overview (`00-overview.md`), all phase docs (01-04)
- **Validation**: Validation (`06-validation.md`), Diagnostics (`07-diagnostics.md`)
- **Output**: Emit (`04-emit.md`), Verification (`08-verification.md`)
- **Source**: Codebase (`09-codebase.md`), Workflows (`10-workflows.md`)

## Navigation Tips

### Finding Information
- **"How does X work?"** → Check phase docs (01-04) or 00-overview.md
- **"Where is X defined?"** → Check 09-codebase.md file map
- **"Why is X designed this way?"** → Check 00-overview.md principles
- **"What does error X mean?"** → Check 07-diagnostics.md
- **"How do I debug X?"** → Check 10-workflows.md

### Following Data Flow
1. Start: 00-overview.md (pipeline overview)
2. Phase 1: 01-reflection.md (DLL → AssemblySnapshot)
3. Phase 2: 02-snapshot.md (Snapshots → NamespaceBundle)
4. Phase 3: 03-transform.md (Bundle → NamespaceModel, adds TsEmitName)
5. Phase 4: 04-emit.md (Model → output files)
6. End: 08-verification.md (verify integrity)

### Understanding Validation
1. Overview: 06-validation.md (50+ rules, PhaseGate)
2. Codes: 07-diagnostics.md (43 diagnostic codes)
3. Implementation: 09-codebase.md (Validation/* files)
4. Usage: 10-workflows.md (running validation)

### Debugging Workflows
1. Read: 10-workflows.md (standard debugging patterns)
2. Check: 07-diagnostics.md (if error code present)
3. Investigate: Phase docs (01-04) for component details
4. Verify: 08-verification.md (check data integrity)

## Design Principles (Summary)

From `00-overview.md`:

1. **Functional**: Pure functions, immutable data, no state
2. **Type-safe**: No weakening, explicit tracking of omissions
3. **Validated**: 50+ rules, fail early, clear diagnostics
4. **Verified**: 100% data integrity through pipeline
5. **Phased**: Strict phase boundaries, no backflow
6. **Documented**: Every decision tracked in metadata

## Common Patterns

### MetadataLoadContext Usage
```csharp
// ❌ WRONG - typeof() fails with MetadataLoadContext
if (type == typeof(bool)) return "boolean";

// ✅ CORRECT - Name-based comparison
if (type.FullName == "System.Boolean") return "boolean";
```
See: `01-reflection.md`, `05-type-mapping.md`

### Name Transformation
```csharp
// Phase 3 creates TsEmitName
model.TsEmitName = NameTransformation.Apply(type.ClrName);

// Phase 4 uses TsEmitName directly
emit.Append($"class {model.TsEmitName}");
```
See: `03-transform.md`, `04-emit.md`

### Validation Pattern
```csharp
// PhaseGate guard at phase boundary
PhaseGate.EnsureReflectionComplete(snapshot, DiagnosticSink.Instance);

// Rule-based validation
TypeValidation.ValidateNamespaceBundle(bundle, DiagnosticSink.Instance);
```
See: `06-validation.md`, `07-diagnostics.md`

## File Locations

### Documentation
- **This directory**: `spec/architecture/concise/` (11 files)
- **Original docs**: `spec/architecture/` (reference, not maintained)
- **Quick reference**: `CLAUDE.md`, `STATUS.md`, `CODING-STANDARDS.md`

### Source Code
- **Pipeline**: `src/tsbindgen/` (4 phase directories)
- **Validation**: `src/tsbindgen/Validation/` (rules and PhaseGate)
- **Models**: `src/tsbindgen/Models/` (immutable records)

### Scripts
- **Validation**: `scripts/validate.js` (generates and compiles)
- **Verification**: `scripts/verify-completeness.js` (data integrity)

See: `09-codebase.md` for complete file map

## Update Policy

**When to update these docs:**
- Adding new phase or major subsystem → Update relevant phase doc
- Adding validation rules → Update `06-validation.md`
- Adding diagnostic codes → Update `07-diagnostics.md`
- Changing type mapping → Update `05-type-mapping.md`
- Reorganizing files → Update `09-codebase.md`

**Keep line counts stable:**
- Each file: 300-400 lines
- Total: ~4,000 lines
- If growing beyond 400 lines, split into sub-topics

## Related Documentation

- **CLAUDE.md**: AI assistant guidelines, critical rules
- **STATUS.md**: Current metrics, known issues
- **CODING-STANDARDS.md**: C# style, functional programming
- **.analysis/**: Investigation reports, debugging notes
- **spec/architecture/**: Original detailed docs (15,000+ lines)
