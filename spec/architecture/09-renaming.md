# 09 - Renaming System

## Overview

The Renaming system is the **centralized naming authority** for the entire generation pipeline. All TypeScript identifiers flow through `SymbolRenamer`, which:

- **Materializes final TS identifiers** for types and members
- **Records every rename** with full provenance (`RenameDecision`)
- **Provides deterministic suffix allocation** for collision resolution
- **Separates static and instance member scopes** to prevent false collisions
- **Supports dual-scope reservations** (class surface + view surface for same member)

**Key Principle**: No component guesses names. All names are reserved during planning phases and looked up during emission.

## File: SymbolRenamer.cs

### Purpose

Central renaming service with dual-scope algorithm. Manages name reservations across multiple scope types (namespace, class surface, view surface) with separate static/instance tracking.

### Class: SymbolRenamer

**Properties (private fields)**:
- `_tablesByScope` - `Dictionary<string, NameReservationTable>` - Scope key to reservation table mapping
- `_decisions` - `Dictionary<(StableId Id, string ScopeKey), RenameDecision>` - Records all rename decisions keyed by StableId + scope
- `_explicitOverrides` - `Dictionary<StableId, string>` - CLI/user-specified name overrides
- `_typeStyleTransform` - `Func<string, string>` - Style transform for type names (e.g., PascalCase)
- `_memberStyleTransform` - `Func<string, string>` - Style transform for member names (e.g., camelCase)

**M5 CRITICAL FIX**: The `_decisions` dictionary was changed from keying by `StableId` alone to `(StableId, ScopeKey)` to support dual-scope reservations. This allows the same member to have different final names in class scope vs view scope.

### Method: ApplyExplicitOverrides()

```csharp
public void ApplyExplicitOverrides(IReadOnlyDictionary<string, string> explicitMap)
```

Applies explicit CLI/user overrides for symbol names. Called first, before any other reservations.

**Parameters**:
- `explicitMap` - Map from CLR path (e.g., "System.String") to desired TypeScript name

**Behavior**:
- Stores overrides in `_explicitOverrides` dictionary
- During reservation, explicit overrides take precedence over style transforms
- If explicit name collides, falls back to numeric suffix strategy

### Method: AdoptTypeStyleTransform()

```csharp
public void AdoptTypeStyleTransform(Func<string, string> transform)
```

Adopts a style transform for TYPE names (classes, interfaces, enums). Called once during context setup, before any reservations.

**Parameters**:
- `transform` - Function to transform type names (e.g., `s => PascalCase(s)`)

**Example**:
```csharp
renamer.AdoptTypeStyleTransform(s => NameTransformation.PascalCase(s));
// "myClass" → "MyClass"
```

### Method: AdoptMemberStyleTransform()

```csharp
public void AdoptMemberStyleTransform(Func<string, string> transform)
```

Adopts a style transform for MEMBER names (methods, properties, fields). Called once during context setup, before any reservations.

**Parameters**:
- `transform` - Function to transform member names (e.g., `s => camelCase(s)`)

**Example**:
```csharp
renamer.AdoptMemberStyleTransform(s => NameTransformation.CamelCase(s));
// "MyMethod" → "myMethod"
```

### Method: ReserveTypeName()

```csharp
public void ReserveTypeName(
    StableId stableId,
    string requested,
    RenameScope scope,
    string reason,
    string decisionSource = "Unknown")
```

Reserves a type name in a namespace scope. Applies the type style transform.

**Parameters**:
- `stableId` - Stable identifier for the type (includes assembly + CLR full name)
- `requested` - Desired TypeScript name (before transform)
- `scope` - Namespace scope (must be `NamespaceScope`)
- `reason` - Why this name is being reserved (e.g., "NameTransform(PascalCase)")
- `decisionSource` - Which component made this decision (e.g., "TypePlanner")

**Algorithm**:
1. Get or create reservation table for scope
2. Apply explicit overrides (if any)
3. Apply type style transform to requested name
4. Sanitize for TypeScript reserved words (adds trailing underscore)
5. Try to reserve sanitized name
6. If collision, apply numeric suffix strategy (name2, name3, etc.)
7. Record decision in `_decisions` dictionary

**Collision Handling**:
- First call for "Foo" → "Foo"
- Second call for "Foo" → "Foo2"
- Third call for "Foo" → "Foo3"

**Example**:
```csharp
renamer.ReserveTypeName(
    typeStableId,
    "MyClass",
    ScopeFactory.Namespace("System.Collections.Generic", NamespaceArea.Internal),
    "NameTransform(PascalCase)",
    "TypePlanner");
```

### Method: ReserveMemberName()

```csharp
public void ReserveMemberName(
    StableId stableId,
    string requested,
    RenameScope scope,
    string reason,
    bool isStatic,
    string decisionSource = "Unknown")
```

Reserves a member name in a type scope. Static and instance members are tracked separately. Applies the member style transform.

**Parameters**:
- `stableId` - Stable identifier for the member
- `requested` - Desired TypeScript name (before transform)
- `scope` - Type scope (must be `TypeScope` - either class base or view base)
- `reason` - Why this name is being reserved
- `isStatic` - True for static members, false for instance members
- `decisionSource` - Which component made this decision

**Dual-Scope Reservation**:
- Creates sub-scope: `{baseScope}#static` or `{baseScope}#instance`
- Class members: `type:System.String#instance`, `type:System.String#static`
- View members: `view:{TypeStableId}:{InterfaceStableId}#instance`

**Algorithm**:
1. Create effective scope with `#static` or `#instance` suffix
2. Get or create reservation table for effective scope
3. Apply explicit overrides (if any)
4. Apply member style transform to requested name
5. Sanitize for TypeScript reserved words
6. Try to reserve sanitized name
7. If collision, check if explicit interface implementation:
   - Extract interface short name from qualified member name
   - Try: `{base}_{InterfaceName}` (e.g., `get_ICollection`)
   - If still collides, apply numeric suffix
8. If not explicit interface impl, apply standard numeric suffix
9. Record decision in `_decisions` with scope key

**Example**:
```csharp
// Class surface member
renamer.ReserveMemberName(
    memberStableId,
    "ToString",
    ScopeFactory.ClassBase(typeSymbol),
    "NameTransform(CamelCase)",
    isStatic: false,
    "MemberPlanner");
// Reserves in scope: "type:System.String#instance"

// View surface member (explicit interface impl)
renamer.ReserveMemberName(
    memberStableId,
    "System.Collections.ICollection.Count",
    ScopeFactory.ViewBase(typeSymbol, interfaceStableId),
    "ExplicitInterfaceImplementation",
    isStatic: false,
    "ViewPlanner");
// Reserves in scope: "view:{TypeStableId}:{InterfaceStableId}#instance"
```

