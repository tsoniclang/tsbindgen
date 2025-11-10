# Phase 5: Normalize - Name Reservation and Signature Unification

## Overview

The **Normalize** phase is the central name assignment phase that runs after Shape (Phase 4) and before Plan (Phase 6). It has two primary responsibilities:

1. **Name Reservation**: Assign final TypeScript names to all types and members through the central Renamer component
2. **Overload Unification**: Unify method overloads that TypeScript cannot distinguish

**Key Characteristics**:
- Pure transformation - returns new graph with `TsEmitName` set on all symbols
- Uses dual-scope algorithm for name reservation (class surface vs. view surface)
- Applies collision detection with `$view` suffix for view-only members
- Unifies overloads differing only by ref/out modifiers or constraints
- Validates completeness - ensures every emitted member has a rename decision

**Inputs**:
- `SymbolGraph` from Shape phase (EmitScope set, views planned)
- `Renamer` component (central name reservation system)

**Outputs**:
- `SymbolGraph` with `TsEmitName` set on all types and members
- Rename decisions stored in Renamer for later retrieval

---

## File: NameReservation.cs

### Purpose

Orchestrates the entire name reservation process. This is the **ONLY** place where names are reserved - all other components must use `Renamer.GetFinal*()` to retrieve names.

### Method: `ReserveAllNames(BuildContext, SymbolGraph) -> SymbolGraph`

**What it does**:
Reserves all type and member names in the symbol graph through the central Renamer, then applies those names to create a new graph.

**Algorithm Steps**:

1. **Reserve Type Names**:
   - Iterates through all namespaces and types (deterministic order)
   - Computes requested base name using `Shared.ComputeTypeRequestedBase()`
   - Calls `Renamer.ReserveTypeName()` with namespace scope
   - Skips compiler-generated types (names containing `<` or `>`)

