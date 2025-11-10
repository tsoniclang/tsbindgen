# 09 - Renaming System

## Overview

Centralized naming authority for TypeScript identifier resolution. All names flow through `SymbolRenamer`.

**Core Functions**:
- Materializes final TS identifiers
- Records all renames with provenance (`RenameDecision`)
- Deterministic suffix allocation for collisions
- Separates static/instance scopes
- Supports dual-scope reservations (class + view for same member)

**Key Principle**: No component guesses names. Reserve during planning, lookup during emission.

## SymbolRenamer.cs

Central renaming service with dual-scope algorithm.

### State

```csharp
Dictionary<string, NameReservationTable> _tablesByScope;
Dictionary<(StableId, string ScopeKey), RenameDecision> _decisions;  // M5: tuple key for dual-scope
Dictionary<StableId, string> _explicitOverrides;
Func<string, string> _typeStyleTransform;    // e.g., PascalCase
Func<string, string> _memberStyleTransform;  // e.g., camelCase
```

### Key Methods

**Setup**:
- `ApplyExplicitOverrides(dict)` - CLI overrides, take precedence
- `AdoptTypeStyleTransform(func)` - e.g., PascalCase for types
- `AdoptMemberStyleTransform(func)` - e.g., camelCase for members

**Reservations**:
```csharp
ReserveTypeName(stableId, requested, NamespaceScope, reason, source)
  // 1. Override check → 2. Style transform → 3. Sanitize reserved words
  // 4. Try reserve → 5. Collision: numeric suffix (2, 3, ...) → 6. Record decision

ReserveMemberName(stableId, requested, TypeScope, reason, isStatic, source)
  // Creates sub-scope: {baseScope}#instance or {baseScope}#static
  // Collision: explicit interface impl → try {base}_{Interface}, else numeric suffix
```

**Lookups** (require SURFACE scope with `#instance`/`#static`):
```csharp
GetFinalTypeName(type, area) → string  // SAFE API, derives scope
GetFinalTypeNameCore(stableId, NamespaceScope) → string  // INTERNAL
GetFinalMemberName(stableId, TypeScope) → string  // M5: scope-aware lookup
TryGetDecision(stableId, scope, out decision) → bool
```

**Collision Detection**:
```csharp
IsNameTaken(scope, name, isStatic) → bool
ListReservedNames(scope, isStatic) → HashSet<string>
PeekFinalMemberName(scope, requested, isStatic) → string  // Peek without commit
```

**Validation**:
```csharp
HasFinalTypeName(stableId, NamespaceScope) → bool
HasFinalMemberName(stableId, TypeScope) → bool  // Class scope only
HasFinalViewMemberName(stableId, TypeScope) → bool  // View scope only
```

### Core Algorithm: ResolveNameWithConflicts

```csharp
private string ResolveNameWithConflicts(
    stableId, requested, table, scope, reason, source, isStatic, styleTransform)
```

**Flow**:
1. Check explicit override → try reserve
2. Apply style transform (type/member specific)
3. Sanitize TypeScript reserved words (add `_` suffix if needed)
4. Try reserve sanitized name → success: return it
5. **Collision detected** → check explicit interface impl:
   - If member name contains `.` (e.g., `System.Collections.ICollection.Count`)
   - Extract interface short name (e.g., `ICollection`)
   - Try: `{sanitized}_{Interface}` (e.g., `count_ICollection`)
   - Still collides: numeric suffix on interface-suffixed name
6. **Not explicit impl** → numeric suffix:
   - Allocate next suffix from table (2, 3, 4...)
   - Try `{base}{suffix}` until success (limit: 1000 attempts)
7. Return final resolved name

**Examples**:
```
"Compare" → "compare" (first)
"Compare" → "compare2" (second)
"System.Collections.ICollection.Count" → "count_ICollection"
"switch" → "switch_"
"switch" (collision) → "switch_2"
```

### Scope Validation (Always Enabled)

```csharp
AssertNamespaceScope(scope)  // Must start with "ns:"
AssertMemberScope(scope)     // Must start with "type:"/"view:" and end with "#instance"/"#static"
```

## RenameScope.cs

Scope types for identifier uniqueness.

### Scope Hierarchy

