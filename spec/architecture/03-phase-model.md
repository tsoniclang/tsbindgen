# Phase 3: Model - Data Structures

## Overview

The **Model** phase defines the immutable data structures that represent the complete symbol graph for all loaded assemblies. These data structures are created during the **Load** phase and transformed during the **Shape** phase.

The Model represents the bridge between raw CLR reflection data and TypeScript emission. It provides:

1. **Type-safe representation** of all CLR types and members
2. **Stable identities** for tracking types/members through transformations
3. **Type references** that capture the full type system (generics, arrays, pointers, etc.)
4. **Provenance tracking** to understand where members came from
5. **Emit scope control** to determine what gets emitted where

All Model structures are **immutable records** that use structural equality and support pure functional transformations via `with` expressions.

---

## File: SymbolGraph.cs

### Purpose

**SymbolGraph** is the root container for the entire symbol graph. It contains all namespaces, types, and members loaded from .NET assemblies, plus fast lookup indices.

### Record: SymbolGraph

```csharp
public sealed record SymbolGraph
```

**Properties:**

- **`Namespaces: ImmutableArray<NamespaceSymbol>`**
  - All namespaces with their types
  - This is the primary hierarchical structure: Graph → Namespaces → Types → Members
  - Each namespace contains all types from potentially multiple contributing assemblies

- **`SourceAssemblies: ImmutableHashSet<string>`**
  - Set of source assembly paths that contributed to this graph
  - Used for tracking and diagnostics
  - Example: `["/path/to/System.Private.CoreLib.dll", "/path/to/System.Runtime.dll"]`

- **`NamespaceIndex: ImmutableDictionary<string, NamespaceSymbol>`**
  - Quick lookup: namespace name → namespace symbol
  - Built once during construction via `WithIndices()`
  - Enables O(1) namespace lookups by name
  - Example key: `"System.Collections.Generic"`

- **`TypeIndex: ImmutableDictionary<string, TypeSymbol>`**
  - Quick lookup: CLR full name → type symbol
  - Built once during construction via `WithIndices()`
  - Enables O(1) type lookups by CLR full name
  - Includes nested types recursively
  - Example key: `"System.Collections.Generic.List`1"`

### Methods

**`WithIndices(): SymbolGraph`**
- Pure function that returns a new graph with populated indices
- Iterates all namespaces and types (including nested) to build lookup dictionaries
- **MUST be called after creating a new graph** to enable efficient lookups
- Example usage: `var indexedGraph = rawGraph.WithIndices();`

**`TryGetNamespace(string name, out NamespaceSymbol? ns): bool`**
- Try to find a namespace by name using the index
- Returns true if found, false otherwise
- Safe lookup that doesn't throw

**`TryGetType(string clrFullName, out TypeSymbol? type): bool`**
- Try to find a type by CLR full name using the index
- Returns true if found, false otherwise
- Works for nested types as well

**`WithUpdatedType(string keyOrStableId, Func<TypeSymbol, TypeSymbol> transform): SymbolGraph`**
- Pure function that updates a single type in the graph
- **Key parameter** can be either:
  - CLR full name: `"System.Collections.Generic.List`1"`
  - StableId: `"System.Private.CoreLib:System.Collections.Generic.List`1"`
- Finds the type, applies the transform function, and returns a new graph
- **Automatically rebuilds indices** after the update
- Handles nested types recursively
- Used extensively in Shape phase transformations
- Example:
  ```csharp
  var updated = graph.WithUpdatedType("System.String", type =>
      type.WithAddedMethods(new[] { syntheticMethod }));
  ```

**`GetStatistics(): SymbolGraphStatistics`**
- Calculates statistics about the graph (namespace count, type count, member counts)
- Recursively counts nested types and their members
- Used for diagnostics and progress reporting

### Record: SymbolGraphStatistics

```csharp
public sealed record SymbolGraphStatistics
```

**Properties:**
- `NamespaceCount: int` - Total number of namespaces
- `TypeCount: int` - Total number of types (including nested)
- `MethodCount: int` - Total number of methods
- `PropertyCount: int` - Total number of properties
- `FieldCount: int` - Total number of fields
- `EventCount: int` - Total number of events
- `TotalMembers: int` - Computed property: sum of all member counts

---

## File: AssemblyKey.cs

### Purpose

**AssemblyKey** provides normalized assembly identity for disambiguation. Ensures consistent assembly identity across different contexts (MetadataLoadContext vs runtime, etc.).

### Record Struct: AssemblyKey

```csharp
public readonly record struct AssemblyKey(
    string Name,
    string PublicKeyToken,
    string Culture,
    string Version)
```

**Properties:**

- **`Name: string`**
  - Assembly simple name (e.g., `"System.Private.CoreLib"`)

- **`PublicKeyToken: string`**
  - Hex string of public key token, or `"null"` if not signed
  - Example: `"7cec85d7bea7798e"`

- **`Culture: string`**
  - Culture name, or `"neutral"` for culture-neutral assemblies
  - Example: `"neutral"`, `"en-US"`

