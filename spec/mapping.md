# CLR → TypeScript Mapping Rules

`Src/Mapping/` contains the pure mapping functions used by
`TypeMapper.MapType`.  Each function is designed to be composable and to emit a
string representation while recording dependencies through
`Analysis/DependencyHelpers.TrackTypeDependency`.

## Mapping modules

| File | Responsibility | Key functions |
| --- | --- | --- |
| `TypeMapper.cs` | Orchestrates mapping, exposes `MapType` and `SetContext` | `MapType` |
| `PrimitiveMapping.cs` | Primitive and “well known” system types | `MapPrimitiveType`, `MapSystemType`, `AddWarning` |
| `GenericMapping.cs` | Handles open/closed generic types, `Task`/`Nullable` special cases | `MapGenericType` |
| `ArrayMapping.cs` | Maps array/`Span` shapes to `ReadonlyArray<T>` | handled inside `MapType` |
| `DelegateMapping.cs` | Converts delegates to function signatures | `MapDelegateToFunctionType` |
| `TypeNameHelpers.cs` / `TypeNameMapping.cs` | Canonical name/arity computation for CLR types | `GetTypeName`, `GetFullTypeName` |

## Mapping behaviours

1. **By-ref and pointer types** (`MapType`): unwrap to element type and map the
   element.  Pointers produce `any` and emit a warning because TypeScript does
   not expose unsafe pointers.
2. **Nullable value types**: `Nullable<T>` becomes `MapType(T) | null`.
3. **Arrays**: convert to `ReadonlyArray<element>`, recursing through nested
   arrays.
4. **Delegates**: map to arrow-function signatures by inspecting the `Invoke`
   method (`DelegateMapping.MapDelegateToFunctionType`).  Dependencies are
   recorded for return type and parameters.
5. **Primitive/system types**: direct string substitution handled by
   `MapPrimitiveType`/`MapSystemType`.  Unknown system types fall back to the
   fully-qualified CLR name via `TypeNameMapping`.
6. **Generics**: `MapGenericType` handles both open and closed generics.  It
   emits `<T1, T2>` wrappers for open generics and maps arguments recursively
   for closed generics.  Special cases include:
   - `System.Threading.Tasks.Task` → `Promise<void>`
   - `System.Threading.Tasks.Task<T>` → `Promise<map(T)>`
   - `ValueTuple` types retain CLR naming (processed by `TypeNameMapping`).
7. **Dependency tracking**: every mapped CLR `Type` passes through
   `DependencyHelpers.TrackTypeDependency`, which records the defining assembly
   and full type name in `Pipeline/DependencyTracker`.  This data drives both the
   import list during rendering and the `.bindings.json` file described later.

## Warnings

- Pointer types emit a warning (`AddWarning`) because the generated signature
  falls back to `any`.
- `TypeMapper` aggregates warnings through `GenerationLogger` when invoked with
  the `--log` option.

These rules guarantee consistent TypeScript representations, enabling the
analysis and emit phases to operate on a predictable shape for every CLR type.
