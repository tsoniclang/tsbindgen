# Renaming Scope Specification

This document defines the scope format and API contracts for the SymbolRenamer system.

## Overview

Scopes enforce naming uniqueness within specific contexts (namespace, class, view). Every TypeScript identifier must be reserved in the correct scope.

## Scope Format Specification

All scopes follow these exact string formats:

| Scope Type | Format | Example |
|------------|--------|---------|
| **Namespace (public)** | `ns:{Namespace}:public` | `ns:System.Collections.Generic:public` |
| **Namespace (internal)** | `ns:{Namespace}:internal` | `ns:System.Collections.Generic:internal` |
| **Class base** | `type:{TypeFullName}` | `type:System.Decimal` |
| **Class instance** | `type:{TypeFullName}#instance` | `type:System.Decimal#instance` |
| **Class static** | `type:{TypeFullName}#static` | `type:System.Decimal#static` |
| **View base** | `view:{TypeStableId}:{InterfaceStableId}` | `view:System.Private.CoreLib:System.Decimal:System.Private.CoreLib:System.IConvertible` |
| **View instance** | `view:{TypeStableId}:{InterfaceStableId}#instance` | (with full StableIds) |
| **View static** | `view:{TypeStableId}:{InterfaceStableId}#static` | (with full StableIds) |

**Rules**:
- Namespace scopes distinguish public vs internal areas
- Class/view scopes distinguish instance vs static surfaces
- Base scopes have NO suffix (used for reservation)
- Surface scopes have `#instance` or `#static` suffix (used for lookup)

## API Contract

### Reservation API

**Purpose**: Reserve a name in a scope (creates rename decision).

**Method**: `ReserveMemberName(stableId, requestedName, baseScope, reason, isStatic, decisionSource)`

**Scope parameter**: MUST be base scope (no `#instance/#static` suffix).

**Behavior**: Renamer appends `#instance` or `#static` internally based on `isStatic` parameter.

**Valid scopes**:
- `type:{TypeFullName}` (class base)
- `view:{TypeStableId}:{InterfaceStableId}` (view base)

### Lookup API

**Purpose**: Retrieve finalized name from scope.

**Method**: `GetFinalMemberName(stableId, surfaceScope)`

**Scope parameter**: MUST be surface scope (includes `#instance/#static` suffix).

**Behavior**: Returns finalized name or throws if not found.

**Valid scopes**:
- `type:{TypeFullName}#instance` (class instance)
- `type:{TypeFullName}#static` (class static)
- `view:{TypeStableId}:{InterfaceStableId}#instance` (view instance)
- `view:{TypeStableId}:{InterfaceStableId}#static` (view static)

### Query API

**Purpose**: Check if name is finalized in scope.

**Methods**:
- `HasFinalTypeName(stableId, namespaceScope)`
- `HasFinalMemberName(stableId, surfaceScope)`
- `HasFinalViewMemberName(stableId, surfaceScope)`

**Scope parameter**: MUST be appropriate scope type for query.

**Behavior**: Returns `true` if finalized, `false` otherwise.

## ScopeFactory Specification

**Purpose**: Construct valid scope strings with type safety.

### Type Name Scopes

```csharp
// Namespace scope for type reservation/lookup
ScopeFactory.Namespace(string namespace, NamespaceArea area)
// Returns: "ns:{namespace}:public" or "ns:{namespace}:internal"
```

### Member Name Scopes (Reservation)

```csharp
// Class base scope (for ReserveMemberName)
ScopeFactory.ClassBase(TypeSymbol type)
// Returns: "type:{type.ClrFullName}"

// View base scope (for ReserveMemberName)
ScopeFactory.ViewBase(TypeSymbol type, string interfaceStableId)
// Returns: "view:{type.StableId}:{interfaceStableId}"
```

### Member Name Scopes (Lookup)

```csharp
// Class surface scope (for GetFinalMemberName)
ScopeFactory.ClassSurface(TypeSymbol type, bool isStatic)
// Returns: "type:{type.ClrFullName}#instance" or "type:{type.ClrFullName}#static"

// View surface scope (for GetFinalMemberName)
ScopeFactory.ViewSurface(TypeSymbol type, string interfaceStableId, bool isStatic)
// Returns: "view:{type.StableId}:{interfaceStableId}#instance" or "#static"
```

### Helper

```csharp
// Get StableId for interface type reference
ScopeFactory.GetInterfaceStableId(NamedTypeReference interfaceRef)
// Returns: "{assembly}:{fullName}"
```

## Usage Patterns

### Pattern 1: Reserve Type Name

