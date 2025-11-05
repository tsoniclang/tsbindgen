# Semantic Analysis & Transforms

The analysis phase runs between raw reflection and the emitters.  It polishes
the type model so the generated TypeScript matches CLR semantics while avoiding
compiler errors (TS2416, TS2417, TS2320, etc.).

## Filters

- **Namespace/type filtering**: `Analysis/TypeFilters.ShouldIncludeType` removes
  non-public types, compiler-generated types, and any namespace configured in
  `GeneratorConfig.SkipNamespaces`.  Whitelists from the CLI are also enforced
  here.
- **Member filtering**: `Analysis/MemberFilters.ShouldIncludeMember` omits
  “noise” methods (`Equals`, `GetHashCode`, `GetType`, `ToString`) unless
  explicitly configured.

## Explicit interface implementations

`Analysis/ExplicitInterfaceAnalyzer` inspects interface maps and classifies
explicit implementations (non-public accessor/method).  The emitters remove such
members from the generated class/interface body but keep them in metadata so the
runtime knows the implementation exists.

## Diamond inheritance & intersection aliases

- `InterfaceAnalysis.HasDiamondInheritance` detects when a CLR interface inherits
  the same ancestor through multiple parents.
- `InterfaceAnalysis.GenerateBaseInterface` creates a `_Base` interface
  containing only the unique members for the diamonded interface.
- `InterfaceAnalysis.CreateIntersectionAlias` emits a type alias that intersects
  `_Base` with all parents, preserving the CLR surface while keeping TypeScript
  happy.
- `InterfaceAnalysis.PruneRedundantInterfaceExtends` removes duplicate parents
  implied by other extends clauses.

## Property covariance & hiding

`Emit/PropertyEmitter.ApplyCovariantWrapperIfNeeded`:

1. Compares the mapped type of the derived property against base class properties
   (walking the entire base chain).  If the types differ, the property is emitted
   as `Covariant<TSpecific, TContract>` to satisfy TS2416.
2. For readonly properties implementing interfaces, compares the concrete type
   against the interface contract and applies the same wrapper when necessary.
3. `PropertyEmitter.IsRedundantPropertyRedeclaration` skips duplicate
   declarations when a base property already produces the same mapped type.

### Enum covariance fallback

TypeScript treats enums as numeric literal unions, so intersecting an enum with
the `Covariant<TSpecific, TContract>` helper collapses to `never`. When a
derived property overrides a base property and both return enums with different
declaring types (for example `HttpRequestCachePolicy.Level` overriding
`RequestCachePolicy.Level`), the generator emits the **base** enum in the `.d.ts`
file and logs a warning. The metadata sidecar still records the CLR-specific
enum so the runtime can call through to `HttpRequestCacheLevel`.

```ts
// Emitted declaration (simplified)
class HttpRequestCachePolicy extends System.Net.Cache.RequestCachePolicy {
    readonly Level: System.Net.Cache.RequestCacheLevel;
}

// Metadata keeps the derived return type so the runtime knows the CLR shape
"Level": { "kind": "property", "returnType": "System.Net.Cache.HttpRequestCacheLevel", ... }
```

Consumers get a TypeScript-safe type (`RequestCacheLevel`) while the runtime
still sees the more specific CLR enum via metadata.

## Static member compatibility

`Analysis/OverloadBuilder.AddBaseClassCompatibleOverloads` ensures that the
static side of derived classes exposes the same signatures as the base class:

- Uses `EnumerateBaseStaticMethods` to pull both constructed-type and type-definition
  methods (capturing method-level generics).
- Filters out signatures that reference the derived class’s type parameters via
  `TypeReferenceChecker.TypeReferencesAnyTypeParam` (avoids TS2302).
- Adds both generic and non-generic overloads to the derived class.

### Static property/method name collisions

When a class declares both a static property and a static method with the same
identifier (e.g. `Vector<T>.Count` in the BCL) TypeScript reports a duplicate
identifier. The emitter keeps the static **method** on the class and promotes
the colliding static properties into the companion namespace so that class/namespace
merging provides both call sites without a clash.

```ts
class Vector_1<T> {
    static Count<T>(vector: System.Numerics.Vector_1<T>, value: T): int;
}

namespace Vector_1 {
    export const Count: int; // formerly the static property on the class
}
```

Metadata still records both members against the class so the runtime knows the
CLR surface; the namespace export only affects the `.d.ts` shape.

## Interface-compatible overloads

`OverloadBuilder.AddInterfaceCompatibleOverloads` inspects interface maps and
injects any missing signatures into the class, covering covariant return types
and explicitly-implemented methods.

## Naming transforms

`Analysis/NameTransformApplicator` applies naming convention transformations to
the entire processed assembly **after** all emitters have run but **before**
rendering the TypeScript declarations:

- Takes CLI options (`--namespace-names camelCase`, `--class-names camelCase`,
  `--interface-names camelCase`, `--method-names camelCase`,
  `--property-names camelCase`, `--enum-member-names camelCase`)
- Recursively transforms all names in the `ProcessedAssembly` model
- Tracks all transformations in a binding manifest: `{ "selectMany": "SelectMany" }`
- The binding manifest is written as `<Assembly>.bindings.json` alongside the
  `.d.ts` and `.metadata.json` files
- Metadata files **always** contain original CLR names (not transformed names)

**Smart camelCase conversion** (`Analysis/NameTransform.ToCamelCase`):
- Normal PascalCase: `SelectMany` → `selectMany`
- Acronyms followed by PascalCase: `XMLParser` → `xmlParser`
- All-caps acronyms: `XML` → `xml`
- Already camelCase: `selectMany` → `selectMany` (unchanged)

This happens in a **post-processing phase** after all declaration records have
been created, keeping the transform logic separate from the emitters and
analysis code.

## Dependency tracking

- `Analysis/DependencyHelpers.TrackTypeDependency` records every external type
  reference (including generic arguments, array element types, by-ref/pointers).
- The data is persisted by `Pipeline/DependencyTracker` and is used during
  rendering and bindings generation to produce both `import type` statements and
  `<Assembly>.bindings.json`.

Together, these transforms make the emit phase straightforward: by the time a
type reaches an emitter it already models the CLR behaviour in a way TypeScript
can consume without errors.