- **`Version: string`**
  - Version string in format `"Major.Minor.Build.Revision"`
  - Example: `"10.0.0.0"`

**Properties (Computed):**

- **`SimpleName: string`**
  - Returns just the `Name` part without version/culture/token
  - Used for display purposes

### Methods

**`static From(AssemblyName asm): AssemblyKey`**
- Creates normalized AssemblyKey from System.Reflection.AssemblyName
- Handles null/missing values with proper defaults:
  - Missing name → empty string
  - Missing token → `"null"`
  - Missing culture → `"neutral"`
  - Missing version → `"0.0.0.0"`

**`ToString(): string`**
- Returns full identity string in GAC format
- Example: `"System.Private.CoreLib, PublicKeyToken=7cec85d7bea7798e, Culture=neutral, Version=10.0.0.0"`

### Extension Methods

**`ToHexString(this byte[] bytes): string`**
- Converts PublicKeyToken byte array to lowercase hex string
- Returns `"null"` for null/empty arrays
- Format: `"7cec85d7bea7798e"` (no dashes)

---

## File: Symbols/NamespaceSymbol.cs

### Purpose

**NamespaceSymbol** represents a namespace containing types. Multiple assemblies can contribute types to the same namespace (e.g., both `System.Private.CoreLib` and `System.Runtime` contribute to `System`).

### Record: NamespaceSymbol

```csharp
public sealed record NamespaceSymbol
```

**Properties:**

- **`Name: string`**
  - Namespace name (e.g., `"System.Collections.Generic"`)
  - Empty string for the root/global namespace

- **`Types: ImmutableArray<TypeSymbol>`**
  - All types declared in this namespace
  - Does NOT include types from nested namespaces
  - Example: `List<T>`, `Dictionary<TKey, TValue>` for `System.Collections.Generic`

- **`StableId: StableId`**
  - Stable identifier for this namespace
  - Used for tracking through transformations

- **`ContributingAssemblies: ImmutableHashSet<string>`**
  - Set of assembly names that contribute types to this namespace
  - Multiple assemblies can contribute to the same namespace
  - Example: `["System.Private.CoreLib", "System.Runtime"]` for `System` namespace

**Properties (Computed):**

- **`IsRoot: bool`**
  - True if this is the root/global namespace (empty name)
  - Root namespace types are emitted at module level in TypeScript (no namespace wrapper)

- **`SafeNameOrNull: string?`**
  - Returns namespace name or null if this is the root namespace
  - Used by emitters to avoid printing empty namespace tokens

---

## File: Symbols/TypeSymbol.cs

### Purpose

**TypeSymbol** represents a type (class, struct, interface, enum, delegate). This is the core of the symbol graph, containing all type metadata, members, and relationships.

### Record: TypeSymbol

```csharp
public sealed record TypeSymbol
```

**Identity Properties:**

- **`StableId: TypeStableId`**
  - Stable identifier for this type BEFORE any name transformations
  - Format: `"AssemblyName:ClrFullName"`
  - Example: `"System.Private.CoreLib:System.Collections.Generic.List`1"`
  - Used as the key for rename decisions and bindings back to CLR

- **`ClrFullName: string`**
  - Full CLR type name including namespace
  - Example: `"System.Collections.Generic.List`1"`
  - Uses backtick notation for generic arity

- **`ClrName: string`**
  - Simple CLR name without namespace
  - Example: `"List`1"` (for `System.Collections.Generic.List`1`)

- **`TsEmitName: string`**
  - TypeScript emit name set by NameApplication after reservation
  - Initially empty, populated during Shape phase
  - Example: `"List_1"` (generic arity uses underscore)
  - For nested types: `"Console$Error"` (dollar separator)

**Classification Properties:**

- **`Namespace: string`**
  - Namespace containing this type
  - Example: `"System.Collections.Generic"`

- **`Kind: TypeKind`**
  - Kind of type: `Class`, `Struct`, `Interface`, `Enum`, `Delegate`, `StaticNamespace`
  - `StaticNamespace` is for C# static classes

- **`Accessibility: Accessibility`**
  - Accessibility level: `Public`, `Protected`, `Internal`, `ProtectedInternal`, `Private`, `PrivateProtected`
  - Default: `Public`

**Generic Properties:**

- **`Arity: int`**
  - Generic arity (0 for non-generic types)
  - Example: `2` for `Dictionary<TKey, TValue>`

- **`GenericParameters: ImmutableArray<GenericParameterSymbol>`**
  - Generic parameters declared by this type
  - Example: `[T]` for `List<T>`, `[TKey, TValue]` for `Dictionary<TKey, TValue>`
  - Empty for non-generic types

**Type Hierarchy Properties:**

- **`BaseType: TypeReference?`**
  - Base type reference
  - Null for interfaces, `System.Object`, and `System.ValueType`
  - Example: For `List<T>`, this would reference `Object`