2. **Reserve Class Surface Member Names**:
   - Calls `Reservation.ReserveMemberNamesOnly()` for each type
   - Reserves methods, properties, fields, events, constructors
   - Uses class scope: `ScopeFactory.ClassSurface(type, isStatic)`
   - Skips members with existing decisions from earlier passes
   - Skips `EmitScope.ViewOnly` members (handled separately)
   - Skips `EmitScope.Omitted` members (don't need names)

3. **Rebuild Class Surface Name Sets**:
   - After reservation, rebuilds complete sets of class-surface names
   - Separate sets for instance and static members
   - Checks ALL `ClassSurface` members (including pre-existing decisions)
   - Creates union set (`classAllNames`) for collision detection
   - **Critical**: Must include members renamed by earlier passes (e.g., HiddenMemberPlanner)

4. **Reserve View Member Names**:
   - Calls `Reservation.ReserveViewMemberNamesOnly()` for each type with explicit views
   - Passes `classAllNames` for collision detection
   - Uses view scope: `ScopeFactory.ViewSurface(type, interfaceStableId, isStatic)`
   - Applies `$view` suffix if view name collides with class surface

5. **Post-Reservation Audit**:
   - Calls `Audit.AuditReservationCompleteness()` to verify completeness
   - Ensures every emitted member has a rename decision
   - Throws if any members are missing decisions (fail-fast)

6. **Apply Names to Graph**:
   - Calls `Application.ApplyNamesToGraph()` to create new graph
   - Sets `TsEmitName` property on all types and members
   - Returns pure transformation (no mutation)

**Class Surface vs View Surface**:

- **Class Surface**: Members emitted on the class declaration
  - Scope: `ScopeFactory.ClassSurface(type, isStatic)`
  - Includes original members, synthesized members, shared interface members
  - Names must be unique within instance/static scope

- **View Surface**: Members emitted only on interface views
  - Scope: `ScopeFactory.ViewSurface(type, interfaceStableId, isStatic)`
  - Separate scope per interface to allow different implementations
  - Names can differ from class surface (using `$view` suffix if collision)

**Static vs Instance Scopes**:

TypeScript has separate namespaces for instance and static members:

```typescript
class Example {
    static foo: string;     // Static scope
    foo: number;           // Instance scope - NO COLLISION!
}
```

Therefore, the Renamer uses separate scopes:
- Class instance: `ScopeFactory.ClassSurface(type, isStatic: false)`
- Class static: `ScopeFactory.ClassSurface(type, isStatic: true)`
- View instance: `ScopeFactory.ViewSurface(type, interfaceId, isStatic: false)`
- View static: `ScopeFactory.ViewSurface(type, interfaceId, isStatic: true)`

**Metrics Logged**:
- Types reserved
- Members reserved
- Members skipped (already renamed by earlier passes)
- Compiler-generated types skipped

---

## File: Naming/Reservation.cs

### Purpose

Core name reservation logic - reserves names through Renamer without mutating symbols.

### Method: `ReserveMemberNamesOnly(BuildContext, TypeSymbol) -> (int Reserved, int Skipped)`

**How it works**:

1. Creates base scope using `ScopeFactory.ClassBase(type)`
2. Iterates through all member collections (deterministic order):
   - Methods (ordered by `ClrName`)
   - Properties (ordered by `ClrName`)
   - Fields (ordered by `ClrName`)
   - Events (ordered by `ClrName`)
   - Constructors

3. For each member:
   - **Skip if `EmitScope.ViewOnly`**: Will be handled in view-scoped reservation
   - **Skip if `EmitScope.Omitted`**: Doesn't need name
   - **Throw if `EmitScope.Unspecified`**: Developer mistake (must be set in Shape)
   - **Skip if already renamed**: Check using `Renamer.TryGetDecision()` with class scope
   - **Compute requested base**: Use `Shared.ComputeMethodBase()` or `Shared.RequestedBaseForMember()`
   - **Reserve through Renamer**: Call `Renamer.ReserveMemberName()` with base scope

4. Returns tuple of (Reserved count, Skipped count)

**Scope Construction**:
- Uses `ClassBase` scope for reservation (Renamer adds `#instance` or `#static` suffix)
- Checks existing decisions with `ClassSurface` scope (includes static flag)

**Collision Detection**:
- Renamer handles collisions by appending numeric suffixes
- First member gets base name: `toInt`
- Subsequent collisions get: `toInt2`, `toInt3`, etc.

### Method: `ReserveViewMemberNamesOnly(BuildContext, SymbolGraph, TypeSymbol, HashSet<string>) -> (int Reserved, int Skipped)`

**Purpose**: Reserve view member names in separate view-scoped namespaces.

**Algorithm**:

1. **Check for Views**: Return (0, 0) if type has no `ExplicitViews`

2. **Iterate Through Views** (deterministic order by interface StableId):
   - Get interface StableId from `TypeReference`
   - Create view base scope: `ScopeFactory.ViewBase(type, interfaceStableId)`
   - Create class surface scope for collision check: `ScopeFactory.ClassBase(type)`

3. **For Each ViewOnly Member** (deterministic order):
   - **Verify EmitScope**: Must be `EmitScope.ViewOnly`
   - **Find isStatic flag**: Use `Shared.FindMemberIsStatic()` to lookup in type's member collection
   - **Compute requested base**: Use `Shared.RequestedBaseForMember(clrName)`
   - **Peek at final name**: Use `Renamer.PeekFinalMemberName()` to see what it would get in view scope
   - **Check collision**: Does peek result exist in `classAllNames` set?
   - **Apply suffix if collision**: Use `requested + "$view"` (or `$view2`, `$view3` if taken)
   - **Reserve in view scope**: Call `Renamer.ReserveMemberName()` with view base scope

4. Returns tuple of (Reserved count, Skipped count)

**View-vs-Class Collision Detection**:

```csharp
// Example: System.Array implements IEnumerable<T>
// Class surface has: GetEnumerator() -> ArrayEnumerator
// View (IEnumerable<T>) has: GetEnumerator() -> IEnumerator<T>

// Peek at what view would get
var peek = ctx.Renamer.PeekFinalMemberName(viewScope, "getEnumerator", isStatic: false);
// peek = "getEnumerator" (first in view scope)

// Check collision with class surface
var collided = classAllNames.Contains(peek);
// collided = true (class already has "getEnumerator")

// Apply $view suffix
var finalRequested = "getEnumerator$view";

// Reserve in view scope
ctx.Renamer.ReserveMemberName(stableId, finalRequested, viewScope, ...);
```

**Why Separate View Scopes?**

A class can implement the same interface member differently through explicit interface implementation:

```csharp
// C#
class MyClass : IComparable, IComparable<MyClass>
{
    public int CompareTo(object obj) { ... }           // Class surface
    int IComparable<MyClass>.CompareTo(MyClass other) { ... }  // ViewOnly
}

// TypeScript
class MyClass {
    compareTo(obj: any): int;  // Class surface

    // View for IComparable<MyClass>
    readonly asIComparable_1: {
        compareTo$view(other: MyClass): int;  // $view suffix
    };
}
```

---

## File: Naming/Application.cs

### Purpose

Apply reserved names from Renamer to symbol graph. This is a pure transformation that creates a new graph with `TsEmitName` properties set.

### Method: `ApplyNamesToGraph(BuildContext, SymbolGraph) -> SymbolGraph`

**What it does**:
- Iterates through all namespaces and calls `ApplyNamesToNamespace()`
- Returns new graph with updated namespaces
- Calls `graph.WithIndices()` to rebuild lookup indices

### Private Method: `ApplyNamesToNamespace(BuildContext, NamespaceSymbol) -> NamespaceSymbol`

**What it does**:
- Skips compiler-generated types
- Calls `ApplyNamesToType()` for each type
- Returns new namespace with updated types

### Private Method: `ApplyNamesToType(BuildContext, TypeSymbol, NamespaceScope) -> TypeSymbol`

**What it does**:
- Gets `TsEmitName` from `Renamer.GetFinalTypeName(type)`
- Calls `ApplyNamesToMembers()` to update all members
- Returns new type with `TsEmitName` and updated `Members`

### Private Method: `ApplyNamesToMembers(BuildContext, TypeSymbol, TypeMembers, TypeScope) -> TypeMembers`

**How it works**:

For each member type (methods, properties, fields, events):

1. **ViewOnly Members** (methods, properties only):
   - Check if `EmitScope == EmitScope.ViewOnly`
   - Get interface StableId from `member.SourceInterface`
   - Create view scope: `ScopeFactory.ViewSurface(declaringType, interfaceStableId, isStatic)`
   - Get name: `Renamer.GetFinalMemberName(stableId, viewScope)`

2. **ClassSurface Members** (all others):
   - Create class scope: `ScopeFactory.ClassSurface(declaringType, isStatic)`
   - Get name: `Renamer.GetFinalMemberName(stableId, classScope)`

3. Return new member with `TsEmitName` set

**Returns**: New `TypeMembers` with all members updated

**Critical**: Must use the **exact same scopes** as reservation to ensure names match.

---

## File: Naming/Audit.cs

### Purpose

Verify name reservation completeness - ensures every type and member that will be emitted has a rename decision in the appropriate scope.

### Method: `AuditReservationCompleteness(BuildContext, SymbolGraph) -> void`

**What it does**:

1. **Iterate Through All Namespaces and Types**:
   - Skip compiler-generated types
   - Verify type name reserved in namespace scope
   - Call `AuditClassSurfaceMembers()` for class members
   - Call `AuditViewSurfaceMembers()` for view members

2. **Collect Errors**: Build list of missing rename decisions with detailed context

3. **Report Results**:
   - Log audit metrics (types checked, members checked)
   - Throw if any errors found (fail-fast)
   - Show first 10 errors in exception message

**Throws**: `InvalidOperationException` if any types/members missing decisions

### Private Method: `AuditClassSurfaceMembers(BuildContext, TypeSymbol, List<string>, ref int) -> void`

**What it does**:

For each member collection (methods, properties, fields, events):

1. Filter to `EmitScope.ClassSurface` members only
2. Create class scope: `ScopeFactory.ClassSurface(type, isStatic)`
3. Check decision exists: `Renamer.TryGetDecision(stableId, scope, out _)`
4. Add error if missing with full context:
   - Error code: `PG_FIN_003`
   - Type full name
   - Member name and StableId
   - EmitScope and IsStatic flags
   - Expected scope key

### Private Method: `AuditViewSurfaceMembers(BuildContext, TypeSymbol, List<string>, ref int) -> void`

**What it does**:

1. **Iterate Through ExplicitViews**:
   - Get interface StableId from view's InterfaceReference

2. **For Each ViewMember**:
   - Determine if actually `EmitScope.ViewOnly` by looking up in type's members
   - Find `isStatic` flag from member symbol
   - Create view scope: `ScopeFactory.ViewSurface(type, interfaceStableId, isStatic)`
   - Check decision exists: `Renamer.TryGetDecision(stableId, scope, out _)`
   - Add error if missing with full context (including view name and interface)

**Why Audit ViewOnly Separately?**

ViewOnly members use different scopes than class members. The audit must check the **exact same scope** used during reservation to ensure the decision exists.

---

## File: Naming/Shared.cs

### Purpose

Utility functions for name computation and sanitization. Ensures consistent name transformation across reservation and application.

### Method: `ComputeTypeRequestedBase(string clrName) -> string`

**What it does**: Compute the requested base name for a type before reservation.

**Transformations**:
1. Replace `+` with `_` (nested types: `Outer+Inner` → `Outer_Inner`)
2. Replace `` ` `` with `_` (generic arity: `List`1` → `List_1`)
3. Replace invalid TS characters: `<`, `>`, `[`, `]` → `_`
4. Apply reserved word sanitization: `TypeScriptReservedWords.Sanitize()`

