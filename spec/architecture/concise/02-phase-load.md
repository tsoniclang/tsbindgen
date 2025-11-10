# Phase LOAD: Reflection

## Overview

Pure CLR reflection phase. No TypeScript concepts yet. Outputs `SymbolGraph` with types, members, and relationships.

**Key operations:**
- BFS transitive closure loading
- Assembly identity validation (PublicKeyToken, version drift)
- Type/member extraction via reflection
- Type reference building with cycle detection
- Closed generic interface substitution maps

---

## AssemblyLoader.cs

### LoadClosureResult
```csharp
record LoadClosureResult(MetadataLoadContext, IReadOnlyList<Assembly>, IReadOnlyDictionary<AssemblyKey, string>)
```

### AssemblyLoader

**CreateLoadContext(assemblyPaths)**
- Uses `PathAssemblyResolver` with target dir + reference pack dir
- Core assembly: System.Private.CoreLib

**LoadAssemblies(loadContext, assemblyPaths)**
- Deduplicates by assembly identity string: `"Name, Version=X.Y.Z.W"`
- Skips mscorlib (core assembly auto-loaded)

**LoadClosure(seedPaths, refPaths, strictVersions)**
- Main entry point for BCL generation
- 5 phases:
  1. `BuildCandidateMap()` - Scan ref dirs → `AssemblyKey → List<path>` (multi-version)
  2. `ResolveClosure()` - BFS transitive closure → `AssemblyKey → path`
  3. `ValidateAssemblyIdentity()` - PG_LOAD_002/003/004 guards
  4. `FindCoreLibrary()` - Locate System.Private.CoreLib
  5. Create `MetadataLoadContext` and load all

**BuildCandidateMap(refPaths)**
- Scans *.dll in reference dirs
- Maps `AssemblyKey → List<string>` for version selection
- Silently skips unreadable DLLs

**ResolveClosure(seedPaths, candidateMap, strictVersions)**
- BFS algorithm:
  - Queue starts with seed paths
  - Uses `PEReader`/`MetadataReader` for lightweight loading (no Assembly.Load)
  - Version policy: highest version wins (logs upgrades)
  - Missing refs: PG_LOAD_001 (external ref) - silently skipped
- Returns: `AssemblyKey → resolved path`

**ValidateAssemblyIdentity(resolvedPaths, strictVersions)**
- **PG_LOAD_002:** Mixed PublicKeyToken for same name → ERROR
- **PG_LOAD_003:** Major version drift → ERROR (strict) or WARNING
- **PG_LOAD_004:** Placeholder (retargetable/ContentType)

**FindCoreLibrary(resolvedPaths)**
- Finds System.Private.CoreLib by name (case-insensitive)
- Throws if missing

**GetReferenceAssembliesPath(assemblyPaths)**
- Primary: Dir of first assembly path
- Fallback: Runtime directory (`typeof(object).Assembly.Location`)
- Rationale: Same dir as target ensures version consistency

**GetResolverPaths(assemblyPaths, referenceAssembliesPath)**
- Deduplicates by assembly name (file name without extension)
- First wins: reference pack takes precedence over target dir
- Prevents PathAssemblyResolver confusion

---

## ReflectionReader.cs

### ReflectionReader
Fields: `_ctx`, `_typeFactory`

**ReadAssemblies(loadContext, assemblyPaths)**
- Sorts assemblies by name (determinism)
- Filters: compiler-generated types, non-public types
- Groups types by namespace
- Returns `SymbolGraph` with `Namespaces` and `SourceAssemblies`

**ReadType(type)**
1. `StableId`: AssemblyName + ClrFullName (interned)
2. `DetermineTypeKind()` → Enum/Interface/Delegate/StaticNamespace/Struct/Class
3. `ComputeAccessibility()` → Recursive for nested types
4. Generic params: `_typeFactory.CreateGenericParameterSymbol()`
5. Base/interfaces: `_typeFactory.Create()`
6. Members: `ReadMembers()`
7. Nested types: Recursive, filters compiler-generated