- **`Interfaces: ImmutableArray<TypeReference>`**
  - All directly implemented interfaces
  - Example: For `List<T>`: `[IList<T>, ICollection<T>, IEnumerable<T>, ...]`
  - Shape phase may add additional interfaces via interface closure

**Member Properties:**

- **`Members: TypeMembers`**
  - Container for all members (methods, properties, fields, events, constructors)
  - See `TypeMembers` record below

- **`NestedTypes: ImmutableArray<TypeSymbol>`**
  - Nested types declared within this type
  - Recursively contains their own nested types
  - Example: `Console` has nested type `Error`

**Type Characteristics:**

- **`IsValueType: bool`**
  - True if this is a value type (struct, enum)
  - Affects TypeScript emit (interfaces vs classes)

- **`IsAbstract: bool`**
  - True if this type is abstract
  - Affects instantiability checks

- **`IsSealed: bool`**
  - True if this type is sealed (cannot be inherited)

- **`IsStatic: bool`**
  - True if this is a C# static class
  - Maps to `TypeKind.StaticNamespace`

- **`DeclaringType: TypeSymbol?`**
  - Declaring type for nested types
  - Null for top-level types

**Documentation:**

- **`Documentation: string?`**
  - XML documentation comment (if available)
  - Null if no documentation

**Shape Phase Properties:**

- **`ExplicitViews: ImmutableArray<Shape.ViewPlanner.ExplicitView>`**
  - Explicit interface views planned for this type
  - Populated by `ViewPlanner` in Shape phase
  - Empty for interfaces and static classes
  - Each view represents a `As_IInterface` property that exposes interface-specific members

### Wither Methods

TypeSymbol provides several wither helpers for pure functional transformations:

**`WithMembers(TypeMembers members): TypeSymbol`**
- Replace all members
- Example: `type.WithMembers(newMembers)`

**`WithAddedMethods(IEnumerable<MethodSymbol> methods): TypeSymbol`**
- Add methods while preserving other members
- Example: `type.WithAddedMethods(syntheticMethods)`

**`WithRemovedMethods(Func<MethodSymbol, bool> predicate): TypeSymbol`**
- Remove methods matching predicate
- Example: `type.WithRemovedMethods(m => m.IsStatic && m.Arity > 0)`

**`WithAddedProperties(IEnumerable<PropertySymbol> properties): TypeSymbol`**
- Add properties while preserving other members

**`WithRemovedProperties(Func<PropertySymbol, bool> predicate): TypeSymbol`**
- Remove properties matching predicate

**`WithAddedFields(IEnumerable<FieldSymbol> fields): TypeSymbol`**
- Add fields while preserving other members

**`WithTsEmitName(string tsEmitName): TypeSymbol`**
- Set the TypeScript emit name
- Used by `NameApplication` during Shape phase

**`WithExplicitViews(ImmutableArray<Shape.ViewPlanner.ExplicitView> views): TypeSymbol`**
- Set explicit interface views
- Used by `ViewPlanner` during Shape phase

### Enum: TypeKind

```csharp
public enum TypeKind
{
    Class,           // Reference type class
    Struct,          // Value type struct
    Interface,       // Interface
    Enum,            // Enumeration
    Delegate,        // Delegate type
    StaticNamespace  // C# static class (emits as namespace in TS)
}
```

### Record: GenericParameterSymbol

```csharp
public sealed record GenericParameterSymbol
```

**Properties:**

- **`Id: GenericParameterId`**
  - Unique identifier for this parameter
  - Combines declaring type name and position
  - Used for substitution when implementing closed generic interfaces

- **`Name: string`**
  - Parameter name
  - Example: `"T"`, `"TKey"`, `"TValue"`

- **`Position: int`**
  - Zero-based position in the parameter list
  - Example: In `Dictionary<TKey, TValue>`, `TKey` has position 0, `TValue` has position 1

- **`Constraints: ImmutableArray<TypeReference>`**
  - Type constraints on this parameter (resolved by `ConstraintCloser`)
  - Example: `[IComparable, IEnumerable<T>]`
  - Initially empty, populated during Shape phase

- **`RawConstraintTypes: System.Type[]?`**
  - Raw CLR constraint types from reflection
  - Populated during Load phase
  - Resolved to `Constraints` by `ConstraintCloser` in Shape phase
  - Null after resolution

- **`Variance: Variance`**
  - Variance: `None`, `Covariant` (out T), `Contravariant` (in T)

- **`SpecialConstraints: GenericParameterConstraints`**
  - Flags for special constraints: `struct`, `class`, `new()`, `notnull`

### Enum: Variance

```csharp
public enum Variance
{
    None,          // Invariant
    Covariant,     // out T
    Contravariant  // in T
}
```

### Enum: GenericParameterConstraints

```csharp
[Flags]
public enum GenericParameterConstraints
{
    None = 0,
    ReferenceType = 1,      // class constraint
    ValueType = 2,          // struct constraint
    DefaultConstructor = 4, // new() constraint
    NotNullable = 8         // notnull constraint
}
```

### Record: TypeMembers

```csharp
public sealed record TypeMembers
```

