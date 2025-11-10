# Phase 5: Normalize - Name Reservation and Signature Unification

## Overview

Central name assignment phase between Shape (Phase 4) and Plan (Phase 6).

**Responsibilities**:
1. Name Reservation - assign final TypeScript names via Renamer
2. Overload Unification - unify indistinguishable overloads

**Key Design**:
- Pure transformation - returns new graph with TsEmitName set
- Dual-scope algorithm: class surface vs view surface
- View-vs-class collision → `$view` suffix
- Unifies overloads differing by ref/out or constraints
- Fail-fast audit ensures completeness

**Input**: SymbolGraph from Shape (EmitScope set, views planned)
**Output**: SymbolGraph with TsEmitName set on all symbols

---

## NameReservation.cs

Orchestrates name reservation. ONLY place names are reserved.

### `ReserveAllNames(BuildContext, SymbolGraph) -> SymbolGraph`

**Algorithm**:
1. Reserve type names via `Renamer.ReserveTypeName()` (namespace scope)
2. Reserve class surface members via `Reservation.ReserveMemberNamesOnly()`
3. Rebuild class name sets (instance/static) for collision detection
4. Reserve view members via `Reservation.ReserveViewMemberNamesOnly()` with collision check
5. Audit completeness via `Audit.AuditReservationCompleteness()`
6. Apply names via `Application.ApplyNamesToGraph()`

**Dual-Scope Concept**:
- **Class Surface**: Members on class declaration
  - Scope: `ScopeFactory.ClassSurface(type, isStatic)`
  - Includes original, synthesized, shared interface members
- **View Surface**: Members only on interface views
  - Scope: `ScopeFactory.ViewSurface(type, interfaceStableId, isStatic)`
  - Separate scope per interface
  - Collision with class → `$view` suffix

**Static vs Instance**: TypeScript has separate namespaces, so Renamer uses separate scopes.

---

## Naming/Reservation.cs

Core reservation logic (no mutation).

### `ReserveMemberNamesOnly(BuildContext, TypeSymbol) -> (int Reserved, int Skipped)`

1. Creates base scope: `ScopeFactory.ClassBase(type)`
2. Iterates members (methods, properties, fields, events, ctors) in deterministic order
3. For each member:
   - Skip if `EmitScope.ViewOnly` (handled separately)
   - Skip if `EmitScope.Omitted`
   - Throw if `EmitScope.Unspecified`
   - Skip if already renamed (check with `Renamer.TryGetDecision()`)
   - Compute requested base via `Shared.ComputeMethodBase()` or `Shared.RequestedBaseForMember()`
   - Reserve via `Renamer.ReserveMemberName()`
4. Returns (Reserved, Skipped) counts

**Collision Resolution**: Renamer appends numeric suffixes (`toInt`, `toInt2`, `toInt3`)

### `ReserveViewMemberNamesOnly(BuildContext, SymbolGraph, TypeSymbol, HashSet<string> classAllNames) -> (int Reserved, int Skipped)`

1. Return (0,0) if no ExplicitViews
2. Iterate views (by interface StableId)
3. For each ViewOnly member:
   - Verify `EmitScope.ViewOnly`
   - Find isStatic via `Shared.FindMemberIsStatic()`
   - Compute requested base
   - Peek final name via `Renamer.PeekFinalMemberName()`
   - Check collision: peek in `classAllNames`?
   - If collision: apply `$view` suffix (or `$view2`, `$view3` if taken)
   - Reserve in view scope

**Why Separate View Scopes**: Class can implement same interface member differently via explicit implementation.

**Example**:
```csharp
class Array : IEnumerable<T> {
    public ArrayEnumerator GetEnumerator() { ... }  // Class surface
    IEnumerator<T> IEnumerable<T>.GetEnumerator() { ... }  // ViewOnly
}
```
→ TypeScript:
```typescript
class Array_1<T> {
    getEnumerator(): ArrayEnumerator;  // Class
    readonly asIEnumerable_1: {
        getEnumerator$view(): IEnumerator_1<T>;  // View with $view suffix
    };
}
```

---

## Naming/Application.cs

Apply reserved names to graph (pure transformation).

### `ApplyNamesToGraph(BuildContext, SymbolGraph) -> SymbolGraph`
Iterates namespaces, calls `ApplyNamesToNamespace()`, rebuilds indices.

### Private: `ApplyNamesToNamespace()` → `ApplyNamesToType()` → `ApplyNamesToMembers()`

**ApplyNamesToMembers logic**:
1. **ViewOnly members** (methods, properties):
   - Get interface StableId from `member.SourceInterface`
   - Create view scope: `ScopeFactory.ViewSurface()`
   - Get name: `Renamer.GetFinalMemberName(stableId, viewScope)`