### Method: GetFinalTypeName()

```csharp
public string GetFinalTypeName(TypeSymbol type, NamespaceArea area = NamespaceArea.Internal)
```

Gets the final TypeScript name for a type (SAFE API - use this). Automatically derives the correct namespace scope from the type.

**Parameters**:
- `type` - Type symbol to look up
- `area` - Namespace area (Public or Internal)

**Returns**: Final TypeScript identifier

**Throws**: `InvalidOperationException` if name was not reserved

**Usage**:
```csharp
string tsName = renamer.GetFinalTypeName(typeSymbol);
// Returns: "MyClass" or "MyClass2" (if collision)
```

### Method: GetFinalTypeNameCore()

```csharp
internal string GetFinalTypeNameCore(StableId stableId, NamespaceScope scope)
```

Gets the final TypeScript name for a type (INTERNAL CORE - do not call directly). Callers should use `GetFinalTypeName(TypeSymbol, NamespaceArea)` instead.

**Parameters**:
- `stableId` - Stable identifier for the type
- `scope` - Namespace scope (must be `NamespaceScope`)

**Returns**: Final TypeScript identifier

**Throws**: `InvalidOperationException` if name was not reserved in this scope

**Algorithm**:
1. Validate scope is a namespace scope
2. Look up decision by `(stableId, scope.ScopeKey)` tuple
3. Return `decision.Final`
4. If not found, throw with diagnostic message

### Method: GetFinalMemberName()

```csharp
public string GetFinalMemberName(StableId stableId, RenameScope scope)
```

Gets the final TypeScript name for a member. **M5 FIX**: Now scope-aware - different scopes (class vs view) return different names.

**CRITICAL**: Scope must be a SURFACE scope (with `#static` or `#instance` suffix). Use `ScopeFactory.ClassSurface/ViewSurface` for lookups.

**Parameters**:
- `stableId` - Stable identifier for the member
- `scope` - Surface scope (must end with `#instance` or `#static`)

**Returns**: Final TypeScript identifier for this member in this scope

**Throws**: `InvalidOperationException` if name was not reserved in this scope

**Validation**:
- Asserts scope format (must be surface scope)
- DEBUG: For view scopes, validates intentionality
- PhaseGate validation (PG_SCOPE_004) catches scope/EmitScope mismatches

**Algorithm**:
1. Validate scope format (must be surface scope)
2. Look up decision by `(stableId, scope.ScopeKey)` tuple
3. Return `decision.Final`
4. If not found, list available scopes for diagnostics and throw

**Example**:
```csharp
// Class surface lookup
string className = renamer.GetFinalMemberName(
    memberStableId,
    ScopeFactory.ClassSurface(typeSymbol, isStatic: false));
// Returns: "toString" (if collision: "toString2")

// View surface lookup (different name possible)
string viewName = renamer.GetFinalMemberName(
    memberStableId,
    ScopeFactory.ViewSurface(typeSymbol, interfaceStableId, isStatic: false));
// Returns: "count_ICollection" (explicit interface impl name)
```

### Method: TryGetDecision()

```csharp
public bool TryGetDecision(StableId stableId, RenameScope scope, out RenameDecision? decision)
```

Tries to get the rename decision for a StableId in a specific scope. **M5 FIX**: Now requires scope parameter since members can be reserved in multiple scopes.

**CRITICAL**: Scope must be a SURFACE scope (with `#static` or `#instance` suffix).

**Parameters**:
- `stableId` - Stable identifier to look up
- `scope` - Surface scope (must end with `#instance` or `#static`)
- `decision` - Out parameter for the rename decision

**Returns**: True if decision found, false otherwise

**Usage**:
```csharp
if (renamer.TryGetDecision(memberStableId, scope, out var decision))
{
    Console.WriteLine($"Renamed from {decision.From} to {decision.Final}");
}
```

### Method: GetAllDecisions()

```csharp
public IReadOnlyCollection<RenameDecision> GetAllDecisions()
```

Gets all rename decisions (for metadata/bindings emission).

**Returns**: Collection of all rename decisions recorded

**Usage**: Used by emitters to generate bindings.json and metadata about name transformations.

### Method: HasFinalTypeName()

```csharp
public bool HasFinalTypeName(StableId stableId, NamespaceScope scope)
```

Checks if a type name has been reserved in the specified namespace scope. Returns true if a rename decision exists for this type.

**Parameters**:
- `stableId` - Stable identifier for the type
- `scope` - Namespace scope

**Returns**: True if reserved, false otherwise

**Usage**: Used by PhaseGate to verify all types have names before emission.

### Method: HasFinalMemberName()

```csharp
public bool HasFinalMemberName(StableId stableId, TypeScope scope)
```

Checks if a member name has been reserved in the CLASS surface scope.

**CRITICAL**: Scope must be a SURFACE scope (with `#static` or `#instance` suffix). Use `ScopeFactory.ClassSurface(type, isStatic)` to create the scope.

**Parameters**:
- `stableId` - Stable identifier for the member
- `scope` - Class surface scope

**Returns**: True if reserved in class scope, false otherwise

**Throws**: `InvalidOperationException` if called with view scope (use `HasFinalViewMemberName` instead)

### Method: HasFinalViewMemberName()

```csharp
public bool HasFinalViewMemberName(StableId stableId, TypeScope scope)
```

Checks if a member name has been reserved in the VIEW surface scope.

**CRITICAL**: Scope must be a SURFACE scope (with `#static` or `#instance` suffix). Use `ScopeFactory.ViewSurface(type, interfaceStableId, isStatic)` to create the scope.

**Parameters**:
- `stableId` - Stable identifier for the member
- `scope` - View surface scope

**Returns**: True if reserved in view scope, false otherwise

**Throws**: `InvalidOperationException` if called with class scope (use `HasFinalMemberName` instead)

### Method: IsNameTaken()

```csharp
public bool IsNameTaken(RenameScope scope, string name, bool isStatic)
```

Checks if a name is already reserved in a specific scope. Used for collision detection when reserving view members.

**Parameters**:
- `scope` - Base scope (will be adjusted for static/instance)
- `name` - Name to check
- `isStatic` - True for static sub-scope, false for instance

**Returns**: True if name is taken, false if available

**Usage**:
```csharp
if (renamer.IsNameTaken(scope, "count", isStatic: false))
{
    // Name collision - need to use different name
}
```

### Method: ListReservedNames()

```csharp
public HashSet<string> ListReservedNames(RenameScope scope, bool isStatic)
```

Lists all reserved names in a scope. Returns the actual final names from the reservation table (after suffix resolution).

**Parameters**:
- `scope` - Base scope (will be adjusted for static/instance)
- `isStatic` - True for static sub-scope, false for instance

