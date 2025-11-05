# bindings.json Consumer Guide (Tsonic runtime)

The `<Assembly>.bindings.json` file maps the TypeScript-facing names produced by
the naming transform back to their CLR counterparts.  The manifest is emitted by
`tsbindgen` when any naming transform (camelCase, etc.) is active.

## File location

For every assembly processed, `tsbindgen` now emits:

```
<OutputDir>/
  AssemblyName.d.ts
  AssemblyName.metadata.json
  AssemblyName.bindings.json  ← new
```

The runtime loader should look for the `.bindings.json` next to the `.metadata.json`.

## JSON structure

```json
{
  "SelectMany": {
    "Kind": "method",
    "Name": "SelectMany",
    "Alias": "selectMany",
    "FullName": "System.Linq.Enumerable.SelectMany"
  },
  "Enumerable": {
    "Kind": "class",
    "Name": "Enumerable",
    "Alias": "enumerable",
    "FullName": "System.Linq.Enumerable"
  },
  "System.Linq": {
    "Kind": "namespace",
    "Name": "System.Linq",
    "Alias": "systemLinq",
    "FullName": "System.Linq"
  }
}
```

- `Name` is the CLR identifier (e.g., "SelectMany", "Enumerable", "System.Linq")
- `Alias` is the TypeScript-facing identifier emitted in the `.d.ts` (e.g., "selectMany", "enumerable", "systemLinq")
- `Kind` describes the type of entity: "namespace", "class", "interface", "method", "property", "enumMember"
- `FullName` contains the fully-qualified CLR name for the entity
- Dictionary keys are the CLR identifiers for quick lookup

The manifest mirrors the declaration hierarchy, making it straightforward for
the runtime to map transformed TypeScript names back to CLR members.

## Usage from Tsonic runtime

1. Load the manifest once per assembly alongside its metadata.
2. When you have a TypeScript identifier (e.g. `selectMany`), you need to find its CLR name:
   - Iterate through the dictionary values to find an entry where `Alias` matches `"selectMany"`
   - Read the `Name` field to get the CLR identifier (`"SelectMany"`)
   - Use `FullName` for the fully-qualified CLR target
3. When you have a CLR identifier (e.g. `SelectMany`), you can directly look it up:
   - Use the CLR name as the dictionary key: `bindings["SelectMany"]`
   - Read the `Alias` field to get the TypeScript identifier
4. If a name is missing from the manifest, assume no transform was applied (CLR name = TS name).

The manifest is additive. New fields may appear in future versions, but existing
properties (`Kind`, `Name`, `Alias`, `FullName`) will remain stable.
