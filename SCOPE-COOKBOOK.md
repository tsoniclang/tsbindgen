# Scope Cookbook — Correct Usage of Renaming Scopes

## Overview

The **SymbolRenamer** uses scopes to prevent naming collisions. Every TypeScript identifier must be reserved in the correct scope with the correct parameters.

**Key principle**: Scopes enforce uniqueness within a specific naming context (namespace, class, view).

## Canonical Scope Formats

All scopes follow these exact formats - **DO NOT deviate**:

| Scope Type | Format | Example |
|------------|--------|---------|
| **Namespace (public)** | `ns:{Namespace}:public` | `ns:System.Collections.Generic:public` |
| **Namespace (internal)** | `ns:{Namespace}:internal` | `ns:System.Collections.Generic:internal` |
| **Class base (reservation)** | `type:{TypeFullName}` | `type:System.Decimal` |
| **Class surface (lookup)** | `type:{TypeFullName}#instance` | `type:System.Decimal#instance` |
| **Class surface (lookup)** | `type:{TypeFullName}#static` | `type:System.Decimal#static` |
| **View base (reservation)** | `view:{TypeStableId}:{InterfaceStableId}` | `view:System.Private.CoreLib:System.Decimal:System.Private.CoreLib:System.IConvertible` |
| **View surface (lookup)** | `view:{TypeStableId}:{InterfaceStableId}#instance` | `view:System.Private.CoreLib:System.Decimal:System.Private.CoreLib:System.IConvertible#instance` |
| **View surface (lookup)** | `view:{TypeStableId}:{InterfaceStableId}#static` | `view:System.Private.CoreLib:System.Decimal:System.Private.CoreLib:System.IConvertible#static` |

## Base vs Surface Scopes

### Base Scopes (for Reservations)

**Purpose**: Used when **reserving** names via `ReserveMemberName()`.

- **Format**: NO `#instance/#static` suffix
- **Examples**:
  - `type:{TypeFullName}` — Class base
  - `view:{TypeStableId}:{InterfaceStableId}` — View base

**Why**: The renamer internally appends `#instance` or `#static` based on the `isStatic` parameter you provide.

### Surface Scopes (for Lookups)

**Purpose**: Used when **looking up** names via `GetFinalMemberName()`, `HasFinalMemberName()`, `HasFinalViewMemberName()`.

- **Format**: MUST have `#instance` or `#static` suffix
- **Examples**:
  - `type:{TypeFullName}#instance` — Class instance surface
  - `type:{TypeFullName}#static` — Class static surface
  - `view:{TypeStableId}:{InterfaceStableId}#instance` — View instance surface
  - `view:{TypeStableId}:{InterfaceStableId}#static` — View static surface

**Why**: Lookups must specify the exact sub-scope to retrieve the correct name.

## Cookbook — Common Operations

### 1. Reserve a Type Name

```csharp
// Use: Reserving a type's name in its namespace
var nsScope = ScopeFactory.Namespace(type.Namespace, NamespaceArea.Internal);
ctx.Renamer.ReserveTypeName(
    type.StableId,
    requestedName,
    nsScope,
    reason: "Type name reservation",
    decisionSource: "NameReservation");
```

### 2. Reserve a Class Surface Member

```csharp
// Use: Reserving a method/property/field on the class surface
var baseScope = ScopeFactory.ClassBase(type);
ctx.Renamer.ReserveMemberName(
    member.StableId,
    requestedName,
    baseScope,
    reason: "Class surface member",
    isStatic: member.IsStatic,
    decisionSource: "NameReservation");
```

**CRITICAL**: Use `ClassBase()` — the renamer will internally add `#instance/#static`.

### 3. Reserve a View Surface Member

```csharp
// Use: Reserving a method/property for an explicit interface view
var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceRef);
var baseScope = ScopeFactory.ViewBase(type, ifaceStableId);
ctx.Renamer.ReserveMemberName(
    member.StableId,
    requestedName,
    baseScope,
    reason: "View surface member",
    isStatic: member.IsStatic,
    decisionSource: "ViewPlanner");
```

