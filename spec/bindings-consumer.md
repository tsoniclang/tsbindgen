# bindings.json Consumer Guide (Tsonic runtime)

This document explains how the new `<Assembly>.bindings.json` files emitted by
`generatedts` should be consumed by the Tsonic runtime.  The manifest maps the
TypeScript-facing names to the original CLR members.

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

Field meanings:

- `assembly`: CLR assembly name.
- `namespaces[]`: hierarchy of namespaces.  `name` is the transformed identifier
  that appears in `.d.ts`; `alias` is the CLR namespace.
- `types[]`: per-type entries with transformed name (`name`), CLR alias (`alias`),
  and `kind` (`class`, `interface`, `enum`, `struct`).
- `members[]`: methods/properties recorded under the containing type.
  - `name`: transformed JS/TS identifier.
  - `alias`: CLR member name.
  - `signature`: optional TypeScript signature (for tooling/debugging).
  - `binding`: the CLR target `{ assembly, type, member }` the runtime should call.

If no transform was applied for a particular category the `name` and `alias`
values will match.

## Usage from Tsonic runtime

1. Load the manifest once per assembly alongside its metadata.
2. When resolving a TypeScript identifier (e.g. `selectMany`), walk the manifest:
   - Find the namespace/type by `name` (transformed form).
   - Use `binding.type` + `binding.member` + `binding.assembly` when emitting C#.
3. The runtime should fall back to CLR names when an entry is missing (in case no
   transform was configured).

## Versioning

The manifest is additive.  Future iterations may add new fields (e.g. overload
disambiguators) but existing fields (`name`, `alias`, `binding`) will remain.

This guide is intended for runtime developers so they know how to interpret the
manifest and map JS-friendly names back to CLR members.