Container for all members of a type. Provides a clean separation of member categories.

**Properties:**

- **`Methods: ImmutableArray<MethodSymbol>`**
  - All methods (including interface-copied and synthetic methods)

- **`Properties: ImmutableArray<PropertySymbol>`**
  - All properties (including indexers marked as properties)

- **`Fields: ImmutableArray<FieldSymbol>`**
  - All fields (instance and static, including constants)

- **`Events: ImmutableArray<EventSymbol>`**
  - All events

- **`Constructors: ImmutableArray<ConstructorSymbol>`**
  - All constructors (instance and static)

**Static Members:**

- **`Empty: TypeMembers`**
  - Pre-constructed empty instance for convenience

### Enum: Accessibility

```csharp
public enum Accessibility
{
    Public,
    Protected,
    Internal,
    ProtectedInternal,
    Private,
    PrivateProtected
}
```

---

## File: Symbols/MemberSymbols/MethodSymbol.cs

### Purpose

**MethodSymbol** represents a method member with full signature information, generic parameters, and metadata for code generation.

### Record: MethodSymbol

```csharp
public sealed record MethodSymbol
```

**Identity Properties:**

- **`StableId: MemberStableId`**
  - Stable identifier for this method
  - Includes declaring type, member name, and canonical signature
  - Example: `"System.Private.CoreLib:System.String::Substring(System.Int32,System.Int32)->System.String"`

- **`ClrName: string`**
  - CLR method name
  - Example: `"Substring"`, `"ToString"`

- **`TsEmitName: string`**
  - TypeScript emit name set by `NameApplication` after reservation
  - Initially empty, populated during Shape phase
  - Example: `"Substring"`, `"get_Item"` (for indexer getters)

**Signature Properties:**

- **`ReturnType: TypeReference`**
  - Method return type
  - `System.Void` for void methods

- **`Parameters: ImmutableArray<ParameterSymbol>`**
  - Method parameters (see `ParameterSymbol` below)
  - Empty for parameterless methods

- **`GenericParameters: ImmutableArray<GenericParameterSymbol>`**
  - Generic parameters declared by this method (for generic methods)
  - Example: `Select<TSource, TResult>` has two generic parameters
  - Empty for non-generic methods

- **`Arity: int`**
  - Computed property: generic parameter count
  - `GenericParameters.Length`

**Modifiers:**

- **`IsStatic: bool`**
  - True if this is a static method

- **`IsAbstract: bool`**
  - True if this is abstract

- **`IsVirtual: bool`**
  - True if this is virtual

- **`IsOverride: bool`**
  - True if this overrides a base method

- **`IsSealed: bool`**
  - True if this is sealed (prevents further overrides)

- **`IsNew: bool`**
  - True if this hides a base member with 'new' keyword

**Visibility:**

- **`Visibility: Visibility`**
  - Visibility: `Public`, `Protected`, `Internal`, `ProtectedInternal`, `PrivateProtected`, `Private`

**Provenance & Scope:**

- **`Provenance: MemberProvenance`**
  - Origin of this method (see `MemberProvenance` enum below)
  - Examples: `Original`, `FromInterface`, `Synthesized`, `ExplicitView`

- **`EmitScope: EmitScope`**
  - Where this member should be emitted (see `EmitScope` enum below)
  - **MUST be explicitly set during Shape phase** (defaults to `Unspecified`)
  - PhaseGate will error (PG_FIN_001) if any member reaches emission with `Unspecified`

**Interface Tracking:**

- **`SourceInterface: TypeReference?`**
  - For interface-sourced members, the interface that contributed this member
  - Null for original members
  - Used to track provenance and for view planning

**Documentation:**

- **`Documentation: string?`**
  - XML documentation comment

### Methods

**`WithSourceInterface(TypeReference? sourceInterface): MethodSymbol`**
- Wither method to set/update the source interface
- Used during interface member copying

### Record: ParameterSymbol

```csharp
public sealed record ParameterSymbol
```

**Properties:**

- **`Name: string`**
  - Parameter name
  - Example: `"index"`, `"startIndex"`, `"value"`

- **`Type: TypeReference`**
  - Parameter type

- **`IsRef: bool`**
  - True if this is a `ref` parameter

- **`IsOut: bool`**
  - True if this is an `out` parameter

- **`IsParams: bool`**
  - True if this is a `params` array parameter

- **`HasDefaultValue: bool`**
  - True if this parameter has a default value

- **`DefaultValue: object?`**
  - Default value (if `HasDefaultValue` is true)
  - Null if no default value

### Enum: Visibility

```csharp
public enum Visibility
{
    Public,
    Protected,
    Internal,
    ProtectedInternal,
    PrivateProtected,
    Private
}
```

### Enum: MemberProvenance

Tracks where a member came from to understand its purpose and how it should be emitted.

