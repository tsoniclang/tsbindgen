# Phase 3: Model - Data Structures

Immutable data structures for entire symbol graph. Type-safe CLR→TypeScript bridge with stable identities, provenance tracking, emit scope control.

All are immutable records with `with` expressions.

---

## SymbolGraph.cs

### SymbolGraph
- `Namespaces, SourceAssemblies, NamespaceIndex, TypeIndex`
- `WithIndices()` - **MUST call after creation** for O(1) lookups
- `TryGetNamespace(string, out NamespaceSymbol?)`, `TryGetType(string, out TypeSymbol?)`
- `WithUpdatedType(keyOrStableId, transform)` - Auto-rebuilds indices

### SymbolGraphStatistics
- `NamespaceCount, TypeCount, MethodCount, PropertyCount, FieldCount, EventCount, TotalMembers`

---

## AssemblyKey.cs

### AssemblyKey(Name, PublicKeyToken, Culture, Version)
- `PublicKeyToken` - Hex or `"null"`, `Culture` - Name or `"neutral"`, `Version` - `"Major.Minor.Build.Revision"`
- `SimpleName` - Just `Name`
- `static From(AssemblyName)` - Normalizes
- `ToString()` - GAC format: `"System.Private.CoreLib, PublicKeyToken=..., Culture=neutral, Version=10.0.0.0"`

---

## NamespaceSymbol.cs

### NamespaceSymbol
- `Name` - Empty for root/global
- `Types` - Not nested namespace types
- `StableId, ContributingAssemblies`
- `IsRoot`, `SafeNameOrNull` - Null for root

---

## TypeSymbol.cs

### TypeSymbol