**CRITICAL**: Use `ViewBase()` — the renamer will internally add `#instance/#static`.

### 4. Lookup a Type Name

```csharp
// Use: Getting the final TypeScript name for a type
var finalName = ctx.Renamer.GetFinalTypeName(type, NamespaceArea.Internal);
```

**Note**: This helper automatically creates the correct namespace scope internally.

### 5. Lookup a Class Surface Member

```csharp
// Use: Getting the final name for a class member during emission
var classScope = ScopeFactory.ClassSurface(type, member.IsStatic);
var finalName = ctx.Renamer.GetFinalMemberName(member.StableId, classScope);
```

**CRITICAL**: Use `ClassSurface()` — includes the required `#instance/#static` suffix.

### 6. Lookup a View Surface Member

```csharp
// Use: Getting the final name for a view member during emission
var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceRef);
var viewScope = ScopeFactory.ViewSurface(type, ifaceStableId, member.IsStatic);
var finalName = ctx.Renamer.GetFinalMemberName(member.StableId, viewScope);
```

**CRITICAL**: Use `ViewSurface()` — includes the required `#instance/#static` suffix.

### 7. Check if a Name Exists (Query Helpers)

```csharp
// Check if a type has a final name
var nsScope = ScopeFactory.Namespace(type.Namespace, NamespaceArea.Internal);
bool hasName = ctx.Renamer.HasFinalTypeName(type.StableId, nsScope);

// Check if a class member has a final name
var classScope = ScopeFactory.ClassSurface(type, isStatic);
bool hasName = ctx.Renamer.HasFinalMemberName(member.StableId, classScope);

// Check if a view member has a final name
var viewScope = ScopeFactory.ViewSurface(type, ifaceStableId, isStatic);
bool hasName = ctx.Renamer.HasFinalViewMemberName(member.StableId, viewScope);
```

**Purpose**: Used in PhaseGate validation to verify all symbols are finalized.

### 8. Peek at Next Available Name (Collision Detection)

```csharp
// Use: Check what name would be assigned without committing
var baseScope = ScopeFactory.ClassBase(type);
var wouldBe = ctx.Renamer.PeekFinalMemberName(baseScope, requestedName, isStatic);
```

**Purpose**: Used in ViewPlanner to detect collisions before reservation.

## Common Mistakes and Fixes

### ❌ WRONG: Using ClassSurface for Reservation

```csharp
// This will FAIL - ClassSurface includes #instance/#static which reservation doesn't expect
var scope = ScopeFactory.ClassSurface(type, isStatic);
ctx.Renamer.ReserveMemberName(id, name, scope, reason, isStatic);
// ERROR: Renamer will double-append suffix → "type:Foo#instance#instance"
```

### ✅ CORRECT: Using ClassBase for Reservation

```csharp
var scope = ScopeFactory.ClassBase(type);
ctx.Renamer.ReserveMemberName(id, name, scope, reason, isStatic);
// Renamer appends suffix internally → "type:Foo#instance"
```

---

### ❌ WRONG: Using ClassBase for Lookup

```csharp
// This will FAIL - Lookup requires #instance/#static suffix
var scope = ScopeFactory.ClassBase(type);
var final = ctx.Renamer.GetFinalMemberName(id, scope);
// ERROR: "No rename decision found for {id} in scope type:Foo"
```

### ✅ CORRECT: Using ClassSurface for Lookup

```csharp
var scope = ScopeFactory.ClassSurface(type, isStatic);
var final = ctx.Renamer.GetFinalMemberName(id, scope);
// Success: Found decision in "type:Foo#instance"
```

---

### ❌ WRONG: Using ClassSurface for View Members

```csharp
// This will FAIL - View members MUST use ViewSurface
var scope = ScopeFactory.ClassSurface(type, isStatic);
var final = ctx.Renamer.GetFinalMemberName(viewMember.StableId, scope);
// ERROR: ViewOnly member reserved in view scope, not class scope
```

### ✅ CORRECT: Using ViewSurface for View Members