```csharp
public enum MemberProvenance
{
    /// Original member declared in this type
    Original,

    /// Copied from an implemented interface
    FromInterface,

    /// Synthesized by a shaper (e.g., explicit interface implementation)
    Synthesized,

    /// Added to resolve C# 'new' hiding
    HiddenNew,

    /// Added to include base class overload
    BaseOverload,

    /// Added to resolve diamond inheritance
    DiamondResolved,

    /// Normalized from indexer syntax
    IndexerNormalized,

    /// Synthesized to satisfy explicit interface view
    ExplicitView,

    /// Marked as ViewOnly due to overload return type conflict
    OverloadReturnConflict
}
```

### Enum: EmitScope

Controls where a member gets emitted in the final TypeScript output.

```csharp
public enum EmitScope
{
    /// Unspecified - placement has not been decided yet
    /// This is the default state and MUST be explicitly set to a real scope
    /// PhaseGate will error (PG_FIN_001) if any member reaches emission with this value
    Unspecified = 0,

    /// Emit on the main class/interface surface
    ClassSurface,

    /// Emit on the static surface (for static classes)
    StaticSurface,

    /// Only emit in explicit interface views (As_IInterface properties)
    ViewOnly,

    /// Omitted from emission (unified away by OverloadUnifier)
    Omitted
}
```

**EmitScope Decision Flow:**

1. All members start as `Unspecified`
2. Shape phase shapers set explicit scopes:
   - `ClassSurface` - Normal members
   - `ViewOnly` - Members that conflict on class surface but are needed for interface views
   - `Omitted` - Members unified away or intentionally skipped
   - `StaticSurface` - For static class members
3. PhaseGate validates that no `Unspecified` members reach emission

---

## File: Symbols/MemberSymbols/PropertySymbol.cs

### Purpose

**PropertySymbol** represents a property member, including indexers (properties with index parameters).

### Record: PropertySymbol

```csharp
public sealed record PropertySymbol
```

**Identity Properties:**

- **`StableId: MemberStableId`**
  - Stable identifier for this property

- **`ClrName: string`**
  - CLR property name
  - Example: `"Length"`, `"Item"` (for indexers)

- **`TsEmitName: string`**
  - TypeScript emit name set by `NameApplication`
  - Initially empty

**Type Properties:**

- **`PropertyType: TypeReference`**
  - Property type (return type)

- **`IndexParameters: ImmutableArray<ParameterSymbol>`**
  - Index parameters for indexers
  - Empty for normal properties
  - Example: For `string this[int index]`, contains one `int` parameter

- **`IsIndexer: bool`**
  - Computed property: true if `IndexParameters.Length > 0`

**Accessor Properties:**

- **`HasGetter: bool`**
  - True if this property has a getter

- **`HasSetter: bool`**
  - True if this property has a setter

**Modifiers:**

- **`IsStatic: bool`**
  - True if this is a static property

- **`IsVirtual: bool`**
  - True if this is virtual

- **`IsOverride: bool`**
  - True if this overrides a base property

- **`IsAbstract: bool`**
  - True if this is abstract

**Visibility, Provenance, Scope:**

- **`Visibility: Visibility`**
- **`Provenance: MemberProvenance`**
- **`EmitScope: EmitScope`** (MUST be set during Shape phase)

**Interface Tracking:**

- **`SourceInterface: TypeReference?`**
  - For interface-sourced properties

**Documentation:**

- **`Documentation: string?`**

### Methods

**`WithSourceInterface(TypeReference? sourceInterface): PropertySymbol`**
- Wither method to set/update the source interface

---

## File: Symbols/MemberSymbols/FieldSymbol.cs

### Purpose

**FieldSymbol** represents a field member, including constants.

### Record: FieldSymbol

```csharp
public sealed record FieldSymbol
```

**Identity Properties:**

- **`StableId: MemberStableId`**
- **`ClrName: string`**
- **`TsEmitName: string`**

**Type Properties:**

- **`FieldType: TypeReference`**
  - Field type

**Modifiers:**

- **`IsStatic: bool`**
  - True if this is a static field

- **`IsReadOnly: bool`**
  - True if this is readonly

- **`IsConst: bool`**
  - True if this is a constant (const)

- **`ConstValue: object?`**
  - Constant value (if `IsConst` is true)

**Visibility, Provenance, Scope:**

- **`Visibility: Visibility`**
- **`Provenance: MemberProvenance`**
- **`EmitScope: EmitScope`** (MUST be set during Shape phase)

**Documentation:**

- **`Documentation: string?`**

---

## File: Symbols/MemberSymbols/EventSymbol.cs

### Purpose

**EventSymbol** represents an event member.

### Record: EventSymbol

```csharp
public sealed record EventSymbol
```

**Identity Properties:**

- **`StableId: MemberStableId`**
- **`ClrName: string`**
- **`TsEmitName: string`**

**Type Properties:**

- **`EventHandlerType: TypeReference`**
  - Event handler type (delegate type)

**Modifiers:**

- **`IsStatic: bool`**
  - True if this is a static event

- **`IsVirtual: bool`**
  - True if this is virtual

- **`IsOverride: bool`**
  - True if this overrides a base event

**Visibility, Provenance, Scope:**