```csharp
var scope = ScopeFactory.Namespace(type.Namespace, NamespaceArea.Internal);
ctx.Renamer.ReserveTypeName(
    type.StableId,
    requestedName,
    scope,
    reason,
    decisionSource);
```

### Pattern 2: Reserve Class Member

```csharp
var scope = ScopeFactory.ClassBase(type);
ctx.Renamer.ReserveMemberName(
    member.StableId,
    requestedName,
    scope,
    reason,
    isStatic: member.IsStatic,
    decisionSource);
```

**Note**: Use `ClassBase()` — renamer appends `#instance/#static` internally.

### Pattern 3: Reserve View Member

```csharp
var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceRef);
var scope = ScopeFactory.ViewBase(type, ifaceStableId);
ctx.Renamer.ReserveMemberName(
    member.StableId,
    requestedName,
    scope,
    reason,
    isStatic: member.IsStatic,
    decisionSource);
```

**Note**: Use `ViewBase()` — renamer appends `#instance/#static` internally.

### Pattern 4: Lookup Type Name

```csharp
var finalName = ctx.Renamer.GetFinalTypeName(type, NamespaceArea.Internal);
```

**Note**: This helper constructs namespace scope internally.

### Pattern 5: Lookup Class Member

```csharp
var scope = ScopeFactory.ClassSurface(type, member.IsStatic);
var finalName = ctx.Renamer.GetFinalMemberName(member.StableId, scope);
```

**Note**: Use `ClassSurface()` — includes `#instance/#static` suffix.

### Pattern 6: Lookup View Member

```csharp
var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceRef);
var scope = ScopeFactory.ViewSurface(type, ifaceStableId, member.IsStatic);
var finalName = ctx.Renamer.GetFinalMemberName(member.StableId, scope);
```

**Note**: Use `ViewSurface()` — includes `#instance/#static` suffix.

## Validation Rules

### Rule 1: Reservation Scope
When calling `ReserveMemberName()`:
- MUST use base scope (no suffix)
- MUST use `ClassBase()` for class members
- MUST use `ViewBase()` for view members
- NEVER manually construct scope strings

### Rule 2: Lookup Scope
When calling `GetFinalMemberName()`:
- MUST use surface scope (with suffix)
- MUST use `ClassSurface()` for class members
- MUST use `ViewSurface()` for view members
- NEVER use base scopes for lookup

### Rule 3: Scope/EmitScope Correspondence
Member lookup scope MUST match `EmitScope`:
- `EmitScope.ClassSurface` → use `ClassSurface()`
- `EmitScope.StaticSurface` → use `ClassSurface()` with `isStatic: true`
- `EmitScope.ViewOnly` → use `ViewSurface()`

### Rule 4: ScopeFactory Required
ALL scope construction MUST use `ScopeFactory` methods. Manual string construction is prohibited.

## Error Conditions

### Lookup Miss
**Error**: `"No rename decision found for {id} in scope {scopeKey}"`

**Causes**:
1. Used base scope instead of surface scope for lookup
2. Used `ClassSurface()` instead of `ViewSurface()` for view member
3. Member not reserved (missing `ReserveMemberName()` call)
4. Wrong `isStatic` value

**Error message includes**: Available scopes where StableId was reserved.

### Scope Mismatch
**Error**: `"Scope kind doesn't match EmitScope"`

**Causes**:
1. Class member looked up with view scope
2. View member looked up with class scope
3. Static member looked up with instance scope
4. Instance member looked up with static scope

## Emission Requirements

### Class Surface Members
```csharp
// For each member where EmitScope == ClassSurface or StaticSurface
var scope = ScopeFactory.ClassSurface(type, member.IsStatic);
var finalName = ctx.Renamer.GetFinalMemberName(member.StableId, scope);
```

### View Surface Members
```csharp
// For each view member where EmitScope == ViewOnly
var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceRef);
var scope = ScopeFactory.ViewSurface(type, ifaceStableId, member.IsStatic);
var finalName = ctx.Renamer.GetFinalMemberName(member.StableId, scope);
```

## PhaseGate Integration

PhaseGate validates scope usage via:
- **PG_FIN_003**: Member has final name in correct scope
- **PG_FIN_004**: Type has final name in namespace scope
- **PG_SCOPE_003**: Scope key is non-empty and well-formed
- **PG_SCOPE_004**: Scope kind matches EmitScope

Validation uses `HasFinal*` query methods to verify finalization.

## Summary

**Core Rules**:
1. Reservation uses base scopes (no suffix)
2. Lookup uses surface scopes (with suffix)
3. Class members use `ClassBase()`/`ClassSurface()`
4. View members use `ViewBase()`/`ViewSurface()`
5. ALL scope construction via `ScopeFactory`
6. Scope must match `EmitScope` during emission
