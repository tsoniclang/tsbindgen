# Pipeline Overview

This document describes the end–to–end flow that produces `.d.ts`,
`.metadata.json`, and `.dependencies.json` files for a single assembly.  The
primary entry point is the CLI executable defined in `Src/Cli/Program.cs`.

## 1. CLI invocation

`Program.Main` parses options for:

| Option | Purpose |
| --- | --- |
| `assembly-path` | Required path to the target `.dll` |
| `--namespaces/-n` | Optional whitelist for namespace filtering |
| `--out-dir/-o` | Output directory (defaults to current directory) |
| `--log/-l` | Optional JSON log path |
| `--config/-c` | Optional configuration JSON |
| `--namespace-names` | Transform namespace names (e.g., `camelCase`) |
| `--class-names` | Transform class/struct names (e.g., `camelCase`) |
| `--interface-names` | Transform interface names (e.g., `camelCase`) |
| `--method-names` | Transform method names (e.g., `camelCase`) |
| `--property-names` | Transform property names (e.g., `camelCase`) |
| `--enum-member-names` | Transform enum member names (e.g., `camelCase`) |
| `--binding-names` | Override transform for binding manifest entries |

The CLI loads configuration via `Config/GeneratorConfig.cs`, resolves type
forwarders (`Reflection/TypeForwardingResolver.cs`), and loads the assembly
using either the runtime loader or `MetadataAssemblyLoader` when the reference
pack is required.

## 2. Pipeline orchestration

`Pipeline/AssemblyProcessor.ProcessAssembly` performs the in-memory
transformation.  The steps are:

1. Initialise a `DependencyTracker` for cross-assembly references.
2. Configure `TypeMapper` with the current assembly + dependency tracker.
3. Gather exported types, filtering via `Analysis/TypeFilters.ShouldIncludeType`.
4. Group types by namespace.
5. For each type, call `Reflection/TypeDispatcher.ProcessType` which delegates to
   the relevant emitter (enum, interface, static namespace, class).
6. Collect intersection aliases recorded by `Analysis/InterfaceAnalysis`.
7. Aggregate the resulting `NamespaceInfo` records into a `ProcessedAssembly`.
8. **If naming transforms are enabled**: Apply `Analysis/NameTransformApplicator`
   to recursively transform all names and track bindings.

Metadata generation (`ProcessAssemblyMetadata`) runs the same type filtering and
calls `Metadata/MetadataProcessor.ProcessTypeMetadata` to build the metadata
model in parallel with declaration generation.

## 3. Declaration rendering

`Emit/DeclarationRenderer.RenderDeclarations` accepts the `ProcessedAssembly`
and dependency data.  It renders:

- Auto-generated header & intrinsics (`Emit/Writers/IntrinsicsWriter.cs`)
- `import type` statements (`Emit/Writers/ImportWriter.cs`)
- Namespace blocks with classes, interfaces, enums, static namespaces, and
  intersection aliases (`Emit/Writers/TypeWriter.cs`)

## 4. Output

`Program.GenerateDeclarationsAsync` writes:

| File | Source |
| --- | --- |
| `<AssemblyName>.d.ts` | Result of `DeclarationRenderer.RenderDeclarations` |
| `<AssemblyName>.metadata.json` | Serialised `MetadataWriter.WriteMetadataAsync` |
| `<AssemblyName>.dependencies.json` | JSON emitted by `DependencyTracker.ToJson` |
| `<AssemblyName>.bindings.json` | Binding manifest (only if naming transforms applied) |

Logging (when requested) uses `Diagnostics/GenerationLogger.cs` to capture
warnings emitted by `TypeMapper`.

The CLI repeats this process for every requested assembly, allowing callers to
generate declarations for individual assemblies or for full BCL sets.
