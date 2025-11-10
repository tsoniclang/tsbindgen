# Phase 4: Emit - Output File Generation

## Overview

Final phase generates all output files from validated `EmissionPlan`:
1. TypeScript declarations (`.d.ts`)
2. Metadata sidecars (`metadata.json`)
3. Binding mappings (`bindings.json`)
4. Module stubs (`index.js`)
5. Support types (`_support/types.d.ts`)

**Key Principles:**
- Uses `Renamer` for all TypeScript identifiers (single source of truth)
- Respects `EmitScope` (ClassSurface/StaticSurface/ViewOnly)
- `TypeNameResolver` ensures type safety
- Deterministic output via `EmitOrder`

---

## Output Structure

```
output/
  System/
    index.d.ts              # Public facade (re-exports)
    index.js                # Runtime stub (throws)
    bindings.json           # CLR name mappings
    internal/
      index.d.ts            # Actual declarations
      metadata.json         # CLR-specific metadata
  _support/
    types.d.ts              # TSUnsafePointer, TSByRef
```

**Special Cases:**
- Root namespace: Uses `_root/` instead of `internal/`
- Dotted namespaces: Cannot use `export import` (TS limitation)

---

## File: FacadeEmitter.cs

**Generates:** `index.d.ts` (public entry point)

**Content:**
1. Import from `internal/index.d.ts`
2. Imports from dependency namespaces
3. Re-export namespace (non-dotted only): `export import System = Internal.System;`
4. Individual type exports: `export type List_1 = Internal.System.Collections.Generic.List_1;`

**Helpers:**
- `GetImportAlias()` - Converts dots to underscores: `"System.Collections.Generic"` → `"System_Collections_Generic"`

**Example:**
```typescript
import * as Internal from './internal/index';
import * as System from '../System/index';
export import Generic = Internal.System.Collections.Generic;
export type List_1 = Internal.System.Collections.Generic.List_1;
```

---

## File: InternalIndexEmitter.cs

**Generates:** `internal/index.d.ts` (actual declarations)

**Content:**
1. File header (namespace, assemblies)
2. Branded primitives: `type int = number & { __brand: "int" };`
3. Conditional support types import (if pointers/byrefs used)
4. Cross-namespace imports
5. Namespace wrapper (skipped for root)
6. Type declarations (in `EmitOrder`)

**Companion Views Pattern:**
- With views: Emit `TypeName$instance` + `__TypeName$views` + intersection type alias
- Without views: Emit normal class/interface/enum/delegate

**Methods:**
- `ShouldEmit()` - Only public types
- `NamespaceUsesSupportTypes()` - Scans for pointers/byrefs
- `ContainsUnsafeType()` - Recursive type reference check
- `EmitBrandedPrimitives()` - All CLR numeric types
- `EmitCompanionViewsInterface()` - `interface __List_1$views<T> { ... }`
- `EmitIntersectionTypeAlias()` - `export type List_1<T> = List_1$instance<T> & __List_1$views<T>;`

**Example:**
```typescript
export type int = number & { __brand: "int" };
import type { TSUnsafePointer, TSByRef } from "../_support/types";
import type { Object } from "../System/internal/index";

export namespace System.Collections.Generic {
    export class List_1$instance<T> {
        constructor(capacity: int);
        readonly Count: int;
        Add(item: T): void;
    }
    export interface __List_1$views<T> {
        readonly IEnumerable_1$view: IEnumerable_1<T>;
    }
    export type List_1<T> = List_1$instance<T> & __List_1$views<T>;
}
```

---

## File: MetadataEmitter.cs

**Generates:** `internal/metadata.json` (CLR-specific info for Tsonic compiler)

**Records:**
- `NamespaceMetadata` - Namespace, assemblies, types list
- `TypeMetadata` - ClrName, TsEmitName, kind, accessibility, modifiers, arity, members
- `MethodMetadata` - ClrName, TsEmitName, NormalizedSignature, Provenance, EmitScope, modifiers, arity, parameters, SourceInterface (ViewOnly)
- `PropertyMetadata` - ClrName, TsEmitName, NormalizedSignature, Provenance, EmitScope, modifiers, IsIndexer, HasGetter/Setter, SourceInterface
- `FieldMetadata` - ClrName, TsEmitName, NormalizedSignature, IsStatic/ReadOnly/Literal
- `EventMetadata` - ClrName, TsEmitName, NormalizedSignature, IsStatic
- `ConstructorMetadata` - NormalizedSignature, IsStatic, ParameterCount

**Key Decision:**
- ViewOnly members get view-scoped names: `"IEnumerable_1$view$GetEnumerator"`
- ClassSurface members get class-scoped names: `"GetEnumerator"`