**Identity:**
- `StableId: TypeStableId` - `"AssemblyName:ClrFullName"` (e.g., `"System.Private.CoreLib:System.Collections.Generic.List`1"`)
- `ClrFullName` - `"List`1"` (backtick)
- `ClrName` - Without namespace
- `TsEmitName` - Set in Shape. `"List_1"` (underscore), nested: `"Console$Error"` (dollar)

**Classification:**
- `Namespace, Kind, Accessibility`

**Generics:**
- `Arity, GenericParameters`

**Hierarchy:**
- `BaseType` - Null for interfaces/Object/ValueType
- `Interfaces` - Direct

**Members:**
- `Members: TypeMembers, NestedTypes`

**Characteristics:**
- `IsValueType, IsAbstract, IsSealed, IsStatic, DeclaringType`

**Other:**
- `Documentation, ExplicitViews`

**Withers:**
- `WithMembers, WithAddedMethods, WithRemovedMethods, WithAddedProperties, WithRemovedProperties, WithAddedFields, WithTsEmitName, WithExplicitViews`

### TypeKind
Class, Struct, Interface, Enum, Delegate, StaticNamespace

### GenericParameterSymbol
- `Id` - Declaring type + position
- `Name, Position`
- `Constraints` - Resolved by ConstraintCloser (initially empty)
- `RawConstraintTypes` - Raw CLR (null after resolution)
- `Variance` - None, Covariant (out), Contravariant (in)
- `SpecialConstraints` - struct, class, new(), notnull

### Variance
None, Covariant, Contravariant

### GenericParameterConstraints (Flags)
None, ReferenceType, ValueType, DefaultConstructor, NotNullable

### TypeMembers
- `Methods, Properties, Fields, Events, Constructors`
- `static Empty`

### Accessibility
Public, Protected, Internal, ProtectedInternal, Private, PrivateProtected

---

## MethodSymbol.cs

### MethodSymbol
- **Identity:** `StableId` (e.g., `"Asm:Type::Method(Params)->Return"`), `ClrName, TsEmitName`
- **Signature:** `ReturnType` (System.Void for void), `Parameters, GenericParameters, Arity`
- **Modifiers:** `IsStatic, IsAbstract, IsVirtual, IsOverride, IsSealed, IsNew`
- **Provenance & Scope:** `Provenance, EmitScope` (**MUST set in Shape**, PG_FIN_001 if Unspecified)
- **Other:** `Visibility, SourceInterface, Documentation`
- **Method:** `WithSourceInterface`

### ParameterSymbol
- `Name, Type, IsRef, IsOut, IsParams, HasDefaultValue, DefaultValue`

### Visibility
Public, Protected, Internal, ProtectedInternal, PrivateProtected, Private

### MemberProvenance
Original, FromInterface, Synthesized, HiddenNew, BaseOverload, DiamondResolved, IndexerNormalized, ExplicitView, OverloadReturnConflict

### EmitScope
- **Unspecified (0)** - **MUST change**, PG_FIN_001 if reaches emission
- **ClassSurface** - Main class/interface
- **StaticSurface** - Static class members
- **ViewOnly** - Only in As_IInterface views
- **Omitted** - Unified/skipped

Flow: Load→Unspecified → Shape→ClassSurface/ViewOnly/Omitted/StaticSurface → Emit→validates

---

## PropertySymbol.cs

### PropertySymbol
- `StableId, ClrName` (`"Item"` for indexers), `TsEmitName`
- `PropertyType, IndexParameters` (empty for normal), `IsIndexer`
- `HasGetter, HasSetter`
- `IsStatic, IsVirtual, IsOverride, IsAbstract`
- `Visibility, Provenance, EmitScope, SourceInterface, Documentation`
- `WithSourceInterface`

---

## FieldSymbol.cs

### FieldSymbol
- `StableId, ClrName, TsEmitName, FieldType`
- `IsStatic, IsReadOnly, IsConst, ConstValue`
- `Visibility, Provenance, EmitScope, Documentation`

---

## EventSymbol.cs

### EventSymbol
- `StableId, ClrName, TsEmitName, EventHandlerType`
- `IsStatic, IsVirtual, IsOverride`
- `Visibility, Provenance, EmitScope, SourceInterface, Documentation`
- `WithSourceInterface`

---

## ConstructorSymbol.cs

### ConstructorSymbol
- `StableId, Parameters, IsStatic, Visibility, Documentation`
- **Note:** No Provenance/EmitScope (always emitted)

---

## TypeReference.cs

### TypeReference
`abstract record TypeReference { TypeReferenceKind Kind }`

Recursive: Named types, Generic parameters, Constructed generics, Arrays, Pointers, ByRef, Nested types

### TypeReferenceKind
Named, GenericParameter, Array, Pointer, ByRef, Nested, Placeholder

### NamedTypeReference
- `Kind` → Named
- `AssemblyName, FullName, Namespace, Name, Arity`
- `TypeArguments: IReadOnlyList<TypeReference>` - Empty for open generics
- `IsValueType`
- `InterfaceStableId: string?` - `"AssemblyName:FullName"` for interfaces, null otherwise

### GenericParameterReference
- `Kind` → GenericParameter
- `Id, Name, Position, Constraints`

### ArrayTypeReference
- `Kind` → Array
- `ElementType: TypeReference, Rank` - 1 for T[], 2 for T[,]

### PointerTypeReference
- `Kind` → Pointer
- `PointeeType: TypeReference, Depth` - 1 for T*, 2 for T**

### ByRefTypeReference
- `Kind` → ByRef
- `ReferencedType: TypeReference`

### NestedTypeReference
- `Kind` → Nested
- `DeclaringType: TypeReference, NestedName, FullReference: NamedTypeReference`

### PlaceholderTypeReference
- `Kind` → Placeholder, `DebugName`
- **Never in final output** - emits `any` with diagnostic

---

## GenericParameterId.cs

### GenericParameterId
- `DeclaringTypeName, Position, IsMethodParameter`
- `ToString()` - `"DeclaringTypeName#Position"` (+ `"M"` if method)

---

## StableId.cs

### StableId
`abstract record StableId { string AssemblyName }`

### TypeStableId
- `AssemblyName, ClrFullName`
- ToString: `"AssemblyName:ClrFullName"` (e.g., `"System.Private.CoreLib:System.Collections.Generic.List`1"`)

### MemberStableId
- `AssemblyName, DeclaringClrFullName, MemberName, CanonicalSignature`
- `MetadataToken: int?` - **NOT in equality**
- ToString: `"AssemblyName:DeclaringClrFullName::MemberName[CanonicalSignature]"`
- Equality: **Excludes MetadataToken**

---

## Key Concepts

### StableId Formats

**TypeStableId:** `"AssemblyName:ClrFullName"`
- Example: `"System.Private.CoreLib:System.String"`

**MemberStableId:** `"AssemblyName:DeclaringType::MemberName[Signature]"`
- Example: `"System.Private.CoreLib:System.String::Substring(System.Int32,System.Int32)->System.String"`
- Semantic equality (excludes MetadataToken)

### Canonical Signatures

**Methods:** `"(ParamType1,ParamType2,...)->ReturnType"`
**Properties:** `"(IndexParam1,...)->PropertyType"` or `"()->PropertyType"`
**Fields/Events:** `"->FieldType"` or empty

### EmitScope Values

1. **Unspecified (0)** - Default, MUST change (PG_FIN_001)
2. **ClassSurface** - Main class/interface
3. **StaticSurface** - Static class members
4. **ViewOnly** - As_IInterface views only
5. **Omitted** - Unified/skipped

### MemberProvenance

1. Original
2. FromInterface
3. Synthesized
4. HiddenNew
5. BaseOverload
6. DiamondResolved
7. IndexerNormalized
8. ExplicitView
9. OverloadReturnConflict

---

## Pipeline Usage

### Load Phase
1. System.Type → TypeSymbol
2. System.Type → TypeReference
3. StableId created
4. Members: Provenance = Original
5. EmitScope = Unspecified
6. SymbolGraph indexed

### Shape Phase
1. Interface closure: Provenance = FromInterface
2. View planning: ExplicitViews
3. Name application: TsEmitName
4. Constraint resolution
5. Emit scope assignment: all non-Unspecified
6. Updates via WithUpdatedType()

### Emit Phase
1. PhaseGate: validates EmitScope != Unspecified
2. TypeScript emit: reads TsEmitName, EmitScope
3. Metadata emit: uses StableId
4. Bindings emit: TsEmitName → StableId

---

## Summary

Provides: Immutable pure data structures, stable identities, type-safe CLR representation, provenance tracking, emit scope control, efficient lookups, pure transformations.

Design: Immutability, structural equality, functional transformations, type safety, performance.
