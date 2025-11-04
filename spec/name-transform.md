# Name Transform Feature (generatedts implementation)

This document explains how `generatedts` must implement the optional naming
transform that produces camelCase (or other conventions) in the emitted
`.d.ts` while preserving CLR names for runtime consumption.

## Motivation

TypeScript developers often expect JS-style identifiers (e.g. `selectMany`
instead of `SelectMany`).  We want to support this without losing the original
CLR name, so the runtime emitter still knows which C# member to call.

## CLI additions

Extend `Cli/Program.cs` to accept the following optional switches.  Each switch
maps to a `NameTransformOption` in `GeneratorConfig`.

| Option | Applies to |
| --- | --- |
| `--namespace-names` | Namespace declarations/bindings |
| `--type-names` | Classes, structs, interfaces, enums |
| `--method-names` | Instance + static methods |
| `--property-names` | Properties (instance + static) |
| `--enum-member-names` | Enum members |
| `--binding-names` | Overrides the key in bindings (optional) |

For the first iteration, support `camelCase` and `none` (default).  The CLI
should reject unknown values and display a helpful message.

## Data model updates

Add `DeclarationName` records in `Model/Declarations.cs`:

```csharp
public record DeclarationName(string Name, string? Alias = null);
```

Update the declaration models to store both CLR and transformed names, e.g.:

```csharp
public record MethodDeclaration(
    DeclarationName Name,
    IReadOnlyList<string> GenericParameters,
    ...);
```

The CLR name becomes `Name.Alias` (optional), and the TypeScript-facing name is
`Name.Name`.

## Name transform module

Create `Analysis/NameTransform.cs` with helper functions:

```csharp
public static class NameTransform
{
    public static string Apply(NameTransformOption option, string clrName)
    {
        return option switch
        {
            NameTransformOption.CamelCase => CamelCase(clrName),
            _ => clrName,
        };
    }

    private static string CamelCase(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return char.ToLowerInvariant(value[0]) + value.Substring(1);
    }
}
```

The `CamelCase` helper can be expanded later to handle acronyms/underscores if
needed.

## Pipeline changes

In `Pipeline/AssemblyProcessor.ProcessAssembly`:

1. After the existing analysis (diamond handling, covariance, overloads), call a
   new method `ApplyNameTransforms` that walks the `NamespaceInfo` tree and
   updates each `DeclarationName` using `NameTransform.Apply` based on the CLI
   options stored in `GeneratorConfig`.
2. Ensure the original CLR name is preserved in `Alias` so metadata and bindings
   can reference it.

Pseudocode:

```csharp
private NamespaceInfo TransformNamespace(NamespaceInfo ns)
{
    var newName = new DeclarationName(
        NameTransform.Apply(_config.NamespaceNameTransform, ns.Name.Name),
        ns.Name.Alias ?? ns.Name.Name);

    return ns with
    {
        Name = newName,
        Types = ns.Types.Select(TransformType).ToList()
    };
}
```

## Renderer updates

- Update emitters (`ClassEmitter`, `InterfaceEmitter`, etc.) to fill the new
  `DeclarationName` structure when creating declarations.
- Modify writers (e.g. `Emit/Writers/TypeWriter.cs`) to render
  `Name.Name` instead of raw strings.
- When referencing a type/member (implements clauses, intersection aliases), use
  the transformed name.

## Metadata

- Metadata should continue to use the CLR names (`Name.Alias` when set).  No
  change required in `MetadataProcessor` other than reading the alias where
  available.

## Bindings manifest

- Create a new emitter `Emit/BindingWriter.cs` that walks the transformed
  declaration model and produces `<Assembly>.bindings.json` per the structure
  described in `spec/bindings-consumer.md`.
- Each entry should include:
  - `name`: the transformed identifier used in TypeScript
  - `alias`: the CLR identifier (original name)
  - `binding`: `{ "assembly": ..., "type": ..., "member": ... }`
  - Optionally `kind` (`class`, `interface`, `method`, `property`, etc.) and
    `signature` for tooling.

Example snippet:

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

## Validation

1. Extend `Scripts/validate.js` with a test run that enables one transform
   (e.g. `--method-names camelCase`) so we have a golden baseline.
2. Update unit tests or add new ones for `NameTransform.Apply` and the binding
   writer.
3. Document the new CLI options in `README.md` and `spec/cli.md` (if present).

Following this spec will allow callers to opt into JavaScript-style names while
the runtime still calls the correct CLR methods.