- **`Visibility: Visibility`**
- **`Provenance: MemberProvenance`**
- **`EmitScope: EmitScope`** (MUST be set during Shape phase)

**Interface Tracking:**

- **`SourceInterface: TypeReference?`**
  - For interface-sourced events

**Documentation:**

- **`Documentation: string?`**

### Methods

**`WithSourceInterface(TypeReference? sourceInterface): EventSymbol`**
- Wither method to set/update the source interface

---

## File: Symbols/MemberSymbols/ConstructorSymbol.cs

### Purpose

**ConstructorSymbol** represents a constructor (instance or static/type initializer).

### Record: ConstructorSymbol

```csharp
public sealed record ConstructorSymbol
```

**Properties:**

- **`StableId: MemberStableId`**
  - Stable identifier for this constructor

- **`Parameters: ImmutableArray<ParameterSymbol>`**
  - Constructor parameters

- **`IsStatic: bool`**
  - True if this is a static constructor (type initializer)

- **`Visibility: Visibility`**
  - Visibility level

- **`Documentation: string?`**
  - XML documentation

**Note:** Constructors do NOT have `Provenance` or `EmitScope` because they are always emitted as-is (not subject to interface copying or view planning).

---

## File: Types/TypeReference.cs

### Purpose

**TypeReference** represents a reference to a type in the CLR type system. It's an immutable, structurally equal representation converted from `System.Type` during reflection.

Type references are recursive structures that can represent:
- Simple named types (`List<T>`)
- Generic type parameters (`T`)
- Constructed generics (`List<int>`)
- Arrays (`int[]`)
- Pointers (`int*`)
- ByRef (`ref int`)
- Nested types (`Outer.Inner`)

### Abstract Record: TypeReference

```csharp
public abstract record TypeReference
{
    public abstract TypeReferenceKind Kind { get; }
}
```

All type references derive from this base and have a `Kind` property.

### Enum: TypeReferenceKind

```csharp
public enum TypeReferenceKind
{
    Named,            // Class, struct, interface, enum, delegate
    GenericParameter, // T, TKey, TValue, etc.
    Array,            // T[], T[,], etc.
    Pointer,          // T*, T**, etc.
    ByRef,            // ref T, out T
    Nested,           // Outer.Inner
    Placeholder       // Internal - breaks recursion cycles
}
```

---

### Record: NamedTypeReference

Reference to a named type (class, struct, interface, enum, delegate).

```csharp
public sealed record NamedTypeReference : TypeReference
```

**Properties:**

- **`Kind: TypeReferenceKind`** → `Named`

- **`AssemblyName: string`**
  - Assembly name where the type is defined
  - Example: `"System.Private.CoreLib"`

- **`FullName: string`**
  - Full CLR type name including namespace
  - Example: `"System.Collections.Generic.List`1"`

- **`Namespace: string`**
  - Namespace
  - Example: `"System.Collections.Generic"`

- **`Name: string`**
  - Simple type name without namespace
  - Example: `"List`1"`

- **`Arity: int`**
  - Generic arity (0 for non-generic types)
  - Example: `1` for `List<T>`, `2` for `Dictionary<TKey, TValue>`

- **`TypeArguments: IReadOnlyList<TypeReference>`**
  - Type arguments for constructed generic types
  - Empty for non-generic or open generic types
  - Example: For `List<int>`, contains a `NamedTypeReference` to `System.Int32`

- **`IsValueType: bool`**
  - True if this is a value type (struct, enum)

- **`InterfaceStableId: string?`**
  - Pre-computed StableId for interface types (format: `"AssemblyName:FullName"`)
  - Set at load time for interfaces to eliminate repeated computation
  - Null for non-interface types
  - Used for fast interface lookups in GlobalInterfaceIndex

---

### Record: GenericParameterReference

Reference to a generic type parameter.

```csharp
public sealed record GenericParameterReference : TypeReference
```

**Properties:**

- **`Kind: TypeReferenceKind`** → `GenericParameter`

- **`Id: GenericParameterId`**
  - Identifier for this generic parameter (includes declaring type and position)

- **`Name: string`**
  - Parameter name
  - Example: `"T"`, `"TKey"`

- **`Position: int`**
  - Position in the declaring type's generic parameter list

- **`Constraints: IReadOnlyList<TypeReference>`**
  - Constraints on this parameter

---

### Record: ArrayTypeReference

Reference to an array type.

```csharp
public sealed record ArrayTypeReference : TypeReference
```

**Properties:**

- **`Kind: TypeReferenceKind`** → `Array`

- **`ElementType: TypeReference`**
  - Element type (recursive)

- **`Rank: int`**
  - Array rank (1 for `T[]`, 2 for `T[,]`, etc.)

---

### Record: PointerTypeReference

Reference to a pointer type.

```csharp
public sealed record PointerTypeReference : TypeReference
```

**Properties:**

- **`Kind: TypeReferenceKind`** → `Pointer`

- **`PointeeType: TypeReference`**
  - Type being pointed to (recursive)