**Returns**: HashSet of all reserved names in the scope

**Usage**:
```csharp
var reserved = renamer.ListReservedNames(
    ScopeFactory.ClassBase(typeSymbol),
    isStatic: false);
// Returns: { "toString", "equals", "getHashCode" }
```

### Method: PeekFinalMemberName()

```csharp
public string PeekFinalMemberName(RenameScope scope, string requestedBase, bool isStatic)
```

Peeks at what final name would be assigned in a scope without committing. Used for collision detection before reservation. Applies member style transform and sanitization, then finds next available suffix if needed.

**Parameters**:
- `scope` - Base scope (will be adjusted for static/instance)
- `requestedBase` - Desired name (before transforms)
- `isStatic` - True for static sub-scope, false for instance

**Returns**: What the final name would be if reserved now

**Algorithm**:
1. Create effective scope (`#static` or `#instance`)
2. Apply member style transform
3. Sanitize for reserved words
4. If scope doesn't exist yet, return sanitized name
5. If base name available, return it
6. Otherwise, find next available suffix (2, 3, 4...) without mutating table
7. Return projected name

**Usage**:
```csharp
string projectedName = renamer.PeekFinalMemberName(
    ScopeFactory.ViewBase(typeSymbol, interfaceStableId),
    "Count",
    isStatic: false);
// Returns: "count" or "count2" (without actually reserving)
```

### Private Method: GetOrCreateTable()

```csharp
private NameReservationTable GetOrCreateTable(RenameScope scope)
```

Gets or creates a reservation table for a scope. Lazy initialization.

### Private Method: ResolveNameWithConflicts()

```csharp
private string ResolveNameWithConflicts(
    StableId stableId,
    string requested,
    NameReservationTable table,
    RenameScope scope,
    string reason,
    string decisionSource,
    bool? isStatic,
    Func<string, string> styleTransform)
```

Core name resolution algorithm with collision handling.

**Algorithm**:
1. Check for explicit override → try to reserve
2. Apply style transform (type or member specific)
3. Sanitize TypeScript reserved words (add trailing `_` if needed)
4. Try to reserve sanitized name → if success, return it
5. **Collision detected** → check if explicit interface implementation:
   - If member name contains `.` (e.g., "System.Collections.ICollection.Count")
   - Extract interface short name (e.g., "ICollection")
   - Try: `{sanitized}_{InterfaceName}` (e.g., "count_ICollection")
   - If still collides, apply numeric suffix to interface-suffixed name
6. **Not explicit interface impl** → apply standard numeric suffix:
   - Allocate next suffix from table (2, 3, 4...)
   - Try to reserve `{base}{suffix}`
   - Keep trying until successful (safety limit: 1000 attempts)
7. Return final resolved name

**Examples**:
```csharp
// Standard collision
"Compare" → "compare" (first)
"Compare" → "compare2" (second)
"Compare" → "compare3" (third)

// Explicit interface implementation
"System.Collections.ICollection.Count" → "count_ICollection"
"System.Collections.ICollection.Count" (different type) → "count_ICollection2"

// Reserved word
"switch" → "switch_"
"switch" (collision) → "switch_2"
```

### Private Method: RecordDecision()

```csharp
private void RecordDecision(RenameDecision decision)
```

Records a rename decision in the `_decisions` dictionary. **M5 FIX**: Keys by `(StableId, ScopeKey)` to support dual-scope reservations.

### Private Method: ExtractOriginalName()

```csharp
private string ExtractOriginalName(string requested)
```

Extracts the original CLR name from a requested name by removing suffixes.

**Algorithm**:
1. Remove `_new` suffix if present
2. Remove numeric suffixes by scanning backwards
3. Return base name

**Examples**:
```csharp
"compare2" → "compare"
"toString_new" → "toString"
"getHashCode3" → "getHashCode"
```

### Private Method: AssertNamespaceScope()

```csharp
private static void AssertNamespaceScope(NamespaceScope scope)
```

Validates that a scope is a valid namespace scope. Throws if scope key doesn't start with `ns:`.

**Always enabled** (not just DEBUG).

### Private Method: AssertMemberScope()

```csharp
private static void AssertMemberScope(TypeScope scope)
```

Validates that a scope is a valid member surface scope. Throws if:
- Scope key doesn't start with `type:` or `view:`
- Scope key doesn't end with `#instance` or `#static`

**Always enabled** (not just DEBUG).

## File: RenameScope.cs

### Purpose

Represents a naming scope where identifiers must be unique. Scopes prevent unrelated symbols from colliding.

### Record: RenameScope (abstract)

Base type for all scope types.

**Properties**:
- `ScopeKey` - `string` (required) - Human-readable scope identifier for debugging and dictionary keys

### Record: NamespaceScope

Scope for top-level types in a namespace.

**Properties**:
- `ScopeKey` - Inherited from `RenameScope`
- `Namespace` - `string` (required) - Full namespace name (e.g., "System.Collections.Generic")
- `IsInternal` - `bool` (required) - True for internal scope, false for facade scope

**Internal constructor** - Use `ScopeFactory.Namespace()` instead.

**Purpose**: Internal and facade are treated as separate scopes to allow clean facade names without collisions from internal names.

**Example ScopeKey**:
- `"ns:System.Collections.Generic:internal"`
- `"ns:System.Collections.Generic:public"`
- `"ns:(global):internal"`

### Record: TypeScope

Scope for members within a type. Static and instance members use separate sub-scopes.

**Properties**:
- `ScopeKey` - Inherited from `RenameScope`
- `TypeFullName` - `string` (required) - Full CLR type name (e.g., "System.Collections.Generic.List`1")
- `IsStatic` - `bool` (required) - True for static member sub-scope, false for instance member sub-scope

**Internal constructor** - Use `ScopeFactory` methods instead.

**Purpose**: Separating static/instance prevents false collision detection. TypeScript allows same name for static and instance members.

**Example ScopeKey**:
- `"type:System.String#instance"` - Instance members of System.String
- `"type:System.String#static"` - Static members of System.String
- `"view:{TypeStableId}:{InterfaceStableId}#instance"` - Explicit interface impl view

### Record: ImportAliasScope

Scope for import aliases (optional, used when aliases are exported).

**Properties**:
- `ScopeKey` - Inherited from `RenameScope`
- `TargetNamespace` - `string` (required) - Target namespace being imported

**Internal constructor** - Add factory method to `ScopeFactory` if needed.

**Currently unused** in the pipeline (reserved for future use).

## File: ScopeFactory.cs

### Purpose

Centralized scope construction for `SymbolRenamer`. **NO MANUAL SCOPE STRINGS** - all scopes must be created through these helpers.

### Constant: GlobalNamespace

```csharp
public const string GlobalNamespace = "(global)";
```