```csharp
abstract record RenameScope(string ScopeKey);

record NamespaceScope(string ScopeKey, string Namespace, bool IsInternal) : RenameScope(ScopeKey);
  // Internal constructor - use ScopeFactory.Namespace()
  // Examples: "ns:System.Collections.Generic:internal", "ns:(global):public"

record TypeScope(string ScopeKey, string TypeFullName, bool IsStatic) : RenameScope(ScopeKey);
  // Internal constructor - use ScopeFactory methods
  // Examples: "type:System.String#instance", "view:{TypeId}:{IfaceId}#static"

record ImportAliasScope(string ScopeKey, string TargetNamespace) : RenameScope(ScopeKey);
  // Currently unused - reserved for future
```

## ScopeFactory.cs

**CRITICAL**: All scopes created through these helpers. NO MANUAL STRINGS.

### Canonical Scope Formats

| Scope Type | Format | Example |
|------------|--------|---------|
| Namespace (public) | `ns:{Namespace}:public` | `ns:System.Collections:public` |
| Namespace (internal) | `ns:{Namespace}:internal` | `ns:System.Collections:internal` |
| Class members (instance) | `type:{TypeFullName}#instance` | `type:System.String#instance` |
| Class members (static) | `type:{TypeFullName}#static` | `type:System.String#static` |
| View members (instance) | `view:{TypeId}:{IfaceId}#instance` | `view:mscorlib:System.String:mscorlib:System.IComparable#instance` |
| View members (static) | `view:{TypeId}:{IfaceId}#static` | `view:mscorlib:System.String:mscorlib:System.IComparable#static` |

### Usage Pattern

**Reservations**: Use BASE scopes (no `#instance`/`#static`) - `ReserveMemberName()` adds suffix
**Lookups**: Use SURFACE scopes (with `#instance`/`#static`) - use `ClassSurface()`/`ViewSurface()`

### Factory Methods

```csharp
Namespace(ns, NamespaceArea) → NamespaceScope
  // Format: "ns:{Namespace}:{public|internal}"
  // null/empty normalized to "(global)"

ClassBase(type) → TypeScope
  // Format: "type:{TypeFullName}" (no suffix)
  // Use for: ReserveMemberName() calls

ClassInstance(type) → TypeScope
ClassStatic(type) → TypeScope
ClassSurface(type, isStatic) → TypeScope
  // Format: "type:{TypeFullName}#{instance|static}"
  // Use for: GetFinalMemberName(), TryGetDecision()

ViewBase(type, interfaceStableId) → TypeScope
  // Format: "view:{TypeId}:{IfaceId}" (no suffix)
  // Use for: ReserveMemberName() calls for ViewOnly members

ViewSurface(type, interfaceStableId, isStatic) → TypeScope
  // Format: "view:{TypeId}:{IfaceId}#{instance|static}"
  // Use for: GetFinalMemberName() for ViewOnly members
  // M5 FIX: Emitters were using ClassSurface() → caused PG_NAME_004 collisions

GetInterfaceStableId(TypeReference) → string
  // Extracts interface StableId for view scope construction
  // Uses pre-stamped InterfaceStableId when available
```

**Constant**: `GlobalNamespace = "(global)"` - for types with null/empty namespace

## StableId.cs

Immutable identity BEFORE name transformations. Keys for rename decisions and CLR bindings.

```csharp
abstract record StableId(string AssemblyName);

record TypeStableId(string AssemblyName, string ClrFullName) : StableId(AssemblyName)
  // ToString(): "{AssemblyName}:{ClrFullName}"
  // Example: "System.Private.CoreLib:System.String"

record MemberStableId(
    string AssemblyName,
    string DeclaringClrFullName,
    string MemberName,
    string CanonicalSignature,
    int? MetadataToken  // Optional, excluded from equality
) : StableId(AssemblyName)
  // ToString(): "{AssemblyName}:{DeclaringClrFullName}::{MemberName}{CanonicalSignature}"
  // Example: "System.Private.CoreLib:System.String::Substring(System.Int32):System.String"
  // Equality: By semantic identity (excludes MetadataToken)
```

**Canonical Signature Formats**:
- Methods: `(ParamType1,ParamType2):ReturnType`
- Properties (indexer): `(IndexerParamTypes)`
- Properties (non-indexer): `()`
- Fields/Events: `""`

