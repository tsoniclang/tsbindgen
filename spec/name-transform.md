# Name Transform Feature

The generator supports optional naming conventions so that the emitted
`.d.ts` files can follow JavaScript / TypeScript style (e.g. camelCase) while the
runtime still references CLR members by their original names.

## CLI support

`Cli/Program.cs` recognises the following switches; each maps to a
`NameTransformOption` stored in `GeneratorConfig`.

| Option | Scope | Values |
| --- | --- | --- |
| `--namespace-names` | Namespace declarations | `none` (default), `camelCase` |
| `--type-names` | Classes, structs, interfaces, enums | `none`, `camelCase` |
| `--method-names` | Instance/static methods | `none`, `camelCase` |
| `--property-names` | Properties | `none`, `camelCase` |
| `--enum-member-names` | Enum members | `none`, `camelCase` |
| `--binding-names` | Override for binding dictionary keys | `none`, `camelCase` |

Unsupported values are rejected with a validation error.

## Transformation behaviour

- `Analysis/NameTransform.Apply` converts CLR identifiers to the requested
  casing.  The current implementation handles common PascalCase, single-letter
  identifiers, and leading acronyms (`XMLParser` → `xmlParser`).  Identifiers
  already in camelCase are returned unchanged.
- `Analysis/NameTransformApplicator` walks the `ProcessedAssembly` tree *after*
  all other analysis steps (diamond handling, overload insertion, etc.).  It
  rewrites namespace/type/member names in-place.
- Every transformed identifier is recorded in a binding map so the runtime can
  recover the CLR name.

## Binding dictionary (`<Assembly>.bindings.json`)

`Cli/Program.GenerateDeclarationsAsync` serialises the collected bindings to
`<Assembly>.bindings.json`.  The file mirrors the declaration hierarchy and
includes both the transformed name (`name`) and the CLR identifier (`alias`).
Example:

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
              "signature": "selectMany<...>",
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

When no transforms are requested the applicator returns no entries and the file
is omitted.

## Interaction with other artefacts

- Declaration emitters (`Emit/...`) operate on simple strings, so they
  automatically pick up the transformed names once the applicator has run.
- Metadata (`Metadata/MetadataProcessor`) is generated *before* the transform
  phase and therefore retains CLR names.  The runtime uses metadata for
  semantics and the bindings dictionary for name resolution.
- Dependency tracking continues to use CLR names; the binding dictionary is
  purely an alias map layered on top.

This behaviour allows consumers to opt in to JS-style naming while the Tsonic
runtime can always restore the original CLR identifiers when emitting C#.