**Example:**
```json
{
  "Namespace": "System.Collections.Generic",
  "ContributingAssemblies": ["System.Private.CoreLib"],
  "Types": [{
    "ClrName": "System.Collections.Generic.List`1",
    "TsEmitName": "List_1",
    "Kind": "Class",
    "Methods": [{
      "ClrName": "Add",
      "TsEmitName": "Add",
      "NormalizedSignature": "Add(T):System.Void",
      "Provenance": "Direct",
      "EmitScope": "ClassSurface",
      "IsStatic": false
    }, {
      "ClrName": "GetEnumerator",
      "TsEmitName": "IEnumerable_1$view$GetEnumerator",
      "NormalizedSignature": "GetEnumerator():System.Collections.Generic.IEnumerator`1<T>",
      "Provenance": "ExplicitImpl",
      "EmitScope": "ViewOnly",
      "SourceInterface": "System.Collections.Generic.IEnumerable`1"
    }]
  }]
}
```

---

## File: BindingEmitter.cs

**Generates:** `bindings.json` (CLR-to-TS name mappings for runtime)

**Records:**
- `NamespaceBindings` - Namespace, types list
- `TypeBinding` - ClrName, TsEmitName, AssemblyName, MetadataToken, members
- `MethodBinding` - ClrName, TsEmitName, MetadataToken, CanonicalSignature, NormalizedSignature, EmitScope, arity, parameters
- `PropertyBinding` - ClrName, TsEmitName, MetadataToken, CanonicalSignature, NormalizedSignature, EmitScope, IsIndexer, HasGetter/Setter
- `FieldBinding` - ClrName, TsEmitName, MetadataToken, NormalizedSignature, IsStatic/ReadOnly
- `EventBinding` - ClrName, TsEmitName, MetadataToken, NormalizedSignature, IsStatic
- `ConstructorBinding` - MetadataToken, CanonicalSignature, NormalizedSignature, IsStatic, ParameterCount

**Key Decision:**
- Includes ALL members (ClassSurface, StaticSurface, AND ViewOnly)
- Allows runtime to bind explicit interface implementations

**Example:**
```json
{
  "Namespace": "System.Collections.Generic",
  "Types": [{
    "ClrName": "System.Collections.Generic.List`1",
    "TsEmitName": "List_1",
    "AssemblyName": "System.Private.CoreLib",
    "Methods": [{
      "ClrName": "Add",
      "TsEmitName": "Add",
      "MetadataToken": 100663359,
      "CanonicalSignature": "Add(!0):System.Void",
      "NormalizedSignature": "Add(T):System.Void",
      "EmitScope": "ClassSurface"
    }, {
      "ClrName": "GetEnumerator",
      "TsEmitName": "IEnumerable_1$view$GetEnumerator",
      "MetadataToken": 100663360,
      "EmitScope": "ViewOnly"
    }]
  }]
}
```

---

## File: ModuleStubEmitter.cs

**Generates:** `index.js` (runtime stub that throws)

**Example:**
```javascript
throw new Error(
  'Cannot import CLR namespace System.Collections.Generic in JavaScript runtime. ' +
  'This module provides TypeScript type definitions only. ' +
  'Actual implementation requires .NET runtime via Tsonic compiler.'
);
```

---

## File: SupportTypesEmitter.cs

**Generates:** `_support/types.d.ts` (centralized marker types, emitted once)

**Content:**
- `TSUnsafePointer<T>` - For pointer types (erases to `unknown` for type safety)
- `TSByRef<T>` - For ref/out/in parameters (structural `{ value: T }`)

**Example:**
```typescript
export type TSUnsafePointer<T> = unknown & { readonly __tsbindgenPtr?: unique symbol };
export type TSByRef<T> = { value: T } & { readonly __tsbindgenByRef?: unique symbol };
```

---

## File: TypeMap.cs

**Maps CLR built-in types to TypeScript types. MUST be checked BEFORE TypeIndex lookup to avoid PG_LOAD_001.**

**Key Mappings:**
- `System.Void` → `void`
- `System.Boolean` → `boolean`
- `System.String` → `string`
- `System.Object` → `any`
- `System.Char` → `string`
- `System.Int32` → `int` (branded)
- `System.Array` → `any[]`
- `System.Delegate` → `Function`

**Methods:**
- `TryMapBuiltin()` - Returns TS type for built-in CLR types
- `IsUnsupportedSpecialForm()` - Checks for pointers/byrefs/function pointers
- `MapUnsupportedSpecialForm()` - Returns `"any"` if `allowUnsafeMaps` true, else throws
- `IsBrandedPrimitive()` - Checks if numeric type needs branded syntax

---

## File: TypeNameResolver.cs

**Single source of truth for resolving TypeScript identifiers from TypeReferences. Uses Renamer to ensure imports/declarations match.**

**Methods:**
- `For(TypeSymbol)` - Returns final TS identifier from `Renamer.GetFinalTypeName()`
- `For(NamedTypeReference)` - Resolves TS name via:
  1. Try `TypeMap.TryMapBuiltin()` FIRST (short-circuit)
  2. Look up in `TypeIndex` via StableId
  3. If not in graph, sanitize CLR name (external type)
  4. Get final name from `Renamer`
- `SanitizeClrName()` - Replaces backtick/plus: `List`1` → `List_1`, `Foo+Bar` → `Foo_Bar`
- `TryMapPrimitive()` - Static helper wrapping `TypeMap.TryMapBuiltin()`
- `IsPrimitive()` - Checks if type doesn't need imports

---

## File: Printers/ClassPrinter.cs

