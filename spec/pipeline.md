# Pipeline Overview

This document describes the two-phase pipeline that generates TypeScript declarations, metadata, and bindings from .NET assemblies.

## Architecture

tsbindgen uses a **two-phase pipeline** that separates reflection from rendering:

1. **Phase 1 (Snapshot)**: Reflects over assemblies and captures everything into canonical JSON snapshots
2. **Phase 2 (Views)**: Reads snapshots and generates various output formats (namespace bundles, ambient declarations, module entry points)

This separation enables:
- Type forwarding resolution (types grouped by namespace, not assembly)
- Multiple packaging surfaces from the same data
- Fast regeneration of outputs without re-running reflection
- Clean architecture (reflection → IR → rendering)

## Directory Layout

```
out/
├── assemblies/
│   ├── System.Linq.snapshot.json
│   ├── System.Linq.Expressions.snapshot.json
│   └── assemblies-manifest.json
├── namespaces/
│   ├── System.Linq/
│   │   ├── index.d.ts
│   │   ├── metadata.json
│   │   ├── bindings.json
│   │   └── index.js
│   ├── System.IO/
│   │   └── ...
│   └── namespaces-manifest.json
├── ambient/
│   └── global.d.ts
└── modules/
    ├── dotnet/
    │   └── system/
    │       └── linq/
    │           ├── index.d.ts
    │           └── index.js
    └── runtime/
        └── fs/
            ├── index.d.ts
            └── index.js
```

## Phase 1: Assembly Snapshot

### CLI Invocation

```bash
tsbindgen generate --assembly path/to/System.Linq.dll --out-dir out/
tsbindgen generate --assembly-dir path/to/ref/net10.0 --out-dir out/
```

### Options

| Option | Purpose |
| --- | --- |
| `--assembly` | Path to a single assembly (repeatable) |
| `--assembly-dir` | Directory containing assemblies to process |
| `--out-dir` | Output directory (required) |
| `--namespaces` | Optional namespace filter |
| `--namespace-names` | Transform namespace names (e.g., `camelCase`) |
| `--class-names` | Transform class names |
| `--interface-names` | Transform interface names |
| `--method-names` | Transform method names |
| `--property-names` | Transform property names |
| `--enum-member-names` | Transform enum member names |

### Process

For each assembly:

1. Load assembly using `MetadataAssemblyLoader` or runtime loader
2. Resolve type forwarding using `TypeForwardingResolver`
3. Process types using existing `AssemblyProcessor`:
   - Filter types via `TypeFilters`
   - Dispatch to emitters (class, interface, enum, etc.)
   - Apply naming transforms via `NameTransformApplicator`
   - Track dependencies via `DependencyTracker`
4. Serialize complete snapshot to `assemblies/<Assembly>.snapshot.json`
5. Write `assemblies/assemblies-manifest.json`

### Snapshot Schema

See [snapshot.md](snapshot.md) for the complete schema. Key sections:

- **Header**: assembly name, path, timestamp, type forwarding targets
- **Namespaces**: array of namespaces with types, imports, diagnostics
- **Types**: full type information (members, generics, base types, interfaces)
- **Members**: methods, properties, constructors with all metadata
- **Bindings**: CLR name → TypeScript alias mappings
- **Diagnostics**: warnings and errors from analysis

## Phase 2: View Generation

Phase 2 consumes **only** the snapshot files—no reflection occurs.

### Process

1. **Load snapshots**: Read all files from `assemblies/`
2. **Aggregate by namespace**: Group types from all assemblies by their logical namespace
3. **Merge data**: Combine types, dependencies, bindings, diagnostics
4. **Generate views**:
   - Namespace bundles (`namespaces/`)
   - Ambient declarations (`ambient/`)
   - Module entry points (`modules/`)

### Namespace Bundles

For each namespace (e.g., `System.Linq`):

**namespaces/System.Linq/index.d.ts**
- TypeScript declarations rendered from snapshot data
- `import type` statements for cross-namespace dependencies

**namespaces/System.Linq/metadata.json**
- CLR semantics (virtual, override, static, visibility)
- Same schema as before for compatibility

**namespaces/System.Linq/bindings.json**
- CLR name → TypeScript alias mappings
- Hierarchical structure mirroring declarations
- Schema: `{ "CLRName": { "Kind": "...", "Name": "CLRName", "Alias": "tsAlias", "FullName": "..." } }`

**namespaces/System.Linq/index.js** (optional)
- Module stub for runtime consumers
- Re-exports namespace types

**namespaces/namespaces-manifest.json**
- Summary of all namespaces, type counts, source assemblies

### Ambient Output

**ambient/global.d.ts**
- Global augmentation referencing namespace bundles
- Triple-slash references or import statements
- Example:
  ```typescript
  /// <reference path="../namespaces/System.Linq/index.d.ts" />
  declare global {
    namespace Tsonic.Runtime {
      export import Linq = System.Linq;
    }
  }
  ```

### Module Entry Points

**modules/dotnet/system/linq/** (.NET scoped modules)
- `index.d.ts` and `index.js`
- Re-export from `../../../../namespaces/System.Linq/`
- Enables `import { IQueryable } from "@tsonic/dotnet/system/linq"`

**modules/runtime/fs/** (Runtime/Node modules)
- `index.d.ts` and `index.js`
- Re-export from namespace bundles (e.g., `System.IO`)
- Enables `import { writeFile } from "@tsonic/node/fs"`

## Code Organization

### Phase 1 Components

| Component | Responsibility |
| --- | --- |
| `Snapshot/AssemblySnapshotWriter` | Serializes ProcessedAssembly to snapshot JSON |
| `Pipeline/AssemblyProcessor` | Orchestrates reflection and analysis |
| `Analysis/*` | Type filtering, transforms, metadata extraction |
| `Reflection/*` | Assembly loading, type forwarding resolution |

### Phase 2 Components

| Component | Responsibility |
| --- | --- |
| `Views/NamespaceAggregator` | Merges snapshots by namespace |
| `Views/NamespaceBundleRenderer` | Generates namespace bundle files |
| `Views/AmbientRenderer` | Generates global.d.ts |
| `Views/ModuleRenderer` | Generates module entry points |
| `Emit/DeclarationRenderer` | Renders TypeScript declarations (reused) |
| `Emit/MetadataWriter` | Writes metadata JSON (reused) |

### CLI

| Component | Responsibility |
| --- | --- |
| `Cli/Commands/GenerateCommand` | New `generate` command handler |
| `Cli/Program` | Entry point, command routing |

## Benefits

1. **Type Forwarding Resolved**: Types grouped by namespace, not assembly
2. **Multiple Surfaces**: Generate ambient, modules, packages from one snapshot
3. **Fast Iteration**: Regenerate views without reflection
4. **Clean Architecture**: Reflection separate from rendering
5. **Deterministic Output**: Same snapshots always produce same views
6. **Extensible**: Add new view types without touching Phase 1

## Migration from Old Pipeline

The old per-assembly pipeline (`tsbindgen <assembly.dll>`) is **removed**. All functionality is now in `tsbindgen generate`.

Old outputs can be approximated by using namespace bundles, but the canonical approach is to use the new namespace-centric layout.