## RenameDecision.cs

Records single rename with provenance.

```csharp
record RenameDecision(
    StableId Id,
    string? Requested,        // Post-style transform, null if no transformation
    string Final,             // Final resolved TS identifier
    string From,              // Original CLR name (pre-transform)
    string Reason,            // Why renamed
    string DecisionSource,    // Which component decided
    string Strategy,          // How conflicts resolved: None, NumericSuffix, FixedSuffix, Error
    int? SuffixIndex,         // Numeric index when NumericSuffix strategy
    string ScopeKey,          // Textual scope for debugging
    bool? IsStatic,           // True/false for members, null for types
    string? Note              // Optional human-readable note
);
```

**Reason Examples**: `NameTransform(CamelCase)`, `HiddenNewConflict`, `ExplicitUserOverride`, `InterfaceSynthesis`
**DecisionSource Examples**: `HiddenMemberPlanner`, `TypePlanner`, `MemberPlanner`, `ViewPlanner`, `CLI`
**Strategy Values**: `None` (no collision), `NumericSuffix` (e.g., `compare2`), `FixedSuffix` (e.g., `_ICollection`)

## NameReservationTable.cs

Internal collision detection per scope.

```csharp
Dictionary<string, StableId> _finalNameToId;       // Final name → owner
Dictionary<string, int> _nextSuffixByBase;         // Base name → next suffix
```

**Methods**:
```csharp
IsReserved(finalName) → bool
GetOwner(finalName) → StableId?
TryReserve(finalName, id) → bool  // Idempotent: same id can re-reserve
AllocateNextSuffix(baseName) → int  // Returns 2, 3, 4... (starts at 2)
GetAllReservedNames() → HashSet<string>
```

## TypeScriptReservedWords.cs

Reserved word handling.

**Reserved Words**: `break`, `case`, `class`, `const`, `switch`, `interface`, `type`, `readonly`, etc. (case-insensitive)

**Methods**:
```csharp
IsReservedWord(name) → bool  // Case-insensitive check

record SanitizeResult(string Sanitized, string Original, bool WasSanitized, string? Reason);
Sanitize(identifier) → SanitizeResult
  // Reserved word → add trailing "_"
  // Example: "switch" → "switch_"

SanitizeParameterName(name) → string  // Shorthand for parameters
EscapeIdentifier(name) → string  // UNUSED: $$name$$ format for Tsonic
```

## Key Algorithms

### 1. Dual-Scope Naming (Class vs View)

**Problem**: C# explicit interface implementations have different names than class surface:
```csharp
class MyClass : IComparable {
    public int CompareTo(object? obj) { ... }  // Class surface
    int IComparable.CompareTo(object? obj) { ... }  // Explicit impl
}
```

**Solution**: Reserve member in TWO scopes:
- **Class scope**: `type:MyClass#instance` → `"compareTo"`
- **View scope**: `view:{TypeId}:{IComparable}#instance` → `"compareTo_IComparable"`

**M5 Fix**: Changed `_decisions` from keying by `StableId` to `(StableId, ScopeKey)` tuple.

### 2. Collision Detection

**Numeric Suffix**:
```
Request: "Compare"
Transform: "compare"
First: "compare" (success)
Second: "compare2" (collision, allocate suffix 2)
Third: "compare3" (allocate suffix 3)
```

**Explicit Interface Impl**:
```
Request: "System.Collections.ICollection.Count"
Extract interface: "ICollection"
Transform: "count"
Collision: try "count_ICollection" (success)
```

### 3. Scope Key Construction

**Namespace**: `ns:{Namespace}:{public|internal}` (global: `ns:(global):internal`)
**Class BASE**: `type:{TypeFullName}` (ReserveMemberName adds `#instance`/`#static`)
**Class SURFACE**: `type:{TypeFullName}#{instance|static}`
**View BASE**: `view:{TypeId}:{IfaceId}` (ReserveMemberName adds suffix)
**View SURFACE**: `view:{TypeId}:{IfaceId}#{instance|static}`

### 4. Name Sanitization Pipeline

**Type Names**:
1. Explicit override check
2. Type style transform
3. Sanitize reserved words
4. Try reserve → collision: numeric suffix
5. Record decision