- **`Depth: int`**
  - Pointer depth (1 for `T*`, 2 for `T**`, etc.)

---

### Record: ByRefTypeReference

Reference to a ByRef type (ref/out parameter).

```csharp
public sealed record ByRefTypeReference : TypeReference
```

**Properties:**

- **`Kind: TypeReferenceKind`** → `ByRef`

- **`ReferencedType: TypeReference`**
  - Type being referenced (recursive)

---

### Record: NestedTypeReference

Reference to a nested type.

```csharp
public sealed record NestedTypeReference : TypeReference
```

**Properties:**

- **`Kind: TypeReferenceKind`** → `Nested`

- **`DeclaringType: TypeReference`**
  - Declaring (outer) type (recursive)

- **`NestedName: string`**
  - Name of the nested type

- **`FullReference: NamedTypeReference`**
  - Full reference including all nesting levels
  - Used for lookups and emission

---

### Record: PlaceholderTypeReference

Internal placeholder used to break recursion cycles during type graph construction.

```csharp
public sealed record PlaceholderTypeReference : TypeReference
```

**Properties:**

- **`Kind: TypeReferenceKind`** → `Placeholder`

- **`DebugName: string`**
  - Debug name for the type that would have caused infinite recursion

**Note:** This should **never appear in final emitted output**. If it does, printers emit `any` with a diagnostic warning.

---

## File: Types/GenericParameterId.cs

### Purpose

**GenericParameterId** uniquely identifies a generic parameter by combining the declaring type name and parameter position. Used for substitution when implementing closed generic interfaces.

### Record: GenericParameterId

```csharp
public sealed record GenericParameterId
```

**Properties:**

- **`DeclaringTypeName: string`**
  - Full name of the type that declares this generic parameter
  - For method-level generics, includes the method signature
  - Example: `"System.Collections.Generic.List`1"`, `"System.Linq.Enumerable.Select`2"`

- **`Position: int`**
  - Zero-based position in the generic parameter list
  - Example: In `Dictionary<TKey, TValue>`, `TKey` has position 0, `TValue` has position 1

- **`IsMethodParameter: bool`**
  - True if this is a method-level generic parameter (rare in BCL)
  - Default: false

**Methods:**

**`ToString(): string`**
- Format: `"DeclaringTypeName#Position"` (with `"M"` suffix if method parameter)
- Example: `"System.Collections.Generic.List`1#0"`, `"System.Linq.Enumerable.Select`2#1M"`

---

## File: Renaming/StableId.cs

### Purpose

**StableId** provides immutable identity for types and members BEFORE any name transformations. Used as the key for rename decisions and for bindings back to CLR.

### Abstract Record: StableId

```csharp
public abstract record StableId
{
    public required string AssemblyName { get; init; }
}
```

All stable IDs include the assembly name for disambiguation.

---

### Record: TypeStableId

Stable identity for a type.

```csharp
public sealed record TypeStableId : StableId
```

**Properties:**

- **`AssemblyName: string`** (inherited)
  - Assembly where the type originates

- **`ClrFullName: string`**
  - Full CLR type name
  - Example: `"System.Collections.Generic.List`1"`

**Methods:**

**`ToString(): string`**
- Format: `"AssemblyName:ClrFullName"`
- Example: `"System.Private.CoreLib:System.Collections.Generic.List`1"`

---

### Record: MemberStableId

Stable identity for a member (method, property, field, event).

**Equality is based on semantic identity (excluding MetadataToken).**

```csharp
public sealed record MemberStableId : StableId
```

**Properties:**

- **`AssemblyName: string`** (inherited)
  - Assembly where the member originates

- **`DeclaringClrFullName: string`**
  - Full CLR name of the declaring type

- **`MemberName: string`**
  - Member name as it appears in CLR metadata
  - Example: `"Substring"`, `"get_Item"`

- **`CanonicalSignature: string`**
  - Canonical signature that uniquely identifies this member among overloads
  - For methods: includes parameter types and return type
  - For properties: includes parameter types (for indexers)
  - For fields/events: typically empty or just the type
  - Example: `"(System.Int32,System.Int32)->System.String"`

- **`MetadataToken: int?`**
  - Optional metadata token for exact CLR correlation
  - **NOT included in equality comparison** (semantic identity only)
  - Used for debugging and diagnostics

**Methods:**

**`ToString(): string`**
- Format: `"AssemblyName:DeclaringClrFullName::MemberName[CanonicalSignature]"`
- Example: `"System.Private.CoreLib:System.String::Substring(System.Int32,System.Int32)->System.String"`

**`Equals(MemberStableId? other): bool`** (overridden)
- Compares `AssemblyName`, `DeclaringClrFullName`, `MemberName`, `CanonicalSignature`
- **Intentionally excludes `MetadataToken`** from comparison (semantic equality)

**`GetHashCode(): int`** (overridden)
- Hash combines `AssemblyName`, `DeclaringClrFullName`, `MemberName`, `CanonicalSignature`
- **Intentionally excludes `MetadataToken`** from hash