**Example**:
```csharp
ComputeTypeRequestedBase("List`1")  // → "List_1"
ComputeTypeRequestedBase("Outer+Inner")  // → "Outer_Inner"
ComputeTypeRequestedBase("Dictionary`2+KeyCollection")  // → "Dictionary_2_KeyCollection"
```

### Method: `ComputeMethodBase(MethodSymbol method) -> string`

**What it does**: Compute base name for a method, handling operators and accessors.

**Operator Mapping**:
- Maps C# operator names to TypeScript-friendly names
- `op_Equality` → `equals`
- `op_Addition` → `add`
- `op_LessThan` → `lessThan`
- Unmapped operators → `operator_` prefix
- Applies reserved word sanitization to all operator names

**Regular Methods**:
- Accessors (`get_`, `set_`, `add_`, `remove_`) use CLR name as-is
- Regular methods use `SanitizeMemberName()`

**Example**:
```csharp
ComputeMethodBase("op_Addition")  // → "add"
ComputeMethodBase("get_Count")    // → "get_Count"
ComputeMethodBase("ToString")     // → "ToString"
```

### Method: `SanitizeMemberName(string name) -> string`

**What it does**: Remove invalid TypeScript identifier characters and handle reserved words.

**Transformations**:
1. Replace invalid characters: `<`, `>`, `[`, `]`, `+` → `_`
2. Apply reserved word sanitization: `TypeScriptReservedWords.Sanitize()`

**Example**:
```csharp
SanitizeMemberName("Add<T>")  // → "Add_T_"
SanitizeMemberName("delete")  // → "delete_" (TS reserved word)
```

### Method: `RequestedBaseForMember(string clrName) -> string`

**What it does**: Centralized function to compute requested base name for any member.

**Used by**:
- Class surface reservation (`ReserveMemberNamesOnly`)
- View member reservation (`ReserveViewMemberNamesOnly`)

**Ensures**: Both class and view members use identical base name computation, so collisions are detected correctly.

**Implementation**: Delegates to `SanitizeMemberName()`

### Method: `IsCompilerGenerated(string clrName) -> bool`

**What it does**: Check if a type name indicates compiler-generated code.

**Detection**: Names containing `<` or `>`

**Examples of compiler-generated types**:
- `<Module>` - Module initializer
- `<PrivateImplementationDetails>` - Compiler-generated helper types
- `<Name>e__FixedBuffer` - Fixed-size buffers
- `<>c__DisplayClass` - Lambda closure classes
- `<>c__Iterator` - Iterator state machines

### Method: `FindMemberIsStatic(TypeSymbol type, ViewMember viewMember) -> bool`

**What it does**: Find whether a view member is static by looking it up in the type's member collection.

**Algorithm**:
1. Switch on `viewMember.Kind` (Method, Property, Event)
2. Use `FirstOrDefault()` to find member with matching StableId
3. Return member's `IsStatic` flag, or `false` if not found

**Why needed**: ViewMember struct doesn't include `IsStatic` flag, so we must look it up.

### Method: `GetTypeReferenceName(TypeReference typeRef) -> string`

**What it does**: Get full type name from a `TypeReference`.

**Handles**:
- `NamedTypeReference` → returns `FullName`
- `NestedTypeReference` → returns `FullReference.FullName`
- Other types → returns `ToString()` or `"Unknown"`

---

## File: OverloadUnifier.cs

### Purpose

Unify method overloads that differ only in ways TypeScript cannot distinguish. Runs after Plan phase, before PhaseGate.

**Problem**:
C# allows overloads differing by:
- `ref`/`out` modifiers (TypeScript doesn't support)
- Generic constraints (TypeScript has weaker constraint system)

These create duplicate signatures in TypeScript.

**Solution**:
1. Group methods by TypeScript erasure key (name, arity, param count)
2. Pick the "widest" signature (most permissive in TypeScript)
3. Mark narrower ones as `EmitScope.Omitted`

### Method: `UnifyOverloads(BuildContext, SymbolGraph) -> SymbolGraph`

**What it does**:

1. Iterate through all namespaces and types
2. Call `UnifyTypeOverloads()` for each type
3. Collect metrics (total unified, types processed)
4. Return new graph with `Namespaces` updated
5. Call `graph.WithIndices()` to rebuild lookup indices

**Returns**: Pure transformation - new graph with unified overloads

### Private Method: `UnifyTypeOverloads(TypeSymbol type) -> (TypeSymbol, int)`

**Algorithm**:

1. **Group Methods by Erasure Key**:
   - Filter to `EmitScope.ClassSurface` or `EmitScope.StaticSurface` methods
   - Group by `ComputeErasureKey()`
   - Keep only groups with 2+ methods (collisions)

2. **For Each Group**:
   - Call `SelectWidestSignature()` to pick best method
   - Mark all other methods as `EmitScope.Omitted`
   - Update method in list

3. **Return**: Updated type and count of unified methods

**Returns**: Tuple of (updated type, unified count)

### Private Method: `ComputeErasureKey(MethodSymbol method) -> string`

**What it does**: Compute TypeScript erasure key for collision detection.

**Format**: `"name|arity|paramCount"`

**Example**:
```csharp
// Method: void Write(ref int value)
ComputeErasureKey(method)  // → "write|0|1"

