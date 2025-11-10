# tsbindgen Specification Index

This directory defines the **contracts** exposed by tsbindgen:
- CLI interface (commands, flags, options)
- Output formats (file structure, JSON schemas)
- Validation requirements

**Note**: Architecture and implementation details are documented separately.

## Specifications

| Document | Purpose |
| --- | --- |
| [cli.md](cli.md) | Command-line interface, flags, and options |
| [output-layout.md](output-layout.md) | Directory structure and file organization |
| [metadata.md](metadata.md) | Metadata JSON schema (`*.metadata.json`) |
| [bindings-consumer.md](bindings-consumer.md) | Bindings JSON schema (`*.bindings.json`) |
| [scopes.md](scopes.md) | Renaming scope format and API contracts |
| [ref-path.md](ref-path.md) | External package resolution via `--ref-path` |
| [validation.md](validation.md) | Validation expectations and success criteria |

## Quick Reference

### CLI
```bash
# Generate BCL declarations (auto-detects bundle mode)
tsbindgen --assemblies /path/to/bcl/**/*.dll --out-dir ./output

# Generate user assembly with external references
tsbindgen --assemblies MyApp.dll \
  --ref-path ./node_modules \
  --external-map external-map.json \
  --out-dir ./output
```

### Output Structure
```
output/
  _support/
    types.d.ts              # Unsafe CLR markers (TSUnsafePointer, TSByRef)
  System/
    internal/
      index.d.ts            # Internal declarations
    index.d.ts              # Facade (re-exports)
    metadata.json           # CLR semantics
    bindings.json           # Name mappings
    typelist.json           # Emitted types (verification)
```

### Key Schemas

**Metadata** (`*.metadata.json`):
- CLR semantics: virtual/override, accessibility, static
- Used by Tsonic compiler for C# emission

**Bindings** (`*.bindings.json`):
- CLR name → TypeScript name mappings
- Used when naming transforms are applied

**Type List** (`*.typelist.json`):
- Flat list of all emitted types/members
- Used for completeness verification