**Prints TypeScript class declarations from TypeSymbol.**

**Methods:**
- `Print()` - Dispatches by `TypeKind`: Class/Struct/StaticNamespace/Enum/Delegate/Interface
- `PrintInstance()` - Emits `TypeName$instance` (for companion views)
- `PrintClass()` - Emits `class`, generic parameters, base class (skips Object/ValueType), interfaces, members
- `PrintStruct()` - Same as class but no abstract modifier, no base class
- `PrintStaticClass()` - Emits `abstract class` with static-only members
- `PrintEnum()` - Emits `enum` with const fields (uses ClassStatic scope for names)
- `PrintDelegate()` - Emits `type` with function signature from `Invoke` method
- `PrintInterface()` - Emits `interface` with extends, members
- `EmitMembers()` - Emits instance members (constructors, fields, properties, methods) via ClassInstance scope
- `EmitStaticMembers()` - Emits static members via ClassStatic scope
- `EmitInterfaceMembers()` - Emits interface members (no static support in TS interfaces) via ClassSurface scope
- `PrintGenericParameter()` - Emits generic param with constraints (single → `extends`, multiple → `&`)

**GUARD:** Never prints non-public types.

---

## File: Printers/MethodPrinter.cs

**Prints TypeScript method signatures from MethodSymbol.**

**Methods:**
- `Print()` - Emits method with modifiers (skipped for interfaces), name from Renamer (ClassSurface scope), generic params, parameters, return type
- `PrintGenericParameter()` - Emits generic param with constraints
- `PrintParameter()` - Emits parameter name, `?` for optional, `{ value: T }` for ref/out
- `PrintWithParamsExpansion()` - Converts params array to rest parameter: `items: T[]` → `...items: T[]`
- `PrintOverloads()` - Yields one string per overload
- `PrintAsPropertyAccessor()` - Extracts property name from `get_Foo`/`set_Foo`

**Key Decision:** Interface members don't get static/abstract modifiers (TS doesn't support static interface members).

---

## File: Printers/TypeRefPrinter.cs

**Prints TypeScript type references from TypeReference model. Uses TypeNameResolver for all names.**

**Methods:**
- `Print()` - Dispatches by kind: Placeholder/Named/GenericParameter/Array/Pointer/ByRef/Nested
- `PrintPlaceholder()` - Warns, returns `"any"`
- `PrintNamed()` - 1) Try `TryMapPrimitive()`, 2) Get name from `resolver.ResolveTypeName()`, 3) Validate non-empty, 4) Handle generic args recursively
- `PrintGenericParameter()` - Returns name as-is: `T`, `TKey`
- `PrintArray()` - Single-dim: `T[]`, multi-dim: `Array<Array<T>>`
- `PrintPointer()` - Returns `TSUnsafePointer<T>`
- `PrintByRef()` - Returns `TSByRef<T>`
- `PrintNested()` - Uses resolver on `FullReference`

**Helpers:**
- `PrintList()` - Comma-separated type references
- `PrintNullable()` - `T | null`
- `PrintReadonlyArray()` - `ReadonlyArray<T>`
- `PrintPromise()` - `Promise<T>`
- `PrintTuple()` - `[T1, T2, T3]`
- `PrintUnion()` - `T1 | T2 | T3`
- `PrintIntersection()` - `T1 & T2 & T3`
- `PrintTypeof()` - `typeof ClassName`

---

## Key Design Decisions

### 1. Companion Views Pattern
- Emit `TypeName$instance` class (ClassSurface) + `__TypeName$views` interface (ViewOnly) + intersection type alias
- Keeps class surface clean, views type-checked via intersection

### 2. Branded Primitive Types
- Emit `type int = number & { __brand: "int" };` in all namespaces
- Type safety for CLR numeric types, no runtime overhead

### 3. Unsafe Type Markers
- Centralized `_support/types.d.ts` with `TSUnsafePointer<T>` (erases to `unknown`) and `TSByRef<T>` (structural `{ value: T }`)
- Type-safe, preserves info, branded for auditing

### 4. EmitScope Filtering
- ClassSurface → class body
- StaticSurface → static members
- ViewOnly → companion views interface

### 5. TypeNameResolver Single Source of Truth
- All name resolution goes through `TypeNameResolver` → `Renamer`
- Never use CLR names directly in printers
- Guaranteed consistency

---

## Summary

Emit phase generates all output files from validated `EmissionPlan`:

**Emitters:**
1. FacadeEmitter - Public facades with re-exports
2. InternalIndexEmitter - Actual declarations with companion views
3. MetadataEmitter - CLR metadata for Tsonic compiler
4. BindingEmitter - CLR-to-TS name mappings for runtime
5. ModuleStubEmitter - JavaScript stubs that throw
6. SupportTypesEmitter - Centralized unsafe type markers

**All emitters use:**
- TypeNameResolver for consistent type names (single source of truth)
- Renamer for final TS identifiers (suffix handling)
- EmitScope for member filtering (ClassSurface/StaticSurface/ViewOnly)
- EmitOrder for deterministic output (stable across runs)

**Output is deterministic, type-safe, and respects single-phase architecture guarantees.**