// Method: void Write(int value)
ComputeErasureKey(method)  // → "write|0|1"

// COLLISION: Same erasure key!
```

**Components**:
- `name`: `method.TsEmitName` (already transformed to camelCase)
- `arity`: `method.Arity` (generic parameter count)
- `paramCount`: `method.Parameters.Length`

### Private Method: `SelectWidestSignature(List<MethodSymbol> overloads) -> MethodSymbol`

**What it does**: Select the widest (most permissive) signature from overload group.

**Preference Order**:
1. **Fewer ref/out parameters** (TypeScript doesn't support)
2. **Fewer generic constraints** (TypeScript has weaker constraints)
3. **First in declaration order** (stable tie-breaker using StableId)

**Algorithm**:
1. Score each method:
   - `RefOutCount` = count of ref/out parameters
   - `ConstraintCount` = sum of constraints on generic parameters
2. Sort by: `RefOutCount` ASC, `ConstraintCount` ASC, `StableId` ASC
3. Return first (widest) method

**Example**:
```csharp
// Overload 1: void Write(int value)        RefOut=0, Constraints=0
// Overload 2: void Write(ref int value)    RefOut=1, Constraints=0
// Overload 3: void Write<T>(T value) where T : struct  RefOut=0, Constraints=1

// Selection: Overload 1 (fewest ref/out, fewest constraints)
```

### Private Method: `CountRefOutParameters(MethodSymbol method) -> int`

**What it does**: Count ref and out parameters in a method.

**Returns**: `method.Parameters.Count(p => p.IsRef || p.IsOut)`

### Private Method: `CountGenericConstraints(MethodSymbol method) -> int`

**What it does**: Count total generic constraints on method type parameters.

**Returns**: `method.GenericParameters.Sum(gp => gp.Constraints.Length)`

---

## File: SignatureNormalization.cs

### Purpose

Creates normalized, canonical signatures for complete member matching. Used across:
- `BindingEmitter` (`bindings.json`)
- `MetadataEmitter` (`metadata.json`)
- `StructuralConformance` (interface matching)
- `ViewPlanner` (member filtering)

**Ensures**: All components use the **same canonical format** for signature matching.

### Method: `NormalizeMethod(MethodSymbol method) -> string`

**Format**: `"MethodName|arity=N|(param1:kind,param2:kind)|->ReturnType|static=bool"`

**Example**: `"CompareTo|arity=0|(T:in)|->int|static=false"`

**Components**:
1. Method name (CLR name, not TS name)
2. Generic arity: `arity=N`
3. Parameters with kinds:
   - Type name (normalized)
   - Kind: `in`, `out`, `ref`, `params`
   - Optionality: `?` suffix if `HasDefaultValue`
4. Return type: `->` followed by normalized type name
5. Static flag: `static=true/false`

**Example**:
```csharp
// Method: static int Parse(string s, out int result)
NormalizeMethod(method)
// → "Parse|arity=0|(string:in,int:out)|->int|static=true"
```

### Method: `NormalizeProperty(PropertySymbol property) -> string`

**Format**: `"PropertyName|(indexParam1,indexParam2)|->PropertyType|static=bool|accessor=get/set/getset"`

**Example**: `"Count|->int|static=false|accessor=get"`
**Example**: `"Item|(int)|->T|static=false|accessor=getset"`

**Components**:
1. Property name (CLR name)
2. Index parameters (for indexers): `(type1,type2)` or empty
3. Property type: `->` followed by normalized type name
4. Static flag: `static=true/false`
5. Accessor type: `get`, `set`, `getset`, `none`

### Method: `NormalizeField(FieldSymbol field) -> string`

**Format**: `"FieldName|->FieldType|static=bool|const=bool"`

**Example**: `"MaxValue|->int|static=true|const=true"`

**Components**:
1. Field name (CLR name)
2. Field type: `->` followed by normalized type name
3. Static flag: `static=true/false`
4. Const flag: `const=true/false`

### Method: `NormalizeEvent(EventSymbol evt) -> string`

**Format**: `"EventName|->DelegateType|static=bool"`

**Example**: `"Click|->EventHandler|static=false"`

**Components**:
1. Event name (CLR name)
2. Delegate type: `->` followed by normalized type name
3. Static flag: `static=true/false`

### Method: `NormalizeConstructor(ConstructorSymbol ctor) -> string`

**Format**: `"constructor|(param1:kind,param2:kind)|static=bool"`

**Example**: `"constructor|(int:in,string:in)|static=false"`

**Components**:
1. Constructor keyword: `"constructor"`
2. Parameters with kinds (same format as methods)
3. Static flag: `static=true/false` (static constructors exist)

### Private Method: `NormalizeTypeName(string typeName) -> string`

**What it does**: Normalize type name for signature matching.

**Transformations**:
1. Remove all whitespace
2. Normalize generic backtick: `` ` `` → `_` (`List`1` → `List_1`)

**Example**:
```csharp
NormalizeTypeName("List`1<T>")  // → "List_1<T>"
NormalizeTypeName("Dictionary`2")  // → "Dictionary_2"
```

---

## Key Algorithms

### Dual-Scope Naming (Class vs View)

The Normalize phase uses a sophisticated dual-scope algorithm to handle the fact that a single CLR member can have different TypeScript names depending on where it's accessed:

**Class Surface Scope**:
- Used for members emitted on class declaration
- Scope key: `ns:TypeStableId#instance` or `ns:TypeStableId#static`
- Collision resolution: Numeric suffixes (`toInt`, `toInt2`, `toInt3`)

**View Surface Scope**:
- Used for members emitted on interface views
- Scope key: `ns:TypeStableId:InterfaceStableId#instance` or `ns:TypeStableId:InterfaceStableId#static`
- Separate scope per interface (allows different implementations)
- Collision resolution: `$view` suffix first, then numeric (`toInt$view`, `toInt$view2`)

**Example**:
```csharp
// C#
class Array : IEnumerable<T>, ICollection<T>
{
    public ArrayEnumerator GetEnumerator() { ... }  // Class surface

    IEnumerator<T> IEnumerable<T>.GetEnumerator() { ... }  // ViewOnly
    IEnumerator IEnumerable.GetEnumerator() { ... }        // ViewOnly
}

// TypeScript
class Array_1<T> {
    // Class surface (ClassSurface scope)
    getEnumerator(): ArrayEnumerator;

    // View for IEnumerable<T> (View scope)
    readonly asIEnumerable_1: {
        getEnumerator$view(): IEnumerator_1<T>;
    };

    // View for IEnumerable (View scope)
    readonly asIEnumerable: {
        getEnumerator$view(): IEnumerator;  // Same $view name (different view scope)
    };
}
```

### Collision Detection and Resolution

**Class Surface Collisions**:
1. First member with base name gets it: `toInt`
2. Subsequent collisions get numeric suffixes: `toInt2`, `toInt3`
3. Renamer maintains counters per scope

**View-vs-Class Collisions**:
1. Peek at what view member would get: `Renamer.PeekFinalMemberName(viewScope, requested, isStatic)`
2. Check if peek result exists in `classAllNames` set
3. If collision: apply `$view` suffix
4. If `$view` also taken in view scope: try `$view2`, `$view3`, etc.

**Algorithm**:
```csharp
var peek = ctx.Renamer.PeekFinalMemberName(viewScope, "toInt", false);
// peek = "toInt" (first in view scope)

if (classAllNames.Contains(peek)) {
    var finalRequested = "toInt$view";

    // Check if $view is also taken in view scope
    while (ctx.Renamer.IsNameTaken(viewScope, finalRequested, false)) {
        suffix++;
        finalRequested = "toInt$view" + suffix;
    }

    ctx.Renamer.ReserveMemberName(stableId, finalRequested, viewScope, ...);
}
```

### CamelCase Transformation

Performed by the Renamer component (not in Normalize phase):

**Rules**:
1. First character lowercase (unless all-caps acronym)
2. Preserve internal capitalization
3. Special handling for acronyms: `URL` → `url`, `HTTPClient` → `httpClient`

**Examples**:
- `ToString` → `toString`
- `CompareTo` → `compareTo`
- `GetHashCode` → `getHashCode`
- `URL` → `url`
- `HTTPClient` → `httpClient`

### Reserved Word Sanitization

Performed by `TypeScriptReservedWords.Sanitize()`:

**TypeScript Reserved Words**:
- Keywords: `break`, `case`, `catch`, `class`, `const`, `continue`, `debugger`, `default`, `delete`, `do`, `else`, `enum`, `export`, `extends`, `false`, `finally`, `for`, `function`, `if`, `import`, `in`, `instanceof`, `new`, `null`, `return`, `super`, `switch`, `this`, `throw`, `true`, `try`, `typeof`, `var`, `void`, `while`, `with`, `yield`
- Strict mode: `implements`, `interface`, `let`, `package`, `private`, `protected`, `public`, `static`, `yield`
- Future reserved: `async`, `await`

**Sanitization**: Append `_` suffix

**Examples**:
- `delete` → `delete_`
- `in` → `in_`
- `static` → `static_`
- `await` → `await_`

---

## Pipeline Integration

**Normalize Phase Position**: Between Shape and Plan

```
Shape Phase (Phase 4)
  ↓
  Sets EmitScope on all members
  Plans explicit interface views
  Marks ViewOnly vs ClassSurface
  ↓
Normalize Phase (Phase 5)  ← YOU ARE HERE
  ↓
  Reserves all TypeScript names
  Sets TsEmitName on all symbols
  Unifies overloads
  Validates completeness
  ↓
Plan Phase (Phase 6)
  ↓
  Uses TsEmitName for emission planning
  Generates final emission instructions
```

**Key Invariants**:
- **Input**: All members must have `EmitScope` set (Unspecified = error)
- **Input**: ViewOnly members must have `SourceInterface` set
- **Output**: All emitted members have `TsEmitName` set
- **Output**: All emitted members have rename decision in Renamer
- **Output**: Colliding overloads unified (narrower ones omitted)

**PhaseGate Validation** (after Normalize):
- `PG_FIN_001`: TsEmitName must be set on all emitted members
- `PG_FIN_002`: TsEmitName must not contain invalid characters
- `PG_FIN_003`: All emitted members must have rename decision (checked by audit)

---

## Summary

The Normalize phase is the **central name assignment phase** that:

1. **Reserves Names**: All TypeScript names assigned through central Renamer
2. **Dual-Scope Algorithm**: Separate scopes for class surface vs. view surface
3. **Collision Detection**: Class-vs-view collisions resolved with `$view` suffix
4. **Overload Unification**: Indistinguishable overloads unified to widest signature
5. **Completeness Validation**: Audit ensures every member has a rename decision

**Key Design Principles**:
- **Centralized Reservation**: Only Normalize reserves names - all others retrieve
- **Pure Transformation**: Returns new graph, no mutation
- **Fail-Fast Validation**: Audit throws if any members missing decisions
- **Deterministic Ordering**: All iterations use stable ordering for reproducibility
- **Scope Consistency**: Same scope construction in reservation, application, audit