**ComputeAccessibility(type)**
- Nested types: Intersection of declaring type accessibility + nested visibility
- `IsNestedPublic` + `DeclaringType.Public` → `Public`, else `Internal`
- Prevents emitting nested public types inside internal containers

**DetermineTypeKind(type)**
Order: Enum → Interface → Delegate → StaticNamespace (abstract+sealed+!valuetype) → Struct → Class

**ReadMembers(type)**
- Uses `BindingFlags.DeclaredOnly` (no inherited members)
- Skips special names (property/event accessors)
- Duplicate detection: `methodKey = "{name}|{metadataToken}"` → ERROR if duplicate StableId
- Returns: `TypeMembers` with all collections

**ReadMethod(method, declaringType)**
- Explicit interface impl: `clrName.Contains('.')` → use qualified name (e.g., "System.IDisposable.Dispose")
- `MemberStableId`: includes `CanonicalSignature` + `MetadataToken`
- `IsOverride` via `IsMethodOverride(method)`
- All members start with `Provenance.Original`, `EmitScope.ClassSurface`

**ReadProperty(property, declaringType)**
- `IndexParameters` for indexers
- `IsOverride` from getter if exists
- Visibility from getter ?? setter

**ReadField(field, declaringType)**
- `IsReadOnly` = `field.IsInitOnly`
- `IsConst` = `field.IsLiteral`
- `ConstValue` via `field.GetRawConstantValue()`

**ReadEvent(evt, declaringType)**
- Explicit interface impl detection (same as methods)
- Visibility from add method

**ReadConstructor(ctor, declaringType)**
- `MemberName = ".ctor"`
- `CanonicalSignature` from parameter types

**ReadParameter(param)**
- Fallback name: `$"arg{param.Position}"`
- Sanitizes TypeScript reserved words: `TypeScriptReservedWords.SanitizeParameterName()`
- `IsRef` = `IsByRef && !IsOut`
- `IsParams` = check ParamArrayAttribute

**IsMethodOverride(method)**
```csharp
return method.IsVirtual && !method.Attributes.HasFlag(MethodAttributes.NewSlot);
```
- Override = virtual method reusing parent's vtable slot (no NewSlot)

**IsCompilerGenerated(typeName)**
```csharp
return typeName.Contains('<') || typeName.Contains('>');
```
Examples: `<Module>`, `<>c__DisplayClass`, `<>d__Iterator`

**CreateMethodSignature(method)**
- Calls `_ctx.CanonicalizeMethod(name, paramTypes, returnType)` (interned)

---

## InterfaceMemberSubstitution.cs

### InterfaceMemberSubstitution (static)

**Purpose:** Builds substitution maps for closed generic interfaces. Actual substitution performed by Shape phase.

**SubstituteClosedInterfaces(ctx, graph)**
1. `BuildInterfaceIndex()` → `ClrFullName → TypeSymbol`
2. For each type: `ProcessType()`
3. Logs total substitution count
- **Note:** Maps built but not stored. Shape phase rebuilds as needed.

**BuildInterfaceIndex(graph)**
- Filters `type.Kind == TypeKind.Interface`
- Returns: `Dictionary<string, TypeSymbol>`

**ProcessType(type, interfaceIndex)**
- For each interface in `type.Interfaces`:
  - Check if closed generic: `NamedTypeReference` with `TypeArguments.Count > 0`
  - Extract generic definition: `GetGenericDefinitionName()`
  - Look up in index
  - Call `BuildSubstitutionMap()`

**BuildSubstitutionMap(interfaceSymbol, closedInterfaceRef)**
- Validates arity matches
- Maps: `parameter.Name → typeArgument`
- Returns: `Dictionary<string, TypeReference>`
- Example: `IComparable<T>` + `IComparable<int>` → `{ "T" → int }`