Canonical global namespace identifier (for types with null/empty namespace).

### CANONICAL SCOPE FORMATS (Authoritative)

**DO NOT DEVIATE FROM THESE FORMATS**:

| Scope Type | Format | Example |
|------------|--------|---------|
| Namespace (public) | `ns:{Namespace}:public` | `"ns:System.Collections:public"` |
| Namespace (internal) | `ns:{Namespace}:internal` | `"ns:System.Collections:internal"` |
| Class members (instance) | `type:{TypeFullName}#instance` | `"type:System.String#instance"` |
| Class members (static) | `type:{TypeFullName}#static` | `"type:System.String#static"` |
| View members (instance) | `view:{TypeStableId}:{InterfaceStableId}#instance` | `"view:mscorlib:System.String:mscorlib:System.IComparable#instance"` |
| View members (static) | `view:{TypeStableId}:{InterfaceStableId}#static` | `"view:mscorlib:System.String:mscorlib:System.IComparable#static"` |

### USAGE PATTERN

**Reservations**: Use BASE scopes (no `#instance`/`#static` suffix) - `ReserveMemberName()` adds it

**Lookups**: Use SURFACE scopes (with `#instance`/`#static` suffix) - use `ClassSurface()`/`ViewSurface()`

**M5 CRITICAL**: View members MUST be looked up with `ViewSurface()`, not `ClassSurface()`.

### Method: Namespace()

```csharp
public static NamespaceScope Namespace(string? ns, NamespaceArea area)
```

Creates namespace scope for type name resolution.

**Parameters**:
- `ns` - Namespace string (null/empty normalized to "(global)")
- `area` - `NamespaceArea.Public` or `NamespaceArea.Internal`

**Returns**: `NamespaceScope` with canonical scope key

**Format**: `"ns:{Namespace}:public"` or `"ns:{Namespace}:internal"`

**Example**:
```csharp
var scope = ScopeFactory.Namespace("System.Collections.Generic", NamespaceArea.Internal);
// scope.ScopeKey = "ns:System.Collections.Generic:internal"

var globalScope = ScopeFactory.Namespace(null, NamespaceArea.Internal);
// globalScope.ScopeKey = "ns:(global):internal"
```

### Method: ClassBase()

```csharp
public static TypeScope ClassBase(TypeSymbol type)
```

Creates BASE class scope for member reservations (no side suffix).

**Format**: `"type:{TypeFullName}"` (ReserveMemberName will add `#instance`/`#static`)

**Use for**: `ReserveMemberName()` calls

**Example**:
```csharp
var scope = ScopeFactory.ClassBase(typeSymbol);
// scope.ScopeKey = "type:System.String"

renamer.ReserveMemberName(memberStableId, "ToString", scope, "...", isStatic: false, "...");
// Reserves in: "type:System.String#instance"
```

### Method: ClassInstance()

```csharp
public static TypeScope ClassInstance(TypeSymbol type)
```

Creates FULL class scope for instance member lookups.

**Format**: `"type:{TypeFullName}#instance"`

**Use for**: `GetFinalMemberName()`, `TryGetDecision()` calls for instance members

**Example**:
```csharp
var scope = ScopeFactory.ClassInstance(typeSymbol);
// scope.ScopeKey = "type:System.String#instance"

string finalName = renamer.GetFinalMemberName(memberStableId, scope);
```

### Method: ClassStatic()

```csharp
public static TypeScope ClassStatic(TypeSymbol type)
```

Creates FULL class scope for static member lookups.

**Format**: `"type:{TypeFullName}#static"`

**Use for**: `GetFinalMemberName()`, `TryGetDecision()` calls for static members

**Example**:
```csharp
var scope = ScopeFactory.ClassStatic(typeSymbol);
// scope.ScopeKey = "type:System.String#static"

string finalName = renamer.GetFinalMemberName(memberStableId, scope);
```

### Method: ClassSurface()

```csharp
public static TypeScope ClassSurface(TypeSymbol type, bool isStatic)
```

Creates FULL class scope based on member's `isStatic` flag.

**Format**: `"type:{TypeFullName}#instance"` or `"#static"`

**Use for**: `GetFinalMemberName()`, `TryGetDecision()` calls when `isStatic` is dynamic

**Preferred over manual ternary** - cleaner call-sites.

**Example**:
```csharp
var scope = ScopeFactory.ClassSurface(typeSymbol, member.IsStatic);
string finalName = renamer.GetFinalMemberName(memberStableId, scope);
```

### Method: ViewBase()

```csharp
public static TypeScope ViewBase(TypeSymbol type, string interfaceStableId)
```

Creates BASE view scope for member reservations (no side suffix).

**Format**: `"view:{TypeStableId}:{InterfaceStableId}"` (ReserveMemberName will add `#instance`/`#static`)

**Use for**: `ReserveMemberName()` calls for ViewOnly members

**Example**:
```csharp
var scope = ScopeFactory.ViewBase(typeSymbol, interfaceStableId);
// scope.ScopeKey = "view:mscorlib:System.String:mscorlib:System.IComparable"

renamer.ReserveMemberName(memberStableId, "CompareTo", scope, "...", isStatic: false, "...");
// Reserves in: "view:mscorlib:System.String:mscorlib:System.IComparable#instance"
```

### Method: ViewSurface()

```csharp
public static TypeScope ViewSurface(TypeSymbol type, string interfaceStableId, bool isStatic)
```

Creates FULL view scope for explicit interface view member lookups.

**Format**: `"view:{TypeStableId}:{InterfaceStableId}#instance"` or `"#static"`

**Use for**: `GetFinalMemberName()`, `TryGetDecision()` calls for ViewOnly members

**M5 FIX**: This is what emitters were missing - they were using `ClassInstance()`/`ClassStatic()` for view members, causing PG_NAME_004 collisions.

**Example**:
```csharp
var scope = ScopeFactory.ViewSurface(typeSymbol, interfaceStableId, isStatic: false);
// scope.ScopeKey = "view:mscorlib:System.String:mscorlib:System.IComparable#instance"

string finalName = renamer.GetFinalMemberName(memberStableId, scope);
```

### Method: GetInterfaceStableId()

```csharp
public static string GetInterfaceStableId(TypeReference ifaceRef)
```

Extracts interface StableId from `TypeReference` (same logic as ViewPlanner). Returns assembly-qualified identifier for grouping/merging.

**HARDENING**: Uses pre-stamped `InterfaceStableId` when available (set at load time).

**Parameters**:
- `ifaceRef` - Type reference to interface

**Returns**: StableId string (e.g., `"mscorlib:System.IComparable"`)

