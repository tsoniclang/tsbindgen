# bindings.json Consumer Guide (Tsonic runtime)

The `<Assembly>.bindings.json` file maps the TypeScript-facing names produced by
the naming transform back to their CLR counterparts.  The manifest is emitted by
`generatedts` when any naming transform (camelCase, etc.) is active.

## File location

For every assembly processed, `generatedts` now emits:

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
  "assembly": "System.Linq",
  "namespaces": [
    {
      "name": "systemLinq",
      "alias": "System.Linq",
      "types": [
        {
          "name": "enumerable",
          "alias": "Enumerable",
          "kind": "class",
          "members": [
            {
              "kind": "method",
              "signature": "selectMany<TSource, TResult>(source: IEnumerable<TSource>, selector: (TSource) => IEnumerable<TResult>)",
              "name": "selectMany",
              "alias": "SelectMany",
              "binding": {
                "assembly": "System.Linq",
                "type": "System.Linq.Enumerable",
                "member": "SelectMany"
              }
            }
          ]
        }
      ]
    }
  ]
}
```

- `name` is the identifier emitted in the `.d.ts`; `alias` is the CLR identifier.
- `binding` contains the fully-qualified CLR target the runtime should call.
- `signature` is optional and may be omitted when not needed.

The manifest mirrors the declaration hierarchy, making it straightforward for
the runtime to map transformed TypeScript names back to CLR members.

## Usage from Tsonic runtime

1. Load the manifest once per assembly alongside its metadata.
2. When resolving a TypeScript identifier (e.g. `selectMany`), walk the
   namespace/type/member structure to locate the entry and read the CLR binding
   target.
3. If a name is missing from the manifest, fall back to the CLR name (no transform).

The manifest is additive.  New fields (e.g. overload metadata) may appear in the
future, but existing properties (`kind`, `originalName`, `fullName`) will remain
stable.