**SubstituteTypeReference(original, substitutionMap)**
Recursive pattern matching:
- `GenericParameterReference`: Lookup in map or return original
- `ArrayTypeReference`: Substitute element type
- `PointerTypeReference`: Substitute pointee type
- `ByRefTypeReference`: Substitute referenced type
- `NamedTypeReference`: Substitute type arguments
- Other: Return original

**GetGenericDefinitionName(fullName)**
- Finds backtick: extract arity digits
- Converts: `"System.IComparable<int>"` → `"System.IComparable`1"`

---

## TypeReferenceFactory.cs

### Purpose
Converts `System.Type` to `TypeReference`. Memoization + cycle detection prevents stack overflow on recursive constraints.

### TypeReferenceFactory
Fields: `_ctx`, `_cache`, `_inProgress`

**Create(type)**
1. Check cache → early return
2. Detect cycle: if `_inProgress.Contains(type)` → return `PlaceholderTypeReference`
3. Mark in-progress (try-finally cleanup)
4. Call `CreateInternal()`
5. Cache result

**CreateInternal(type)**
Order: ByRef → Pointer → Array → GenericParameter → Named

**Pointer depth counting:**
- Walks element types: `int***` → depth 3
- Final element type via `Create()` recursion

**CreateNamed(type)**
1. Extract: assemblyName, fullName, namespace, name
2. **HARDENING:** Guarantee non-empty Name:
   - Fallback: Extract last segment after '.' or '+'
   - Last resort: `"UnknownType"` + log warning
3. Generic types:
   - Arity from `GetGenericArguments().Length`
   - If `IsConstructedGenericType`: Recursively `Create()` each arg
4. **HARDENING:** Stamp `interfaceStableId` at load time:
   - Format: `"{assemblyName}:{fullName}"`
   - Eliminates repeated computation in later phases

**CreateGenericParameter(type)**
- Creates `GenericParameterId` with declaring type + position
- **Constraints NOT resolved** (empty list)
- ConstraintCloser (Shape phase) resolves later to avoid infinite recursion

**CreateGenericParameterSymbol(type)**
1. Validates `type.IsGenericParameter`
2. Extracts variance: Covariant/Contravariant/None
3. Special constraints: ReferenceType/ValueType/DefaultConstructor (bitwise OR)
4. **Stores raw constraint types:** `type.GetGenericParameterConstraints()` → System.Type[]
5. ConstraintCloser converts to TypeReferences later
6. Empty `Constraints` field (filled by ConstraintCloser)

**ClearCache()**
- Test-only: `_cache.Clear()`

---

## Call Flow

### LoadClosure
```
LoadClosure
  ├─► BuildCandidateMap → AssemblyKey → List<path>
  ├─► ResolveClosure (BFS) → AssemblyKey → path
  ├─► ValidateAssemblyIdentity (PG_LOAD_002/003/004)
  ├─► FindCoreLibrary
  └─► Create MetadataLoadContext + load all
```

### ReadAssemblies
```
ReadAssemblies
  ├─► AssemblyLoader.LoadAssemblies
  ├─► For each assembly (sorted):
  │     ├─► For each type:
  │     │     ├─► Skip compiler-generated
  │     │     ├─► ComputeAccessibility (recursive for nested)
  │     │     ├─► Skip non-public
  │     │     └─► ReadType
  │     │           ├─► DetermineTypeKind
  │     │           ├─► TypeReferenceFactory.CreateGenericParameterSymbol
  │     │           ├─► TypeReferenceFactory.Create (base/interfaces)
  │     │           ├─► ReadMembers
  │     │           │     ├─► ReadMethod (explicit iface impl detection)
  │     │           │     ├─► ReadProperty (indexer support)
  │     │           │     ├─► ReadField (const value extraction)
  │     │           │     ├─► ReadEvent
  │     │           │     └─► ReadConstructor
  │     │           └─► ReadType (nested types, recursive)
  │     └─► Group by namespace
  └─► Build SymbolGraph
```