**Algorithm**:
1. If `NamedTypeReference` with pre-stamped `InterfaceStableId` → return it
2. If `NamedTypeReference` without stamp → return `"{AssemblyName}:{FullName}"`
3. If `NestedTypeReference` → recursively build `"{DeclaringType}+{NestedName}"`
4. Otherwise → return `ToString()` or `"unknown"`

**Example**:
```csharp
string interfaceId = ScopeFactory.GetInterfaceStableId(ifaceRef);
// Returns: "mscorlib:System.Collections.Generic.IEnumerable`1"

var viewScope = ScopeFactory.ViewBase(typeSymbol, interfaceId);
```

## File: StableId.cs

### Purpose

Immutable identity for types and members BEFORE any name transformations. Used as the key for rename decisions and for bindings back to CLR.

### Record: StableId (abstract)

Base type for all stable identifiers.

**Properties**:
- `AssemblyName` - `string` (required) - Assembly name where the symbol originates

### Record: TypeStableId

Stable identity for a type.

**Properties**:
- `AssemblyName` - Inherited from `StableId`
- `ClrFullName` - `string` (required) - Full CLR type name (e.g., "System.Collections.Generic.List`1")

**ToString()**: `"{AssemblyName}:{ClrFullName}"`

**Example**:
```csharp
var stableId = new TypeStableId
{
    AssemblyName = "System.Private.CoreLib",
    ClrFullName = "System.String"
};
// stableId.ToString() = "System.Private.CoreLib:System.String"
```

### Record: MemberStableId

Stable identity for a member (method, property, field, event). Equality is based on semantic identity (excluding MetadataToken).

**Properties**:
- `AssemblyName` - Inherited from `StableId`
- `DeclaringClrFullName` - `string` (required) - Full CLR name of the declaring type
- `MemberName` - `string` (required) - Member name as it appears in CLR metadata
- `CanonicalSignature` - `string` (required) - Canonical signature that uniquely identifies this member among overloads
- `MetadataToken` - `int?` (optional) - Optional metadata token for exact CLR correlation

**ToString()**: `"{AssemblyName}:{DeclaringClrFullName}::{MemberName}{CanonicalSignature}"`

**Canonical Signature Format**:
- **Methods**: `"(ParamType1,ParamType2):ReturnType"`
  - Example: `"(System.Int32,System.String):System.Boolean"`
- **Properties**: `"(IndexerParamTypes)"`
  - Non-indexer: `"()"`
  - Indexer: `"(System.Int32)"`
- **Fields/Events**: `""`

**Equality**: Based on AssemblyName, DeclaringClrFullName, MemberName, and CanonicalSignature. **MetadataToken intentionally excluded** to support semantic equality across reflection contexts.

**Example**:
```csharp
var stableId = new MemberStableId
{
    AssemblyName = "System.Private.CoreLib",
    DeclaringClrFullName = "System.String",
    MemberName = "Substring",
    CanonicalSignature = "(System.Int32):System.String",
    MetadataToken = 0x06001234
};
// stableId.ToString() = "System.Private.CoreLib:System.String::Substring(System.Int32):System.String"

// Two MemberStableIds with same semantic identity but different tokens are equal:
var id1 = new MemberStableId { AssemblyName = "A", DeclaringClrFullName = "B", MemberName = "C", CanonicalSignature = "D", MetadataToken = 1 };
var id2 = new MemberStableId { AssemblyName = "A", DeclaringClrFullName = "B", MemberName = "C", CanonicalSignature = "D", MetadataToken = 2 };
// id1.Equals(id2) == true
```

## File: RenameDecision.cs

### Purpose

Records a single rename decision with full provenance. Captures: what changed, why, how, and who decided.

### Record: RenameDecision

Immutable record of a name transformation.

**Properties**:
- `Id` - `StableId` (required) - The stable identifier for the symbol being renamed
- `Requested` - `string?` - What the caller requested (post-style transform). Null if original name with no transformation
- `Final` - `string` (required) - The final resolved TypeScript identifier
- `From` - `string` (required) - Original CLR logical name (pre-style transform, for traceability)
- `Reason` - `string` (required) - Why this rename was needed
- `DecisionSource` - `string` (required) - Which component made this decision
- `Strategy` - `string` (required) - How conflicts were resolved
- `SuffixIndex` - `int?` - When NumericSuffix strategy applies, the numeric index (2, 3, 4...)
- `ScopeKey` - `string` (required) - Textual scope identifier for debugging
- `IsStatic` - `bool?` - True if this member is static (important for static-side tracking). Null for type-level renames
- `Note` - `string?` - Optional human-readable note for complex decisions

**Reason Examples**:
- `"NameTransform(CamelCase)"`
- `"HiddenNewConflict"`
- `"StaticSideNameCollision"`
- `"ExplicitUserOverride"`
- `"InterfaceSynthesis"`
- `"StructuralConformanceView"`
- `"ReturnTypeConflictNormalization"`

**DecisionSource Examples**:
- `"HiddenMemberPlanner"`
- `"InterfaceSynthesis"`
- `"ImportPlanner"`
- `"StructuralConformance"`
- `"CLI"`
- `"TypePlanner"`
- `"MemberPlanner"`
- `"ViewPlanner"`

**Strategy Values**:
- `"None"` - No transformation needed (name was available as-is)
- `"NumericSuffix"` - Applied numeric suffix due to collision (e.g., "compare2")
- `"FixedSuffix"` - Applied fixed suffix (e.g., "_ICollection" for explicit interface impl)
- `"Error"` - Could not resolve (should not appear in successful generation)

**Example**:
```csharp
var decision = new RenameDecision
{
    Id = memberStableId,
    Requested = "toString",
    Final = "toString2",
    From = "ToString",
    Reason = "NameTransform(CamelCase)+Collision",
    DecisionSource = "MemberPlanner",
    Strategy = "NumericSuffix",
    SuffixIndex = 2,
    ScopeKey = "type:System.Object#instance",
    IsStatic = false,
    Note = "Collision with base class member"
};
```

## File: NameReservationTable.cs

### Purpose

Internal structure for tracking name reservations within a scope. Manages collision detection and numeric suffix allocation.

### Class: NameReservationTable

**Private Fields**:
- `_finalNameToId` - `Dictionary<string, StableId>` - Maps final TypeScript name to owning StableId
- `_nextSuffixByBase` - `Dictionary<string, int>` - Tracks next available numeric suffix for each base name

### Method: IsReserved()

```csharp
public bool IsReserved(string finalName)
```

Checks if a name is already reserved in this scope.

**Returns**: True if name is taken, false if available

### Method: GetOwner()

```csharp
public StableId? GetOwner(string finalName)
```

Gets the StableId that owns a reserved name, or null if not reserved.

**Returns**: StableId owner or null

### Method: TryReserve()