```csharp
var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceRef);
var scope = ScopeFactory.ViewSurface(type, ifaceStableId, isStatic);
var final = ctx.Renamer.GetFinalMemberName(viewMember.StableId, scope);
// Success: Found decision in "view:System.Private.CoreLib:System.Decimal:...#instance"
```

---

### ❌ WRONG: Manual Scope String Construction

```csharp
// NEVER DO THIS - bypasses all validation and type safety
var scope = new TypeScope
{
    TypeFullName = type.ClrFullName,
    IsStatic = false,
    ScopeKey = $"type:{type.ClrFullName}#instance"  // Manual string construction
};
```

### ✅ CORRECT: Using ScopeFactory

```csharp
var scope = ScopeFactory.ClassSurface(type, isStatic);
// Factory ensures correct format and validation
```

## Emitter Rules

### Rule 1: Class Surface Emission

When emitting **class surface** members (methods, properties, fields with `EmitScope.ClassSurface`):

```csharp
foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface))
{
    var scope = ScopeFactory.ClassSurface(type, method.IsStatic);
    var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, scope);
    // Emit using finalName
}
```

### Rule 2: View Surface Emission

When emitting **view surface** members (methods, properties with `EmitScope.ViewOnly`):

```csharp
foreach (var view in type.ExplicitViews)
{
    var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceRef);

    foreach (var member in view.ViewMembers)
    {
        var scope = ScopeFactory.ViewSurface(type, ifaceStableId, member.IsStatic);
        var finalName = ctx.Renamer.GetFinalMemberName(member.StableId, scope);
        // Emit using finalName in view property
    }
}
```

### Rule 3: DEBUG Assertions in Emitters

Add these assertions to catch scope mistakes during development:

```csharp
#if DEBUG
foreach (var view in type.ExplicitViews)
{
    var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceRef);

    foreach (var member in view.ViewMembers)
    {
        // Get class name and view name
        var classScope = ScopeFactory.ClassSurface(type, member.IsStatic);
        var viewScope = ScopeFactory.ViewSurface(type, ifaceStableId, member.IsStatic);

        var className = ctx.Renamer.GetFinalMemberName(member.StableId, classScope);
        var viewName = ctx.Renamer.GetFinalMemberName(member.StableId, viewScope);

        // View name MUST be different (ends with $view suffix)
        Debug.Assert(
            viewName == className || viewName.EndsWith("$view"),
            $"View member {member.StableId} has same name as class surface: {className}");
    }
}
#endif
```

## Troubleshooting

### Lookup Miss? Compare ScopeKeys

If you get `"No rename decision found for {id} in scope {scopeKey}"`:

1. **Check the error message** - it shows the scope you tried to use
2. **Check "Available scopes"** - error message lists all scopes where this StableId was reserved
3. **Compare formats**:
   - Did you use Base instead of Surface for lookup?
   - Did you use ClassSurface instead of ViewSurface for a view member?
   - Did you forget the `#instance/#static` suffix?

**Example error**:

```
No rename decision found for System.Private.CoreLib:System.Decimal:ToByte in scope type:System.Decimal#instance.
Available scopes for this StableId: [
  view:System.Private.CoreLib:System.Decimal:System.Private.CoreLib:System.IConvertible#instance
]
```

**Diagnosis**: Member was reserved in **view** scope but looked up in **class** scope.

**Fix**: Use `ViewSurface()` instead of `ClassSurface()`.

## Summary — Golden Rules

1. ✅ **ALWAYS** use `ScopeFactory` — never construct scope strings manually
2. ✅ **Reserve** with **Base** scopes (`ClassBase`, `ViewBase`)
3. ✅ **Lookup** with **Surface** scopes (`ClassSurface`, `ViewSurface`)
4. ✅ **Class members** → `ClassBase`/`ClassSurface`
5. ✅ **View members** → `ViewBase`/`ViewSurface`
6. ✅ **PhaseGate** uses `HasFinal*` helpers to verify finalization
7. ✅ **Emitters** use `GetFinalMemberName()` with correct scope
8. ✅ **DEBUG** asserts verify class vs view name differences

---

**Questions? Check PhaseGate error messages — they show expected vs actual scopes.**