### TypeReferenceFactory.Create
```
Create(type)
  ├─► Cache check → return
  ├─► Cycle check → PlaceholderTypeReference
  └─► CreateInternal
        ├─► ByRef → ByRefTypeReference
        ├─► Pointer → PointerTypeReference (depth counting)
        ├─► Array → ArrayTypeReference (rank from GetArrayRank)
        ├─► GenericParameter → GenericParameterReference (constraints empty)
        └─► Named → NamedTypeReference
              ├─► HARDENING: Guarantee non-empty Name
              ├─► Generic: Recursively Create() each arg
              └─► HARDENING: Stamp interfaceStableId
```

### InterfaceMemberSubstitution
```
SubstituteClosedInterfaces
  ├─► BuildInterfaceIndex → ClrFullName → TypeSymbol
  └─► For each type:
        └─► ProcessType
              ├─► For each closed generic interface:
              │     ├─► GetGenericDefinitionName
              │     └─► BuildSubstitutionMap
              │           └─► Map: parameter.Name → typeArgument
              └─► Return substitution count
```

---

## Key Algorithms

### BFS Transitive Closure (ResolveClosure)
1. Queue ← seed paths
2. While queue not empty:
   - Dequeue path
   - Get AssemblyKey (via `AssemblyName.GetAssemblyName`)
   - Skip if visited
   - Mark visited
   - Version policy: If already resolved, keep highest version
   - Add to resolved map
   - Read metadata via PEReader/MetadataReader (lightweight)
   - For each AssemblyReference:
     - Look up in candidate map
     - Pick highest version from candidates
     - Enqueue

**Time:** O(N), **Space:** O(N)

### Type Reference Cycle Detection
1. Check cache → return
2. Check `_inProgress` → return PlaceholderTypeReference (breaks cycle)
3. Add to `_inProgress` (try-finally cleanup)
4. `CreateInternal()` (may recursively call Create)
5. Cache result

**Example cycle:** `IComparable<T> where T : IComparable<T>`
- Create(T) → marks T in-progress → resolves constraint → Create(T) → detects cycle → PlaceholderTypeReference

**Time:** O(D) first call, O(1) cached. **Space:** O(D) stack, O(T) cache.

### Accessibility for Nested Types
```csharp
// Recursive: nested public only if all ancestors public
if (type.IsNestedPublic)
    return ComputeAccessibility(type.DeclaringType!) == Public ? Public : Internal;
```

**Time:** O(N) nesting depth, **Space:** O(N) stack

### Generic Parameter Substitution
Recursive pattern matching:
- GenericParam → map lookup or original
- Array/Pointer/ByRef → substitute element/pointee/ref
- NamedTypeRef → substitute type args
- Other → original

**Time:** O(D) type depth, **Space:** O(D) stack

---

## Summary

**Load phase responsibilities:**
1. Load transitive closure (BFS)
2. Validate assembly identity (PublicKeyToken, version drift)
3. Extract types/members (reflection)
4. Build type references (memoization + cycle detection)
5. Build substitution maps (closed generic interfaces)

**Output:** `SymbolGraph` with pure CLR metadata (no TypeScript concepts).

**Key design decisions:**
- **MetadataLoadContext isolation:** Reflection on BCL without version conflicts
- **Name-based type comparisons:** Required for MetadataLoadContext (`typeof()` doesn't work)
- **Cycle detection:** Prevents stack overflow on recursive constraints
- **DeclaredOnly members:** No inherited members (Shape phase handles inheritance)
- **Compiler-generated filtered:** Skips angle-bracket types
- **Deduplication:** Assembly identity, MetadataToken, type keys
- **Determinism:** Sorted iteration for reproducible output
- **Lightweight loading:** PEReader/MetadataReader (no Assembly.Load in BFS)
- **Version policy:** Highest version wins
- **Hardening:** Non-empty Name guarantee, InterfaceStableId stamping
- **Deferred constraint resolution:** ConstraintCloser (Shape phase) handles recursive constraints