```csharp
public bool TryReserve(string finalName, StableId id)
```

Reserves a name for a StableId. Returns true if successful, false if already taken. If the same StableId tries to reserve the same name again, returns true (idempotent).

**Parameters**:
- `finalName` - Name to reserve
- `id` - StableId claiming this name

**Returns**: True if reserved (or already owned by same id), false if collision

**Idempotency**: Allows same StableId to re-reserve same name (returns true). Different StableId trying to take already-reserved name returns false.

**Algorithm**:
1. If name already reserved:
   - If owned by same StableId → return true (idempotent)
   - If owned by different StableId → return false (collision)
2. Reserve name for this StableId → return true

### Method: AllocateNextSuffix()

```csharp
public int AllocateNextSuffix(string baseName)
```

Allocates the next numeric suffix for a base name. First call for "compare" returns 2, second returns 3, etc.

**Parameters**:
- `baseName` - Base name to allocate suffix for

**Returns**: Next suffix index (starts at 2)

**Algorithm**:
1. Look up current suffix for base name
2. If not found, initialize to 2 (base name is implicitly "1")
3. Increment counter for next call
4. Return current suffix

**Example**:
```csharp
var table = new NameReservationTable();
table.AllocateNextSuffix("compare"); // Returns: 2
table.AllocateNextSuffix("compare"); // Returns: 3
table.AllocateNextSuffix("compare"); // Returns: 4
```

### Method: GetReservedNames()

```csharp
public IEnumerable<string> GetReservedNames()
```

Gets all reserved names (for debugging/diagnostics).

**Returns**: Enumerable of reserved name strings

### Method: GetAllReservedNames()

```csharp
public HashSet<string> GetAllReservedNames()
```

Gets all reserved names as a HashSet for efficient collision detection.

**Returns**: HashSet with Ordinal string comparison

### Property: Count

```csharp
public int Count
```

Gets the count of reserved names in this table.

## File: TypeScriptReservedWords.cs

### Purpose

TypeScript reserved word handling and sanitization. Provides pure functions for detecting and escaping TypeScript keywords.

### Reserved Words List

```csharp
private static readonly HashSet<string> ReservedWords
```

Case-insensitive HashSet containing all TypeScript reserved words:

**Core Keywords**:
- `break`, `case`, `catch`, `class`, `const`, `continue`, `debugger`, `default`
- `delete`, `do`, `else`, `enum`, `export`, `extends`, `false`, `finally`
- `for`, `function`, `if`, `import`, `in`, `instanceof`, `new`, `null`
- `return`, `super`, `switch`, `this`, `throw`, `true`, `try`, `typeof`
- `var`, `void`, `while`, `with`, `yield`

**ES6+ Keywords**:
- `let`, `static`, `implements`, `interface`, `package`, `private`, `protected`
- `public`, `as`, `async`, `await`, `constructor`, `get`, `set`

**TypeScript-Specific**:
- `from`, `of`, `namespace`, `module`, `declare`, `abstract`, `any`, `boolean`
- `never`, `number`, `object`, `string`, `symbol`, `unknown`, `type`, `readonly`

### Method: IsReservedWord()

```csharp
public static bool IsReservedWord(string name)
```

Checks if a name is a TypeScript reserved word. Case-insensitive comparison.

**Parameters**:
- `name` - Identifier to check

**Returns**: True if reserved word, false otherwise

**Example**:
```csharp
TypeScriptReservedWords.IsReservedWord("class");  // true
TypeScriptReservedWords.IsReservedWord("Class");  // true (case-insensitive)
TypeScriptReservedWords.IsReservedWord("myClass"); // false
```

### Record: SanitizeResult

Result of sanitization operation with metadata.

**Properties**:
- `Sanitized` - `string` (required) - The sanitized identifier, safe for TypeScript emission
- `Original` - `string` (required) - Original identifier before sanitization
- `WasSanitized` - `bool` (required) - True if the identifier was modified during sanitization
- `Reason` - `string?` - Reason for sanitization (e.g., "ReservedWord"). Null if no sanitization was needed

### Method: Sanitize()

```csharp
public static SanitizeResult Sanitize(string identifier)
```

Sanitizes an identifier for TypeScript emission. Reserved words get a trailing underscore suffix. Returns metadata about the sanitization for diagnostics.

**Parameters**:
- `identifier` - Identifier to sanitize

**Returns**: `SanitizeResult` with sanitized name and metadata

**Algorithm**:
1. If null/empty → return as-is with `WasSanitized = false`
2. If reserved word → add trailing `_`, return with `WasSanitized = true`, `Reason = "ReservedWord"`
3. Otherwise → return as-is with `WasSanitized = false`

**Example**:
```csharp
var result = TypeScriptReservedWords.Sanitize("switch");
// result.Sanitized = "switch_"
// result.Original = "switch"
// result.WasSanitized = true
// result.Reason = "ReservedWord"

var result2 = TypeScriptReservedWords.Sanitize("myMethod");
// result2.Sanitized = "myMethod"
// result2.WasSanitized = false
// result2.Reason = null
```

### Method: SanitizeParameterName()

```csharp
public static string SanitizeParameterName(string name)
```

Sanitizes parameter name by appending underscore suffix if it's a reserved word. Used for method/constructor parameters.

**Parameters**:
- `name` - Parameter name to sanitize

**Returns**: Sanitized name (original or with trailing `_`)

**Example**:
```csharp
TypeScriptReservedWords.SanitizeParameterName("switch"); // "switch_"
TypeScriptReservedWords.SanitizeParameterName("type");   // "type_"
TypeScriptReservedWords.SanitizeParameterName("value");  // "value"
```

### Method: EscapeIdentifier()

```csharp
public static string EscapeIdentifier(string name)
```

Escapes identifier using `$$name$$` format for Tsonic. Used for type/member names in TypeScript declarations.

**Parameters**:
- `name` - Identifier to escape

**Returns**: Escaped name (original or wrapped in `$$...$$`)

**Example**:
```csharp
TypeScriptReservedWords.EscapeIdentifier("switch"); // "$$switch$$"
TypeScriptReservedWords.EscapeIdentifier("type");   // "$$type$$"
TypeScriptReservedWords.EscapeIdentifier("myMethod"); // "myMethod"
```

**NOTE**: This method is currently unused in the pipeline. The standard approach is to use trailing `_` via `Sanitize()`.

## Key Algorithms

### 1. Dual-Scope Naming (Class vs View)

The renaming system supports **dual-scope reservations** for the same member:

**Problem**: In C#, explicit interface implementations have different names than class surface members:
```csharp
class MyClass : IComparable
{
    public int CompareTo(object? obj) { ... }  // Class surface
    int IComparable.CompareTo(object? obj) { ... }  // Explicit interface impl
}
```