---

## Key Concepts

### StableId Format and Semantics

**StableIds** provide stable, transformation-independent identity:

1. **TypeStableId**: `"AssemblyName:ClrFullName"`
   - Example: `"System.Private.CoreLib:System.String"`
   - Used to track types through all transformations

2. **MemberStableId**: `"AssemblyName:DeclaringType::MemberName[Signature]"`
   - Example: `"System.Private.CoreLib:System.String::Substring(System.Int32,System.Int32)->System.String"`
   - Used to track members through transformations
   - **Semantic equality** (excludes MetadataToken)

**Key Properties:**
- Immutable - set once during Load phase
- Stable across transformations
- Used as keys in rename dictionaries
- Used for bindings back to CLR

---

### Canonical Signatures

**Canonical signatures** uniquely identify members among overloads:

**For Methods:**
- Format: `"(ParamType1,ParamType2,...)->ReturnType"`
- Example: `"(System.Int32,System.Int32)->System.String"` for `string Substring(int, int)`
- Includes all parameter types and return type

**For Properties:**
- Format: `"(IndexParam1,IndexParam2,...)->PropertyType"`
- Example: `"(System.Int32)->System.Char"` for `char this[int index]`
- Normal properties: `"()->PropertyType"`

**For Fields/Events:**
- Format: `"->FieldType"` or empty
- Example: `"()->System.Int32"` for `int Length`

**Purpose:**
- Disambiguate overloads
- Enable stable identity across assemblies
- Support signature-based lookups

---

### EmitScope Enum Values

**EmitScope** controls where members get emitted:

1. **`Unspecified` (0)** - Default, MUST be set during Shape phase
   - PhaseGate error (PG_FIN_001) if any member reaches emission with this value

2. **`ClassSurface`** - Emit on main class/interface
   - Normal members that don't conflict

3. **`StaticSurface`** - Emit on static surface
   - For static class members (emitted in namespace scope)

4. **`ViewOnly`** - Emit only in explicit interface views
   - Members that conflict on class surface but are needed for interface contracts
   - Accessed via `As_IInterface` properties

5. **`Omitted`** - Omit from emission
   - Unified away by `OverloadUnifier`
   - Intentionally skipped (indexers, etc.)

**Decision Flow:**
```
Load → (all Unspecified)
  ↓
Shape → (set to ClassSurface/ViewOnly/Omitted/StaticSurface)
  ↓
Emit → (PhaseGate validates: no Unspecified allowed)
```

---

### Member Provenance

**MemberProvenance** tracks where members came from:

1. **`Original`** - Declared in this type
2. **`FromInterface`** - Copied from implemented interface
3. **`Synthesized`** - Created by a shaper
4. **`HiddenNew`** - Added to resolve C# 'new' hiding
5. **`BaseOverload`** - Added from base class overload
6. **`DiamondResolved`** - Added to resolve diamond inheritance
7. **`IndexerNormalized`** - Normalized from indexer syntax
8. **`ExplicitView`** - Created for explicit interface view
9. **`OverloadReturnConflict`** - Marked ViewOnly due to return type conflict

**Purpose:**
- Understand transformation history
- Make informed emit decisions
- Debug provenance issues
- Track interface member sources

---

## Pipeline Usage

### Load Phase (Creates Model)

1. **Reflection** converts `System.Type` → `TypeSymbol`
2. **TypeReference** converts `System.Type` → immutable type references
3. **StableId** created for every type/member
4. **Members** populated with `Provenance = Original`
5. **EmitScope** defaults to `Unspecified` for all members
6. **SymbolGraph** assembled and indexed

### Shape Phase (Transforms Model)

1. **Interface closure** adds members with `Provenance = FromInterface`
2. **View planning** sets `ExplicitViews` on types
3. **Name application** sets `TsEmitName` on all types/members
4. **Constraint resolution** resolves `GenericParameterSymbol.Constraints`
5. **Emit scope assignment** sets all `EmitScope` to non-Unspecified values
6. **Graph updates** use `WithUpdatedType()` for pure transformations

### Emit Phase (Reads Model)

1. **PhaseGate** validates all `EmitScope != Unspecified`
2. **TypeScript emit** reads `TsEmitName` and `EmitScope`
3. **Metadata emit** uses `StableId` for CLR bindings
4. **Bindings emit** maps `TsEmitName` → `StableId`

---

## Summary

The **Model** phase provides:

1. **Immutable, pure data structures** for the entire type system
2. **Stable identities** that survive all transformations
3. **Type-safe representation** of CLR types and members
4. **Provenance tracking** to understand member origins
5. **Emit scope control** to manage output placement
6. **Efficient lookups** via indices
7. **Pure transformations** via wither methods

All Model structures are designed for:
- **Immutability** (records with init-only properties)
- **Structural equality** (record equality semantics)
- **Functional transformations** (wither methods, `WithUpdatedType()`)
- **Type safety** (no nulls except where semantically required)
- **Performance** (immutable collections, pre-computed indices)