2. **ClassSurface members**:
   - Create class scope: `ScopeFactory.ClassSurface()`
   - Get name: `Renamer.GetFinalMemberName(stableId, classScope)`
3. Return new member with TsEmitName set

**Critical**: Must use exact same scopes as reservation.

---

## Naming/Audit.cs

Verify completeness - every emitted member has rename decision.

### `AuditReservationCompleteness(BuildContext, SymbolGraph) -> void`

1. Iterate namespaces/types (skip compiler-generated)
2. Verify type name reserved
3. Call `AuditClassSurfaceMembers()` for class members
4. Call `AuditViewSurfaceMembers()` for view members
5. Collect errors (missing decisions)
6. Throw if errors found (fail-fast)

### Private: `AuditClassSurfaceMembers()`
- Filter to `EmitScope.ClassSurface`
- Check decision exists with class scope
- Add error if missing (PG_FIN_003)

### Private: `AuditViewSurfaceMembers()`
- Iterate ExplicitViews
- For each ViewMember:
  - Lookup in type's members for `isStatic`
  - Create view scope: `ScopeFactory.ViewSurface()`
  - Check decision exists
  - Add error if missing

---

## Naming/Shared.cs

Utility functions for name computation.

### `ComputeTypeRequestedBase(string clrName) -> string`
**Transformations**:
- `+` → `_` (nested types)
- `` ` `` → `_` (generic arity)
- Invalid chars (`<>[]`) → `_`
- Apply `TypeScriptReservedWords.Sanitize()`

**Examples**:
- `List`1` → `List_1`
- `Dictionary`2+KeyCollection` → `Dictionary_2_KeyCollection`

### `ComputeMethodBase(MethodSymbol) -> string`
**Operator Mapping**:
- `op_Equality` → `equals`
- `op_Addition` → `add`
- Unmapped → `operator_` prefix
- Applies reserved word sanitization

**Regular Methods**:
- Accessors (`get_`, `set_`, etc.) use CLR name as-is
- Others use `SanitizeMemberName()`

### `SanitizeMemberName(string) -> string`
- Replace invalid chars (`<>[]+ `) → `_`
- Apply reserved word sanitization

### `RequestedBaseForMember(string clrName) -> string`
Centralized for class and view members. Delegates to `SanitizeMemberName()`.

### `IsCompilerGenerated(string) -> bool`
Names containing `<` or `>` (e.g., `<Module>`, `<>c__DisplayClass`)

### `FindMemberIsStatic(TypeSymbol, ViewMember) -> bool`
Lookup ViewMember in type's collection to get IsStatic flag.

### `GetTypeReferenceName(TypeReference) -> string`
Handles `NamedTypeReference`, `NestedTypeReference`, fallback to ToString().

---

## OverloadUnifier.cs

Unify overloads differing only in ways TypeScript can't distinguish.

**Problem**: C# allows overloads by ref/out modifiers or generic constraints. TypeScript doesn't.

**Solution**: Group by erasure key, pick widest signature, omit narrower ones.

### `UnifyOverloads(BuildContext, SymbolGraph) -> SymbolGraph`
Iterates types, calls `UnifyTypeOverloads()`, rebuilds indices.

### Private: `UnifyTypeOverloads(TypeSymbol) -> (TypeSymbol, int)`
**Algorithm**:
1. Group methods by `ComputeErasureKey()` (name|arity|paramCount)
2. Keep groups with 2+ methods
3. For each group:
   - Call `SelectWidestSignature()`
   - Mark others as `EmitScope.Omitted`
4. Return updated type and count

### Private: `ComputeErasureKey(MethodSymbol) -> string`
Format: `"name|arity|paramCount"`

Example:
- `void Write(ref int value)` → `"write|0|1"`
- `void Write(int value)` → `"write|0|1"` (collision!)

### Private: `SelectWidestSignature(List<MethodSymbol>) -> MethodSymbol`
**Preference Order**:
1. Fewer ref/out parameters
2. Fewer generic constraints
3. First in declaration order (StableId)

**Scoring**:
- `RefOutCount` = count ref/out params
- `ConstraintCount` = sum of constraints
- Sort: RefOut ASC, Constraints ASC, StableId ASC

**Example**:
```csharp
void Write(int value)            // RefOut=0, Constraints=0 ← SELECTED
void Write(ref int value)        // RefOut=1, Constraints=0
void Write<T>(T value) where T:struct  // RefOut=0, Constraints=1
```

### Private: `CountRefOutParameters()`, `CountGenericConstraints()`
Self-explanatory.

---

## SignatureNormalization.cs

Create canonical signatures for member matching. Used by:
- `BindingEmitter`, `MetadataEmitter`, `StructuralConformance`, `ViewPlanner`

### `NormalizeMethod(MethodSymbol) -> string`
Format: `"MethodName|arity=N|(param1:kind,param2:kind)|->ReturnType|static=bool"`

**Example**: `"Parse|arity=0|(string:in,int:out)|->int|static=true"`

**Parameter Kinds**: `in`, `out`, `ref`, `params`
**Optionality**: `?` suffix if HasDefaultValue

### `NormalizeProperty(PropertySymbol) -> string`
Format: `"PropertyName|(indexParams)|->PropertyType|static=bool|accessor=get/set/getset"`

**Examples**:
- `"Count|->int|static=false|accessor=get"`
- `"Item|(int)|->T|static=false|accessor=getset"`

### `NormalizeField(FieldSymbol) -> string`
Format: `"FieldName|->FieldType|static=bool|const=bool"`

**Example**: `"MaxValue|->int|static=true|const=true"`

### `NormalizeEvent(EventSymbol) -> string`
Format: `"EventName|->DelegateType|static=bool"`

**Example**: `"Click|->EventHandler|static=false"`

### `NormalizeConstructor(ConstructorSymbol) -> string`
Format: `"constructor|(params)|static=bool"`

**Example**: `"constructor|(int:in,string:in)|static=false"`

### Private: `NormalizeTypeName(string) -> string`
- Remove whitespace
- Normalize generic backtick: `` ` `` → `_`
- `List`1<T>` → `List_1<T>`

---

## Key Algorithms

### Dual-Scope Naming

**Class Surface Scope**:
- Key: `ns:TypeStableId#instance` or `#static`
- Collision resolution: numeric suffixes (`toInt`, `toInt2`)