**Solution**: Reserve member in TWO scopes:
1. **Class scope**: `type:MyClass#instance` → name: `"compareTo"`
2. **View scope**: `view:{TypeStableId}:{IComparable}#instance` → name: `"compareTo_IComparable"`

**Lookup**: Use correct scope for lookup:
- Emitting class body → use `ClassSurface(type, isStatic)`
- Emitting view members → use `ViewSurface(type, interfaceStableId, isStatic)`

**M5 Fix**: Changed `_decisions` dictionary from keying by `StableId` alone to `(StableId, ScopeKey)` tuple.

### 2. Collision Detection and Numeric Suffix

When a requested name is already taken:

**Algorithm**:
1. Apply style transform (type or member specific)
2. Sanitize for reserved words
3. Try to reserve base name
4. **If collision**: Check if explicit interface implementation
   - Extract interface short name from qualified name
   - Try: `{base}_{InterfaceName}`
   - If still collides, apply numeric suffix to interface-suffixed name
5. **If not explicit interface impl**: Apply standard numeric suffix
   - Allocate next suffix from table (2, 3, 4...)
   - Try to reserve `{base}{suffix}`
   - Keep trying until successful

**Example Flow**:
```
Request: "Compare"
Style transform: "compare"
Sanitize: "compare" (not reserved word)
Try reserve: Success → "compare"

Request: "Compare" (second time)
Style transform: "compare"
Sanitize: "compare"
Try reserve: COLLISION
Allocate suffix: 2
Try reserve "compare2": Success → "compare2"

Request: "System.Collections.ICollection.Count"
Extract interface: "ICollection"
Style transform: "count"
Sanitize: "count"
Try reserve: COLLISION
Try reserve "count_ICollection": Success → "count_ICollection"
```

### 3. Scope Key Construction

**Namespace Scope**:
```
Format: "ns:{Namespace}:{public|internal}"
Example: "ns:System.Collections.Generic:internal"
Global: "ns:(global):internal"
```

**Class Scope** (BASE - for reservations):
```
Format: "type:{TypeFullName}"
Example: "type:System.String"
Note: ReserveMemberName adds #instance or #static
```

**Class Scope** (SURFACE - for lookups):
```
Format: "type:{TypeFullName}#{instance|static}"
Example: "type:System.String#instance"
         "type:System.String#static"
```

**View Scope** (BASE - for reservations):
```
Format: "view:{TypeStableId}:{InterfaceStableId}"
Example: "view:mscorlib:System.String:mscorlib:System.IComparable"
Note: ReserveMemberName adds #instance or #static
```

**View Scope** (SURFACE - for lookups):
```
Format: "view:{TypeStableId}:{InterfaceStableId}#{instance|static}"
Example: "view:mscorlib:System.String:mscorlib:System.IComparable#instance"
```

**Algorithm**:
1. Choose base format based on scope type
2. Normalize namespace (null/empty → "(global)")
3. For type scopes, append `#static` or `#instance`
4. Use ordinal string comparison for lookups

### 4. Name Sanitization Pipeline

All names flow through this pipeline:

**Type Names**:
```
1. Explicit override check → if found, use override
2. Apply type style transform → e.g., PascalCase
3. Sanitize reserved words → add trailing _ if needed
4. Try to reserve → if collision, numeric suffix
5. Record decision → store in _decisions
```

**Member Names**:
```
1. Explicit override check → if found, use override
2. Apply member style transform → e.g., camelCase
3. Sanitize reserved words → add trailing _ if needed
4. Try to reserve → if collision, check explicit interface impl
   a. If explicit impl → try {base}_{InterfaceName}
   b. If still collides → numeric suffix on interface-suffixed name
   c. If not explicit impl → standard numeric suffix
5. Record decision → store in _decisions with scope key
```

**Reserved Word Handling**:
```
Input: "switch"
Check: IsReservedWord("switch") → true
Sanitize: "switch_"
```

**Numeric Suffix**:
```
Base name: "compare"
First collision: suffix = 2 → "compare2"
Second collision: suffix = 3 → "compare3"
Nth collision: suffix = N+1 → "compareN+1"
```

## Example Scenarios

### Scenario 1: Type Name Reservation

**Setup**: Reserving type `System.String` in namespace scope

**Code**:
```csharp
var typeSymbol = /* System.String TypeSymbol */;
var nsScope = ScopeFactory.Namespace("System", NamespaceArea.Internal);

renamer.ReserveTypeName(
    typeSymbol.StableId,
    "String",
    nsScope,
    "NameTransform(PascalCase)",
    "TypePlanner");
```

**Flow**:
1. Get/create table for scope `"ns:System:internal"`
2. Check explicit overrides → none
3. Apply type style transform: `"String"` → `"String"` (already PascalCase)
4. Sanitize: `"String"` → `"String"` (not reserved word)
5. Try reserve in table → SUCCESS
6. Record decision:
   ```csharp
   {
       Id = TypeStableId("System.Private.CoreLib", "System.String"),
       Requested = "String",
       Final = "String",
       From = "String",
       Reason = "NameTransform(PascalCase)",
       DecisionSource = "TypePlanner",
       Strategy = "None",
       ScopeKey = "ns:System:internal",
       IsStatic = null
   }
   ```

**Lookup**:
```csharp
string tsName = renamer.GetFinalTypeName(typeSymbol, NamespaceArea.Internal);
// Returns: "String"
```

### Scenario 2: Class Surface Method Reservation

**Setup**: Reserving instance method `ToString()` on `System.Object`

**Code**:
```csharp
var typeSymbol = /* System.Object TypeSymbol */;
var memberStableId = new MemberStableId
{
    AssemblyName = "System.Private.CoreLib",
    DeclaringClrFullName = "System.Object",
    MemberName = "ToString",
    CanonicalSignature = "():System.String"
};

var classBase = ScopeFactory.ClassBase(typeSymbol);

renamer.ReserveMemberName(
    memberStableId,
    "ToString",
    classBase,
    "NameTransform(CamelCase)",
    isStatic: false,
    "MemberPlanner");
```

**Flow**:
1. Create effective scope: `classBase` + `#instance` → `"type:System.Object#instance"`
2. Get/create table for `"type:System.Object#instance"`
3. Check explicit overrides → none
4. Apply member style transform: `"ToString"` → `"toString"`
5. Sanitize: `"toString"` → `"toString"` (not reserved word)
6. Try reserve in table → SUCCESS
7. Record decision:
   ```csharp
   {
       Id = memberStableId,
       Requested = "toString",
       Final = "toString",
       From = "ToString",
       Reason = "NameTransform(CamelCase)",
       DecisionSource = "MemberPlanner",
       Strategy = "None",
       ScopeKey = "type:System.Object#instance",
       IsStatic = false
   }
   ```