**Member Names**:
1. Explicit override check
2. Member style transform
3. Sanitize reserved words
4. Try reserve → collision:
   - Explicit interface impl: try `{base}_{Interface}`
   - Standard: numeric suffix
5. Record decision with scope key

## Example Scenarios

### Scenario 1: Type Name Reservation

```csharp
var nsScope = ScopeFactory.Namespace("System", NamespaceArea.Internal);
renamer.ReserveTypeName(typeStableId, "String", nsScope, "NameTransform(PascalCase)", "TypePlanner");
// Scope: "ns:System:internal"
// Flow: Override check → PascalCase ("String") → Sanitize (no change) → Reserve → Success
// Result: Final = "String"

string tsName = renamer.GetFinalTypeName(typeSymbol, NamespaceArea.Internal);
// Returns: "String"
```

### Scenario 2: Class Surface Method

```csharp
var classBase = ScopeFactory.ClassBase(typeSymbol);  // "type:System.Object"
renamer.ReserveMemberName(memberStableId, "ToString", classBase, "NameTransform(CamelCase)", false, "MemberPlanner");
// Effective scope: "type:System.Object#instance"
// Flow: Override check → camelCase ("toString") → Sanitize (no change) → Reserve → Success
// Result: Final = "toString"

var scope = ScopeFactory.ClassSurface(typeSymbol, isStatic: false);
string tsName = renamer.GetFinalMemberName(memberStableId, scope);
// Returns: "toString"
```

### Scenario 3: View Surface with Collision

```csharp
// Assume "compareTo" already reserved in class scope
var viewBase = ScopeFactory.ViewBase(typeSymbol, interfaceStableId);
renamer.ReserveMemberName(memberStableId, "System.IComparable.CompareTo", viewBase, "ExplicitInterfaceImplementation", false, "ViewPlanner");
// Effective scope: "view:mscorlib:System.String:mscorlib:System.IComparable#instance"
// Flow: Override check → camelCase → Sanitize → Explicit impl detected
// Extract interface: "IComparable" → Try "compareTo_IComparable" → Success
// Result: Final = "compareTo_IComparable"

// Class lookup
var classScope = ScopeFactory.ClassSurface(typeSymbol, false);
string className = renamer.GetFinalMemberName(classMemberStableId, classScope);
// Returns: "compareTo"

// View lookup (DIFFERENT name!)
var viewScope = ScopeFactory.ViewSurface(typeSymbol, interfaceStableId, false);
string viewName = renamer.GetFinalMemberName(memberStableId, viewScope);
// Returns: "compareTo_IComparable"
```

### Scenario 4: Static vs Instance Separation

```csharp
var classBase = ScopeFactory.ClassBase(typeSymbol);

// Reserve instance method
renamer.ReserveMemberName(instanceStableId, "Compare", classBase, "NameTransform(CamelCase)", false, "MemberPlanner");
// Effective scope: "type:System.String#instance"
// Result: Final = "compare"

// Reserve static method (SAME base name, different scope)
renamer.ReserveMemberName(staticStableId, "Compare", classBase, "NameTransform(CamelCase)", true, "MemberPlanner");
// Effective scope: "type:System.String#static" (DIFFERENT table)
// Result: Final = "compare"

// Both succeed with same name - TypeScript allows this:
// class String_ {
//     compare(other: String_): int;              // Instance
//     static compare(a: String_, b: String_): int;  // Static
// }
```

## Summary

**Capabilities**:
1. Centralized naming authority - single source of truth
2. Full provenance tracking - every rename recorded
3. Dual-scope support - class vs view different names
4. Static/instance separation - prevents false collisions
5. Deterministic suffix allocation - consistent resolution
6. Style transform flexibility - types vs members
7. Reserved word sanitization - automatic handling
8. Explicit interface impl support - special naming

**Critical Rules**:
- NO MANUAL SCOPE STRINGS - always use `ScopeFactory`
- Reservations use BASE scopes (no `#static`/`#instance`)
- Lookups use SURFACE scopes (must have `#static`/`#instance`)
- View members use `ViewSurface()` - never `ClassSurface()`
- All names reserved before emission - no guessing