**View Surface Scope**:
- Key: `ns:TypeStableId:InterfaceStableId#instance` or `#static`
- Separate per interface
- Collision resolution: `$view` first, then numeric (`toInt$view`, `toInt$view2`)

### Collision Detection

**Class Surface Collisions**: Numeric suffixes by Renamer.

**View-vs-Class Collisions**:
1. Peek final name in view scope
2. Check if peek in `classAllNames`
3. If collision: apply `$view` suffix
4. If `$view` taken in view scope: `$view2`, `$view3`, etc.

**Algorithm**:
```csharp
var peek = Renamer.PeekFinalMemberName(viewScope, "toInt", false);
if (classAllNames.Contains(peek)) {
    var finalRequested = "toInt$view";
    while (Renamer.IsNameTaken(viewScope, finalRequested, false)) {
        suffix++;
        finalRequested = "toInt$view" + suffix;
    }
    Renamer.ReserveMemberName(stableId, finalRequested, viewScope, ...);
}
```

### CamelCase Transformation

Performed by Renamer (not Normalize phase).

**Rules**:
- First char lowercase (unless all-caps acronym)
- Preserve internal capitalization
- Acronym handling: `URL` → `url`, `HTTPClient` → `httpClient`

### Reserved Word Sanitization

Via `TypeScriptReservedWords.Sanitize()`:
- Keywords: `break`, `case`, `class`, `delete`, `function`, `if`, `return`, `void`, etc.
- Strict mode: `implements`, `interface`, `let`, `private`, `static`, etc.
- Future: `async`, `await`
- **Sanitization**: Append `_` suffix
- Examples: `delete` → `delete_`, `in` → `in_`, `static` → `static_`

---

## Pipeline Integration

```
Shape (Phase 4)
  ↓ Sets EmitScope, plans views
Normalize (Phase 5) ← HERE
  ↓ Reserves names, sets TsEmitName, unifies overloads
Plan (Phase 6)
  ↓ Uses TsEmitName for emission
```

**Input Invariants**:
- EmitScope set on all members (Unspecified = error)
- ViewOnly members have SourceInterface set

**Output Invariants**:
- TsEmitName set on all emitted members
- All emitted members have rename decision
- Colliding overloads unified

**PhaseGate Validation** (after Normalize):
- `PG_FIN_001`: TsEmitName set on emitted members
- `PG_FIN_002`: TsEmitName has no invalid chars
- `PG_FIN_003`: All emitted members have rename decision

---

## Summary

**Normalize Phase**:
1. **Reserves Names**: Via central Renamer
2. **Dual-Scope Algorithm**: Class vs view surface
3. **Collision Detection**: `$view` suffix for view-vs-class
4. **Overload Unification**: Widest signature wins
5. **Completeness Validation**: Audit ensures all decisions made

**Design Principles**:
- Centralized reservation (only Normalize reserves)
- Pure transformation (no mutation)
- Fail-fast validation (audit throws on missing decisions)
- Deterministic ordering (reproducibility)
- Scope consistency (same scopes in reservation, application, audit)