**Lookup**:
```csharp
var scope = ScopeFactory.ClassSurface(typeSymbol, isStatic: false);
string tsName = renamer.GetFinalMemberName(memberStableId, scope);
// Returns: "toString"
```

### Scenario 3: View Surface Method with Collision

**Setup**: Reserving explicit interface implementation `IComparable.CompareTo` on `System.String` where base name collides with class surface

**Code**:
```csharp
var typeSymbol = /* System.String TypeSymbol */;
var memberStableId = new MemberStableId
{
    AssemblyName = "System.Private.CoreLib",
    DeclaringClrFullName = "System.String",
    MemberName = "System.IComparable.CompareTo",
    CanonicalSignature = "(System.Object):System.Int32"
};

var interfaceStableId = "mscorlib:System.IComparable";
var viewBase = ScopeFactory.ViewBase(typeSymbol, interfaceStableId);

// Assume "compareTo" is already reserved in class scope
var classScope = ScopeFactory.ClassSurface(typeSymbol, isStatic: false);
// (earlier): renamer.ReserveMemberName(..., "CompareTo", classBase, ..., false, ...)

renamer.ReserveMemberName(
    memberStableId,
    "System.IComparable.CompareTo",
    viewBase,
    "ExplicitInterfaceImplementation",
    isStatic: false,
    "ViewPlanner");
```

**Flow**:
1. Create effective scope: `viewBase` + `#instance` → `"view:mscorlib:System.String:mscorlib:System.IComparable#instance"`
2. Get/create table for this view scope (separate from class scope)
3. Check explicit overrides → none
4. Apply member style transform: `"System.IComparable.CompareTo"` → `"system.IComparable.CompareTo"` (camelCase first char)
5. Sanitize: no reserved words
6. Try reserve `"system.icomparable.compareto"` → depends on table
7. **Check explicit interface implementation**: member name contains `.`
   - Extract interface short name: `"IComparable"` from `"System.IComparable.CompareTo"`
   - Try reserve `"compareTo_IComparable"` → SUCCESS
8. Record decision:
   ```csharp
   {
       Id = memberStableId,
       Requested = "system.icomparable.compareto",
       Final = "compareTo_IComparable",
       From = "System.IComparable.CompareTo",
       Reason = "ExplicitInterfaceImplementation",
       DecisionSource = "ViewPlanner",
       Strategy = "FixedSuffix",
       ScopeKey = "view:mscorlib:System.String:mscorlib:System.IComparable#instance",
       IsStatic = false
   }
   ```

**Lookup**:
```csharp
// Class scope lookup
var classScope = ScopeFactory.ClassSurface(typeSymbol, isStatic: false);
string className = renamer.GetFinalMemberName(classMemberStableId, classScope);
// Returns: "compareTo" (from class surface)

// View scope lookup (DIFFERENT name!)
var viewScope = ScopeFactory.ViewSurface(typeSymbol, interfaceStableId, isStatic: false);
string viewName = renamer.GetFinalMemberName(memberStableId, viewScope);
// Returns: "compareTo_IComparable" (from view surface)
```

**Key Point**: Same type, similar semantics, but **different final names** in class vs view scope. This is the dual-scope system in action.

### Scenario 4: Static vs Instance Collision Handling

**Setup**: Type has both static and instance methods with same name

**Code**:
```csharp
var typeSymbol = /* System.String TypeSymbol */;

// Instance method
var instanceStableId = new MemberStableId
{
    AssemblyName = "System.Private.CoreLib",
    DeclaringClrFullName = "System.String",
    MemberName = "Compare",
    CanonicalSignature = "(System.String,System.String):System.Int32"
};

// Static method (different signature)
var staticStableId = new MemberStableId
{
    AssemblyName = "System.Private.CoreLib",
    DeclaringClrFullName = "System.String",
    MemberName = "Compare",
    CanonicalSignature = "(System.String,System.String,System.Boolean):System.Int32"
};

var classBase = ScopeFactory.ClassBase(typeSymbol);

// Reserve instance method
renamer.ReserveMemberName(
    instanceStableId,
    "Compare",
    classBase,
    "NameTransform(CamelCase)",
    isStatic: false,
    "MemberPlanner");

// Reserve static method (SAME base name, different scope)
renamer.ReserveMemberName(
    staticStableId,
    "Compare",
    classBase,
    "NameTransform(CamelCase)",
    isStatic: true,
    "MemberPlanner");
```

**Flow**:

**Instance Reservation**:
1. Create effective scope: `"type:System.String#instance"`
2. Apply transform: `"Compare"` → `"compare"`
3. Try reserve `"compare"` in instance table → SUCCESS
4. Final name: `"compare"`

**Static Reservation**:
1. Create effective scope: `"type:System.String#static"` (DIFFERENT table)
2. Apply transform: `"Compare"` → `"compare"`
3. Try reserve `"compare"` in static table → SUCCESS
4. Final name: `"compare"`

**Result**: Both methods have final name `"compare"` because they're in separate scopes. TypeScript allows this:
```typescript
class String_ {
    compare(other: String_): int;           // Instance
    static compare(a: String_, b: String_): int;  // Static
}
```

**Lookup**:
```csharp
// Instance lookup
var instanceScope = ScopeFactory.ClassSurface(typeSymbol, isStatic: false);
string instanceName = renamer.GetFinalMemberName(instanceStableId, instanceScope);
// Returns: "compare"

// Static lookup (SAME name, different scope)
var staticScope = ScopeFactory.ClassSurface(typeSymbol, isStatic: true);
string staticName = renamer.GetFinalMemberName(staticStableId, staticScope);
// Returns: "compare"
```

**Key Point**: Static and instance members are tracked in separate scopes, preventing false collisions while allowing legitimate name reuse.

---

## Summary

The Renaming system provides:

1. **Centralized naming authority** - Single source of truth for all TypeScript identifiers
2. **Full provenance tracking** - Every rename recorded with reason and source
3. **Dual-scope support** - Same member can have different names in class vs view
4. **Static/instance separation** - Prevents false collisions
5. **Deterministic suffix allocation** - Consistent collision resolution
6. **Style transform flexibility** - Different transforms for types vs members
7. **Reserved word sanitization** - Automatic handling of TypeScript keywords
8. **Explicit interface impl support** - Special naming for explicit interface members

**Critical Rules**:
- **NO MANUAL SCOPE STRINGS** - Always use `ScopeFactory`
- **Reservations use BASE scopes** - No `#static`/`#instance` suffix
- **Lookups use SURFACE scopes** - Must have `#static`/`#instance` suffix
- **View members use ViewSurface** - Never use ClassSurface for view members
- **All names reserved before emission** - No guessing allowed
