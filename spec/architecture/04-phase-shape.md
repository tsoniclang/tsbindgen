# Phase 4: Shape - CLR to TypeScript Semantic Transformation

## Overview

The Shape phase transforms CLR semantics into TypeScript-compatible semantics. It operates on the normalized `SymbolGraph` from Phase 3 and prepares types for final emission by resolving inheritance conflicts, synthesizing missing members, planning interface views, and handling TypeScript-specific constraints.

**Key Responsibilities:**
- Flatten interface hierarchies (remove `extends`)
- Synthesize missing interface members for classes/structs
- Resolve diamond inheritance conflicts
- Add base class overloads for TypeScript compatibility
- Deduplicate members by emitted name
- Plan explicit interface views (As_IInterface properties)
- Handle indexer representation (property vs methods)
- Analyze static-side inheritance issues
- Close generic constraints for TypeScript

**Execution Order:** Shape runs after Normalize and before Renaming/Emit phases.

**PURE Transformations:** All Shape passes are pure functions that return new `SymbolGraph` instances without mutation.

---

## Pass 1: GlobalInterfaceIndex

**File:** `GlobalInterfaceIndex.cs`

### Purpose
Build cross-assembly interface indexes for member resolution. Creates two global indexes:
1. `GlobalInterfaceIndex` - All interface signatures (methods + properties)
2. `InterfaceDeclIndex` - Declared-only members (excludes inherited)

Used by later passes for:
- Type-forwarded interface resolution
- Structural conformance checking
- Finding which interface declares a member

### Public API

#### `GlobalInterfaceIndex.Build(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static void Build(BuildContext ctx, SymbolGraph graph)`

**What it does:**
- Indexes ALL public interfaces across all assemblies
- Computes method and property signatures for each interface
- Stores in global dictionary keyed by `ClrFullName`

**Algorithm:**
1. Clear previous index (`_globalIndex.Clear()`)
2. Collect all interfaces from `graph.Namespaces.SelectMany(ns => ns.Types).Where(t => t.Kind == Interface)`
3. For each interface:
   - Compute `MethodSignatures` using `CanonicalizeMethod`
   - Compute `PropertySignatures` using `CanonicalizeProperty`
   - Store as `InterfaceInfo` in `_globalIndex[iface.ClrFullName]`

**Called by:** Shape phase initialization (before other passes)

#### `GlobalInterfaceIndex.GetInterface(string fullName)`
**Signature:** `public static InterfaceInfo? GetInterface(string fullName)`

**Returns:** Interface info by full CLR name, or null if not found

#### `GlobalInterfaceIndex.ContainsInterface(string fullName)`
**Signature:** `public static bool ContainsInterface(string fullName)`

**Returns:** True if interface exists in index

#### `GlobalInterfaceIndex.GetAllInterfaces()`
**Signature:** `public static IEnumerable<InterfaceInfo> GetAllInterfaces()`

**Returns:** All indexed interfaces

### Private Methods

#### `ComputeMethodSignatures(BuildContext ctx, TypeSymbol iface)`
Computes canonical signatures for all methods in interface.

**Returns:** `HashSet<string>` of canonical method signatures

#### `ComputePropertySignatures(BuildContext ctx, TypeSymbol iface)`
Computes canonical signatures for all properties in interface.

**Returns:** `HashSet<string>` of canonical property signatures

#### `GetTypeFullName(TypeReference typeRef)`
Converts type reference to full name string for signature computation.

**Handles:** Named, Nested, GenericParameter, Array, Pointer, ByRef types

### Data Structures

#### `InterfaceInfo` Record
```csharp
public record InterfaceInfo(
    TypeSymbol Symbol,
    string FullName,
    string AssemblyName,
    HashSet<string> MethodSignatures,
    HashSet<string> PropertySignatures);
```

**Purpose:** Stores complete interface signature information

---

## Pass 2: InterfaceDeclIndex

**File:** `GlobalInterfaceIndex.cs` (second class in same file)

### Purpose
Build index of interface members that are DECLARED (not inherited). Used to resolve which interface actually declares a member when walking inheritance chains.

**Why separate from GlobalInterfaceIndex?**
- `GlobalInterfaceIndex` includes ALL members (declared + inherited)
- `InterfaceDeclIndex` includes ONLY declared members
- Needed for precise attribution when multiple interfaces define same member

### Public API

#### `InterfaceDeclIndex.Build(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static void Build(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. Clear previous index (`_declIndex.Clear()`)
2. For each interface:
   - Collect inherited signatures from `CollectInheritedSignatures(iface)`
   - For each method/property in interface:
     - Compute canonical signature
     - If NOT in inherited set → add to `declaredMethods`/`declaredProperties`
   - Store as `DeclaredMembers` in `_declIndex[iface.ClrFullName]`

**Called by:** Shape phase initialization (after GlobalInterfaceIndex.Build)

#### `InterfaceDeclIndex.GetDeclaredMembers(string ifaceFullName)`
**Signature:** `public static DeclaredMembers? GetDeclaredMembers(string ifaceFullName)`

**Returns:** Declared-only members for interface

#### `InterfaceDeclIndex.DeclaresMethod(string ifaceFullName, string canonicalSig)`
**Signature:** `public static bool DeclaresMethod(string ifaceFullName, string canonicalSig)`

**Returns:** True if interface directly declares method with this signature

#### `InterfaceDeclIndex.DeclaresProperty(string ifaceFullName, string canonicalSig)`
**Signature:** `public static bool DeclaresProperty(string ifaceFullName, string canonicalSig)`

**Returns:** True if interface directly declares property with this signature

### Private Methods

#### `CollectInheritedSignatures(TypeSymbol iface)`
Walks base interface chain and collects ALL inherited member signatures.

**Algorithm:**
- BFS traversal of `iface.Interfaces`
- For each base interface: lookup in `GlobalInterfaceIndex` and collect all signatures
- Returns union of all base signatures

### Data Structures

#### `DeclaredMembers` Record
```csharp
public record DeclaredMembers(
    string InterfaceFullName,
    HashSet<string> MethodSignatures,
    HashSet<string> PropertySignatures);
```

**Purpose:** Stores declared-only member signatures for an interface

---

## Pass 3: StructuralConformance

**File:** `StructuralConformance.cs`

### Purpose
Analyze structural conformance for interfaces. For each interface that cannot be structurally implemented on the class surface, synthesize ViewOnly members that will appear in explicit views (As_IInterface properties).

**Key Concept:** In TypeScript, a class implements an interface structurally (duck typing). If the class surface doesn't satisfy the interface signature, we must create ViewOnly members for explicit views.

### Public API

#### `StructuralConformance.Analyze(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Analyze(BuildContext ctx, SymbolGraph graph)`

**What it does:**
- For each class/struct that implements interfaces:
  - Build class surface (representable members excluding ViewOnly)
  - For each interface:
    - Build substituted interface surface (flattened + type args)
    - Check if class surface satisfies interface using TS assignability
    - For missing members: synthesize ViewOnly clones with interface StableId
  - Add synthesized ViewOnly members to type immutably
- Returns new `SymbolGraph` with ViewOnly members added

**Algorithm:**
1. Filter classes/structs: `graph.Namespaces.SelectMany(ns => ns.Types).Where(t => t.Kind == Class || Struct)`
2. For each type: call `AnalyzeType(ctx, graph, type)` → returns (updated type, synthesized count)
3. Build updated namespaces immutably
4. Return new graph with indices: `(graph with { Namespaces = ... }).WithIndices()`

**Called by:** Shape phase (after InterfaceInliner)

### Private Methods

#### `AnalyzeType(BuildContext ctx, SymbolGraph graph, TypeSymbol type)`
**Returns:** `(TypeSymbol UpdatedType, int SynthesizedCount)`

**Algorithm:**
1. Build `classSurface = BuildClassSurface(ctx, type)` (excludes ViewOnly)
2. For each `ifaceRef` in `type.Interfaces`:
   - Check `WillPlanViewFor(ctx, graph, type, ifaceRef)` → skip if false
   - Find interface: `FindInterface(graph, ifaceRef)`
   - Build `interfaceSurface = BuildInterfaceSurface(ctx, graph, ifaceRef, iface)`
   - For each interface method/property:
     - Check `classSurface.IsTsAssignableMethod/Property(ifaceMember)` → skip if satisfied
     - Synthesize ViewOnly member with interface's StableId
3. Validate no duplicates in synthesized list
4. Validate no duplicates with existing members
5. Add ViewOnly members to type: `type.WithAddedMethods(...).WithAddedProperties(...)`

#### `WillPlanViewFor(BuildContext ctx, SymbolGraph graph, TypeSymbol type, TypeReference ifaceRef)`
**Returns:** `bool` - True if we will emit a view for this interface

**Purpose:** Gate synthesis - only create ViewOnly members for interfaces we'll actually emit views for

#### `BuildClassSurface(BuildContext ctx, TypeSymbol type)`
**Returns:** `ClassSurface` - Representable members on class (excludes ViewOnly)

**Algorithm:**
- Filter `type.Members.Methods.Where(m => m.EmitScope != ViewOnly && IsRepresentable(m))`
- Filter `type.Members.Properties.Where(p => p.EmitScope != ViewOnly && IsRepresentable(p))`
- Return `ClassSurface(methods, properties, ctx)`

#### `BuildInterfaceSurface(BuildContext ctx, SymbolGraph graph, TypeReference closedIfaceRef, TypeSymbol ifaceSymbol)`
**Returns:** `InterfaceSurface` - Flattened interface members with type args substituted

**Algorithm:**
1. For each method in `ifaceSymbol.Members.Methods`:
   - Compute canonical signature
   - Find declaring interface: `InterfaceResolver.FindDeclaringInterface(...)`
   - Substitute type parameters: `SubstituteMethodTypeParameters(method, closedIfaceRef)`
   - Add `(substitutedMethod, declaringIface)` to list
2. For each property (skip indexers):
   - Same process as methods
3. Return `InterfaceSurface(methods, properties)`

#### `SynthesizeViewOnlyMethod(BuildContext ctx, TypeSymbol type, MethodSymbol ifaceMethod, TypeReference declaringInterface)`
**Returns:** `MethodSymbol` - Synthesized ViewOnly method

**Key:** Uses interface member's StableId (NOT class StableId) to prevent ID conflicts

**Creates:**
```csharp
new MethodSymbol {
    StableId = ifaceMethod.StableId,  // From interface!
    ClrName = ifaceMethod.ClrName,
    ReturnType = ifaceMethod.ReturnType,
    Parameters = ifaceMethod.Parameters,
    GenericParameters = ifaceMethod.GenericParameters,
    IsStatic = false,
    IsVirtual = true,
    Provenance = MemberProvenance.ExplicitView,
    EmitScope = EmitScope.ViewOnly,
    SourceInterface = declaringInterface
}
```

#### `SynthesizeViewOnlyProperty(BuildContext ctx, TypeSymbol type, PropertySymbol ifaceProperty, TypeReference declaringInterface)`
**Returns:** `PropertySymbol` - Synthesized ViewOnly property

**Same logic as method synthesis**

### Helper Classes

#### `ClassSurface` Record
```csharp
private record ClassSurface(
    List<MethodSymbol> Methods,
    List<PropertySymbol> Properties,
    BuildContext Ctx)
```

**Methods:**
- `IsTsAssignableMethod(MethodSymbol ifaceMethod)` - Check TS-level assignability
- `IsTsAssignableProperty(PropertySymbol ifaceProperty)` - Check TS-level assignability
- `HasMethod/HasProperty(...)` - Check if member exists by canonical signature

**Algorithm for `IsTsAssignableMethod`:**
1. Find candidates by name (case-insensitive)
2. For each candidate:
   - Erase to TS signatures: `EraseMethodForAssignability(method)`
   - Check `TsAssignability.IsMethodAssignable(classSig, ifaceSig)`
3. Return true if any candidate assignable

#### `InterfaceSurface` Record
```csharp
private record InterfaceSurface(
    List<(MethodSymbol Method, TypeReference DeclaringIface)> Methods,
    List<(PropertySymbol Property, TypeReference DeclaringIface)> Properties)
```

**Purpose:** Store interface members with their declaring interface

---

## Pass 4: InterfaceInliner

**File:** `InterfaceInliner.cs`

### Purpose
Flatten interface hierarchies - remove `extends` chains. Copies all inherited members into each interface so TypeScript doesn't need extends.

**Why?** TypeScript `extends` causes variance issues and complicates type checking. Safer to flatten everything.

### Public API

#### `InterfaceInliner.Inline(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Inline(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. Collect all interfaces: `graph.Namespaces.SelectMany(ns => ns.Types).Where(t => t.Kind == Interface)`
2. For each interface: `updatedGraph = InlineInterface(ctx, updatedGraph, iface)`
3. Return updated graph

**Called by:** Shape phase (before StructuralConformance)

### Private Methods

#### `InlineInterface(BuildContext ctx, SymbolGraph graph, TypeSymbol iface)`
**Returns:** `SymbolGraph` - Graph with interface hierarchy flattened

**Algorithm:**
1. Collect members:
   - Start with `iface.Members.Methods/Properties/Events`
   - BFS traversal of `iface.Interfaces`:
     - For each base interface: find in graph, add ALL members
     - Queue grandparents for visiting
2. Deduplicate:
   - `DeduplicateMethods(ctx, allMembers)` - By canonical signature
   - `DeduplicateProperties(ctx, allProperties)` - By name (TS doesn't allow property overloads)
   - `DeduplicateEvents(ctx, allEvents)` - By canonical signature
3. Update interface:
   - Create new `TypeMembers` with deduplicated members
   - Clear `Interfaces` array (no more extends)
   - Return `graph.WithUpdatedType(iface.StableId, t => t with { Members = newMembers, Interfaces = Empty })`

#### `DeduplicateMethods(BuildContext ctx, List<MethodSymbol> methods)`
**Returns:** `IReadOnlyList<MethodSymbol>` - Unique methods by canonical signature

**Algorithm:**
- Build `Dictionary<string, MethodSymbol>` keyed by canonical signature
- Keep first occurrence (deterministic)

#### `DeduplicateProperties(BuildContext ctx, List<PropertySymbol> properties)`
**Returns:** `IReadOnlyList<PropertySymbol>` - Unique properties

**Algorithm:**
- For indexers: deduplicate by full signature (name + params + type)
- For regular properties: deduplicate by name only (TS doesn't allow overloads)
- Keep first occurrence

#### `DeduplicateEvents(BuildContext ctx, List<EventSymbol> events)`
**Returns:** `IReadOnlyList<EventSymbol>` - Unique events by canonical signature

**Key Transformation:**
```
Before:
  interface IEnumerable<T> : IEnumerable {
    // Only declares GetEnumerator() : IEnumerator<T>
  }
  interface IEnumerable {
    // Declares GetEnumerator() : IEnumerator
  }

After:
  interface IEnumerable_1<T> {
    // Both members inlined:
    GetEnumerator() : IEnumerator_1<T>
    GetEnumerator() : IEnumerator
  }
  interface IEnumerable {
    // Just its member:
    GetEnumerator() : IEnumerator
  }
```

---

## Pass 5: ExplicitImplSynthesizer

**File:** `ExplicitImplSynthesizer.cs`

### Purpose
Synthesize missing interface members for classes/structs. Ensures all interface-required members exist on implementing types. In C#, explicit interface implementations (EII) are invisible on the class - we must synthesize them.

### Public API

#### `ExplicitImplSynthesizer.Synthesize(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Synthesize(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. Filter classes/structs: `graph.Namespaces.SelectMany(ns => ns.Types).Where(t => t.Kind == Class || Struct)`
2. For each type: `(updatedGraph, synthesizedCount) = SynthesizeForType(ctx, updatedGraph, type)`
3. Return updated graph with synthesized members

**Called by:** Shape phase (after InterfaceInliner, before DiamondResolver)

### Private Methods

#### `SynthesizeForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type)`
**Returns:** `(SymbolGraph UpdatedGraph, int SynthesizedCount)`

**Algorithm:**
1. Validate no duplicates in existing members (debug check)
2. Collect required members: `requiredMembers = CollectInterfaceMembers(ctx, graph, type)`
3. Find missing: `missing = FindMissingMembers(ctx, type, requiredMembers)`
4. Synthesize missing:
   - For each `(iface, method)` in `missing.Methods`: synthesize method
   - For each `(iface, property)` in `missing.Properties`: synthesize property
5. Deduplicate synthesized list by StableId (multiple interfaces may require same member)
6. Validate no duplicates within synthesized list
7. Validate no duplicates with existing members
8. Add to type: `graph.WithUpdatedType(type.StableId, t => t with { Members = ... })`

**Deduplication:** Multiple interfaces (e.g., ICollection, IList) may require same member (e.g., CopyTo). Keep first synthesis by StableId.

#### `CollectInterfaceMembers(BuildContext ctx, SymbolGraph graph, TypeSymbol type)`
**Returns:** `InterfaceMembers` - All methods/properties required by interfaces

**Algorithm:**
1. For each `ifaceRef` in `type.Interfaces`:
   - Check `WillPlanViewFor(ctx, graph, type, ifaceRef)` → skip if false
   - Find interface in graph
   - Collect all methods (skip indexer properties)
   - Add `(ifaceRef, member)` to lists
2. Return `InterfaceMembers(methods, properties)`

#### `FindMissingMembers(BuildContext ctx, TypeSymbol type, InterfaceMembers required)`
**Returns:** `MissingMembers` - Members that exist in interface but not class

**Algorithm:**
- For each required method: check `type.Members.Methods.Any(m => m.StableId.Equals(method.StableId))`
- For each required property: check `type.Members.Properties.Any(p => p.StableId.Equals(property.StableId))`
- If not found → add to missing list

**Key Fix:** Compare by StableId directly (not re-canonicalizing signatures)

#### `SynthesizeMethod(BuildContext ctx, TypeSymbol type, TypeReference iface, MethodSymbol method)`
**Returns:** `MethodSymbol` - Synthesized EII method

**Algorithm:**
1. Compute canonical signature
2. Resolve declaring interface: `InterfaceResolver.FindDeclaringInterface(...)`
3. Use interface member's StableId (NOT class StableId)
4. Create new `MethodSymbol`:
   ```csharp
   new MethodSymbol {
       StableId = method.StableId,  // From interface!
       ClrName = method.ClrName,
       Provenance = MemberProvenance.ExplicitView,
       EmitScope = EmitScope.ViewOnly,
       SourceInterface = declaringInterface ?? iface
   }
   ```

#### `SynthesizeProperty(BuildContext ctx, TypeSymbol type, TypeReference iface, PropertySymbol property)`
**Returns:** `PropertySymbol` - Synthesized EII property

**Same logic as method synthesis**

### Data Structures

#### `InterfaceMembers` Record
```csharp
private record InterfaceMembers(
    List<(TypeReference Iface, MethodSymbol Method)> Methods,
    List<(TypeReference Iface, PropertySymbol Property)> Properties)
```

#### `MissingMembers` Record
```csharp
private record MissingMembers(
    List<(TypeReference Iface, MethodSymbol Method)> Methods,
    List<(TypeReference Iface, PropertySymbol Property)> Properties)
{
    public int Count => Methods.Count + Properties.Count;
}
```

---

## Pass 6: InterfaceResolver

**File:** `InterfaceResolver.cs`

### Purpose
Resolve interface members to their declaring interface. Determines which interface in an inheritance chain actually declares a member.

**Why needed?** When IList<T> : ICollection<T> both have Add(), we need to know which interface declared it first.

### Public API

#### `InterfaceResolver.FindDeclaringInterface(TypeReference closedIface, string memberCanonicalSig, bool isMethod, BuildContext ctx)`
**Signature:**
```csharp
public static TypeReference? FindDeclaringInterface(
    TypeReference closedIface,
    string memberCanonicalSig,
    bool isMethod,
    BuildContext ctx)
```

**Parameters:**
- `closedIface` - The closed interface reference (e.g., ICollection<TFoo>)
- `memberCanonicalSig` - Canonical signature after substitution
- `isMethod` - True if method, false if property

**Returns:** Closed interface reference that declares the member, or null if not found

**Algorithm:**
1. Check cache: `_declaringInterfaceCache.TryGetValue((closedIfaceName, memberCanonicalSig), out var cached)`
2. Get generic definition name: `GetGenericDefinitionName(closedIfaceName)`
3. Build inheritance chain: `BuildInterfaceChain(closedIface, ctx)` → top-down order
4. Walk chain from ancestors to immediate:
   - For each interface: check `InterfaceDeclIndex.DeclaresMethod/Property(ifaceDefName, memberCanonicalSig)`
   - Collect candidates that declare this signature
5. Pick winner:
   - 0 candidates → null (shouldn't happen)
   - 1 candidate → that interface
   - Multiple candidates → pick most ancestral (first in chain)
6. Cache result and return

#### `InterfaceResolver.ClearCache()`
**Signature:** `public static void ClearCache()`

**Purpose:** Clear cache for testing or when rebuilding index

### Private Methods

#### `BuildInterfaceChain(TypeReference iface, BuildContext ctx)`
**Returns:** `List<TypeReference>` - Inheritance chain from roots to given interface (top-down)

**Algorithm:**
- Recursive BFS: `BuildInterfaceChainRecursive(iface, chain, visited, ctx)`
- For each interface:
  - Process base interfaces first (recursion)
  - Add current interface to chain
- Reverse chain to get top-down order (roots first)

#### `BuildInterfaceChainRecursive(...)`
**Algorithm:**
1. Check visited set → skip if already processed
2. Get interface info: `GlobalInterfaceIndex.GetInterface(genericDefName)`
3. For each base interface:
   - Substitute type arguments: `SubstituteTypeArguments(baseIfaceRef, closedIface)`
   - Recurse: `BuildInterfaceChainRecursive(closedBaseRef, ...)`
4. Add current interface to chain

#### `GetGenericDefinitionName(string fullName)`
**Returns:** Generic definition name (with backtick arity)

**Example:** `"System.Collections.Generic.IEnumerable`1[[System.Int32]]"` → `"System.Collections.Generic.IEnumerable`1"`

**Algorithm:** Strip type arguments if present (find `[[`, return substring before it)

---

## Pass 7: DiamondResolver

**File:** `DiamondResolver.cs`

### Purpose
Resolve diamond inheritance conflicts. When multiple inheritance paths bring the same method with potentially different signatures, ensure all variants are available in TypeScript.

**Diamond Pattern:**
```
    IBase
   /    \
  IA    IB
   \    /
   Class
```
If IA and IB both override IBase.Method() with different signatures, we have a conflict.

### Public API

#### `DiamondResolver.Resolve(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Resolve(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. Check policy strategy: `ctx.Policy.Interfaces.DiamondResolution`
   - If `Error` → call `AnalyzeForDiamonds(ctx, graph)` and return unchanged graph
2. For each type: `(updatedGraph, resolved) = ResolveForType(ctx, updatedGraph, type, strategy)`
3. Return updated graph

**Called by:** Shape phase (after ExplicitImplSynthesizer)

### Private Methods

#### `ResolveForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type, DiamondResolutionStrategy strategy)`
**Returns:** `(SymbolGraph UpdatedGraph, int ResolvedCount)`

**Algorithm:**
- Skip enums, delegates, static namespaces
- Process per-scope to avoid cross-scope contamination:
  - `ResolveForScope(ctx, graph, type, EmitScope.ClassSurface, strategy)`
  - `ResolveForScope(ctx, graph, type, EmitScope.ViewOnly, strategy)`

#### `ResolveForScope(BuildContext ctx, SymbolGraph graph, TypeSymbol type, EmitScope scope, DiamondResolutionStrategy strategy)`
**Returns:** `(SymbolGraph UpdatedGraph, int Detected)`

**Algorithm:**
1. Group methods by CLR name: `type.Members.Methods.Where(m => m.EmitScope == scope).GroupBy(m => m.ClrName)`
2. Filter groups with multiple methods
3. For each group:
   - Group by canonical signature
   - If all same signature → no conflict, skip
   - If multiple signatures → diamond conflict detected
4. Log conflicts (PhaseGate will validate)
5. Strategy handling:
   - `OverloadAll` → keep all overloads (already in members list)
   - `PreferDerived` → log preference (don't modify scopes)
6. Return graph unchanged (detection only, PhaseGate handles validation)

**Note:** This pass DETECTS conflicts but doesn't modify EmitScope. PhaseGate validation will catch duplicates if strategy causes problems.

#### `AnalyzeForDiamonds(BuildContext ctx, SymbolGraph graph)`
**Purpose:** Analyze mode - report all diamond conflicts as warnings

**Algorithm:**
- For each type:
  - Group methods by name
  - For groups with multiple signatures → emit `DiagnosticCodes.DiamondInheritance` warning

---

## Pass 8: BaseOverloadAdder

**File:** `BaseOverloadAdder.cs`

### Purpose
Add base class overloads when derived class differs. In TypeScript, all overloads must be present on the derived class (unlike C# where they're inherited).

**Example:**
```csharp
class Base {
    void Method(int x) { }
    void Method(string s) { }
}
class Derived : Base {
    override void Method(int x) { }  // TS error: missing Method(string)
}
```

### Public API

#### `BaseOverloadAdder.AddOverloads(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph AddOverloads(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. Debug check: validate no duplicates exist before processing
2. Filter classes with base types: `graph.Namespaces.SelectMany(ns => ns.Types).Where(t => t.Kind == Class && t.BaseType != null)`
3. For each class: `(updatedGraph, added) = AddOverloadsForClass(ctx, updatedGraph, derivedClass)`
4. Return updated graph

**Called by:** Shape phase (after DiamondResolver)

### Private Methods

#### `AddOverloadsForClass(BuildContext ctx, SymbolGraph graph, TypeSymbol derivedClass)`
**Returns:** `(SymbolGraph UpdatedGraph, int AddedCount)`

**Algorithm:**
1. Find base class: `FindBaseClass(graph, derivedClass)` → skip if external/System.Object
2. Group methods by name:
   - `derivedMethodsByName = derivedClass.Members.Methods.Where(m => !m.IsStatic).GroupBy(m => m.ClrName)`
   - `baseMethodsByName = baseClass.Members.Methods.Where(m => !m.IsStatic).GroupBy(m => m.ClrName)`
3. For each base method name:
   - If derived doesn't override → skip (keeps base methods)
   - For each base method:
     - Build expected StableId for derived version
     - Check if derived has this exact signature
     - If missing → synthesize with `CreateBaseOverloadMethod(...)`
4. Deduplicate synthesized list by StableId (base hierarchy may have same method at multiple levels)
5. Validate no duplicates within added list
6. Validate no duplicates with existing members
7. Add to derived: `graph.WithUpdatedType(derivedClass.StableId, t => t with { Members = ... })`

**Key Fix:** Compare by StableId directly (not re-canonicalizing)

#### `CreateBaseOverloadMethod(BuildContext ctx, TypeSymbol derivedClass, MethodSymbol baseMethod)`
**Returns:** `MethodSymbol` - Synthesized base overload

**Algorithm:**
1. Create StableId for derived location:
   ```csharp
   new MemberStableId {
       AssemblyName = derivedClass.StableId.AssemblyName,
       DeclaringClrFullName = derivedClass.ClrFullName,
       MemberName = baseMethod.ClrName,
       CanonicalSignature = ctx.CanonicalizeMethod(...)
   }
   ```
2. Reserve name with Renamer: `ctx.Renamer.ReserveMemberName(stableId, baseMethod.ClrName, typeScope, "BaseOverload", isStatic: false)`
3. Create method:
   ```csharp
   new MethodSymbol {
       StableId = stableId,
       ClrName = baseMethod.ClrName,
       ReturnType = baseMethod.ReturnType,
       Parameters = baseMethod.Parameters,
       Provenance = MemberProvenance.BaseOverload,
       EmitScope = EmitScope.ClassSurface
   }
   ```

#### `FindBaseClass(SymbolGraph graph, TypeSymbol derivedClass)`
**Returns:** `TypeSymbol?` - Base class, or null if external/System.Object

**Skips:** System.Object, System.ValueType

---

## Pass 9: OverloadReturnConflictResolver

**File:** `OverloadReturnConflictResolver.cs`

### Purpose
Resolve return-type conflicts in overloads. TypeScript doesn't support method overloads that differ only in return type. Detects such conflicts and logs them (PhaseGate validates).

**Example:**
```csharp
// C# allows:
int GetValue(string key);
string GetValue(string key);  // Different return type

// TypeScript doesn't allow this!
```

### Public API

#### `OverloadReturnConflictResolver.Resolve(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Resolve(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. For each type: `(updatedGraph, resolved) = ResolveForType(ctx, updatedGraph, type)`
2. Return updated graph

**Called by:** Shape phase (after BaseOverloadAdder)

### Private Methods

#### `ResolveForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type)`
**Returns:** `(SymbolGraph UpdatedGraph, int ResolvedCount)`

**Algorithm:**
- Skip enums, delegates, static namespaces
- Process per-scope:
  - `ResolveForScope(ctx, graph, type, EmitScope.ClassSurface)`
  - `ResolveForScope(ctx, graph, type, EmitScope.ViewOnly)`

#### `ResolveForScope(BuildContext ctx, SymbolGraph graph, TypeSymbol type, EmitScope scope)`
**Returns:** `(SymbolGraph UpdatedGraph, int Detected)`

**Algorithm:**
1. Group methods by signature excluding return type:
   - `type.Members.Methods.Where(m => m.EmitScope == scope).GroupBy(m => GetSignatureWithoutReturn(ctx, m))`
2. For each group with multiple methods:
   - Get return types: `methods.Select(m => GetTypeFullName(m.ReturnType)).Distinct()`
   - If multiple return types → conflict detected
   - Log conflict (PhaseGate will validate)
3. Same process for indexer properties (check property type conflicts)
4. Return graph unchanged (detection only)

**Note:** Doesn't modify EmitScope - just logs conflicts for PhaseGate validation

#### `GetSignatureWithoutReturn(BuildContext ctx, MethodSymbol method)`
**Returns:** `string` - Signature without return type

**Format:** `"MethodName(param1Type,param2Type,...)"`

#### `GetPropertySignatureWithoutReturn(BuildContext ctx, PropertySymbol property)`
**Returns:** `string` - Indexer signature without property type

**Format:** `"this[param1Type,param2Type,...]|accessor=get/set/both/none"`

**Why include accessor?** Getters and setters shouldn't conflict with each other

---

## Pass 10: MemberDeduplicator

**File:** `MemberDeduplicator.cs`

### Purpose
Final deduplication pass to remove any duplicate members introduced by multiple Shape passes (BaseOverloadAdder, ExplicitImplSynthesizer, etc.). Keeps first occurrence of each unique StableId.

**Safety Net:** Previous passes try to avoid creating duplicates, but this ensures clean graph before Renaming.

### Public API

#### `MemberDeduplicator.Deduplicate(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Deduplicate(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. For each namespace:
   - For each type:
     - Deduplicate methods by StableId
     - Deduplicate properties by StableId
     - Deduplicate fields by StableId
     - Deduplicate events by StableId
     - Deduplicate constructors by StableId
   - If any duplicates removed → create new type with unique members
2. Build new graph with deduplicated namespaces

**Called by:** Shape phase (after all other Shape passes, before ViewPlanner)

### Private Methods

#### `DeduplicateByStableId<T>(ImmutableArray<T> members, out int duplicatesRemoved)`
**Returns:** `ImmutableArray<T>` - Unique members

**Algorithm:**
1. Use reflection to get `StableId` property
2. Build `HashSet<StableId>` to track seen IDs
3. For each member:
   - If not seen → add to unique list and mark as seen
   - If already seen → skip (duplicate)
4. Return unique list and count of removed duplicates

**Generic:** Works for MethodSymbol, PropertySymbol, FieldSymbol, EventSymbol, ConstructorSymbol

---

## Pass 11: ViewPlanner

**File:** `ViewPlanner.cs`

### Purpose
Plan explicit interface views (As_IInterface properties). Creates As_IInterface properties for interfaces that couldn't be structurally implemented. These properties expose interface-specific members marked ViewOnly.

**Key Concept:** ViewOnly members (synthesized by StructuralConformance/ExplicitImplSynthesizer) must be accessible through explicit views.

### Public API

#### `ViewPlanner.Plan(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Plan(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. Filter classes/structs: `graph.Namespaces.SelectMany(ns => ns.Types).Where(t => t.Kind == Class || Struct)`
2. For each type:
   - Plan views: `plannedViews = PlanViewsForType(ctx, graph, type)`
   - If views planned → attach to type: `updatedGraph.WithUpdatedType(type.StableId, t => t.WithExplicitViews(plannedViews))`
3. Return updated graph

**Called by:** Shape phase (after MemberDeduplicator)

### Private Methods

#### `PlanViewsForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type)`
**Returns:** `List<ExplicitView>` - Views to create for this type

**Algorithm:**
1. Skip interfaces and static types (they ARE the view)
2. Collect ALL ViewOnly members with SourceInterface:
   - `type.Members.Methods.Where(m => m.EmitScope == ViewOnly && m.SourceInterface != null)`
   - `type.Members.Properties.Where(p => p.EmitScope == ViewOnly && p.SourceInterface != null)`
   - `type.Members.Events.Where(e => e.EmitScope == ViewOnly && e.SourceInterface != null)`
3. Group by interface StableId: `viewOnlyMembers.GroupBy(x => GetInterfaceStableId(x.ifaceRef))`
4. For each interface group:
   - Collect ViewMembers: `new ViewMember(Kind, StableId, ClrName)`
   - Check for duplicate StableIds → throw if found (data integrity bug)
   - Merge with existing views if present: union by MemberStableId
   - Create ExplicitView:
     ```csharp
     new ExplicitView(
         InterfaceReference: ifaceRef,
         ViewPropertyName: CreateViewName(ifaceRef),
         ViewMembers: viewMembers.ToImmutableArray())
     ```
5. Return planned views

**Validation:** Throws if same ViewMember appears twice with identical StableId (upstream bug)

#### `GetInterfaceStableId(TypeReference ifaceRef)`
**Returns:** `string` - Assembly-qualified identifier for interface

**Format:** `"{assemblyName}:{fullName}"` or `"{declaringType}+{nestedName}"` for nested

#### `CreateViewName(TypeReference ifaceRef)`
**Returns:** `string` - View property name

**Examples:**
- `IDisposable` → `As_IDisposable`
- `IEnumerable<string>` → `As_IEnumerable_1_of_string`
- `IDictionary<string, int>` → `As_IDictionary_2_of_string_and_int`

**Algorithm:**
1. Get base name: `IEnumerable`1` → `IEnumerable_1`
2. If generic: append `_of_` + type arg names joined by `_and_`
3. Sanitize type arg names: replace backticks/dots with underscores

### Data Structures

#### `ExplicitView` Record
```csharp
public sealed record ExplicitView(
    TypeReference InterfaceReference,
    string ViewPropertyName,
    ImmutableArray<ViewMember> ViewMembers)
```

#### `ViewMember` Record
```csharp
public record ViewMember(
    ViewMemberKind Kind,
    MemberStableId StableId,
    string ClrName)
```

#### `ViewMemberKind` Enum
```csharp
public enum ViewMemberKind {
    Method,
    Property,
    Event
}
```

---

## Pass 12: ClassSurfaceDeduplicator

**File:** `ClassSurfaceDeduplicator.cs`

### Purpose
Deduplicate class surface by emitted name (post-camelCase). When multiple properties emit to the same name, keep the most specific one and demote others to ViewOnly.

**Example:**
```csharp
class Foo {
    object Current { get; }              // IEnumerator.Current
    string Current { get; }              // IEnumerator<string>.Current
}
// Both emit to "current" in TS → keep string version, demote object version to ViewOnly
```

### Public API

#### `ClassSurfaceDeduplicator.Deduplicate(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Deduplicate(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. For each namespace → type:
   - `(updatedType, demoted) = DeduplicateType(ctx, type)`
   - If demoted > 0 → use updated type
2. Return new graph with updated types

**Called by:** Shape phase (after ViewPlanner)

### Private Methods

#### `DeduplicateType(BuildContext ctx, TypeSymbol type)`
**Returns:** `(TypeSymbol UpdatedType, int Demoted)`

**Algorithm:**
- Only process classes/structs
- Deduplicate properties: `DeduplicateProperties(ctx, type)`
- Could deduplicate methods but property duplicates are main issue

#### `DeduplicateProperties(BuildContext ctx, TypeSymbol type)`
**Returns:** `(ImmutableArray<PropertySymbol> Updated, int Demoted)`

**Algorithm:**
1. Group class-surface properties by emitted name (camelCase):
   - `type.Members.Properties.Where(p => p.EmitScope == ClassSurface).GroupBy(p => ApplyCamelCase(p.ClrName))`
2. Filter groups with duplicates: `.Where(g => g.Count() > 1)`
3. For each duplicate group:
   - Pick winner: `PickWinner(candidates)`
   - Demote all losers to ViewOnly: add to demotions set
4. Apply demotions: `type.Members.Properties.Select(p => demotions.Contains(p.StableId) ? p with { EmitScope = ViewOnly } : p)`
5. Return updated properties and demotion count

#### `PickWinner(List<PropertySymbol> candidates)`
**Returns:** `PropertySymbol` - Winner to keep on class surface

**Preference order:**
1. Non-explicit over explicit (`Provenance != ExplicitView`)
2. Generic over non-generic (`GenericParameterReference` vs concrete)
3. Narrower type over `object` (`!= System.Object`)
4. Stable ordering by `(DeclaringClrFullName, CanonicalSignature)`

**Example:** `IEnumerator<T>.Current` (generic T) wins over `IEnumerator.Current` (object)

#### `ApplyCamelCase(string name)`
**Returns:** `string` - Name with lowercase first character

**Example:** `"Current"` → `"current"`

---

## Pass 13: HiddenMemberPlanner

**File:** `HiddenMemberPlanner.cs`

### Purpose
Plan handling of C# 'new' hidden members. When a derived class hides a base member with 'new', we need to emit both:
- Base member (inherited)
- Derived member (with suffix like "_new")

Uses Renamer to reserve names with HiddenNewConflict reason.

### Public API

#### `HiddenMemberPlanner.Plan(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static void Plan(BuildContext ctx, SymbolGraph graph)`

**What it does:**
- For each class/struct with base type:
  - Find methods marked `IsNew`
  - Reserve renamed version through Renamer (e.g., `Method_new`)
- DOES NOT modify graph (pure planning - Renamer handles names)

**Called by:** Shape phase (after ClassSurfaceDeduplicator)

### Private Methods

#### `ProcessType(BuildContext ctx, TypeSymbol type)`
**Returns:** `int` - Count of hidden members processed

**Algorithm:**
1. Skip non-classes/non-structs
2. Skip types without base type
3. For each method with `IsNew`:
   - Build requested name: `method.ClrName + ctx.Policy.Classes.HiddenMemberSuffix` (default: "_new")
   - Reserve with Renamer:
     ```csharp
     ctx.Renamer.ReserveMemberName(
         method.StableId,
         requestedName,
         typeScope,
         "HiddenNewConflict",
         method.IsStatic,
         "HiddenMemberPlanner")
     ```
4. Recurse for nested types

**Note:** Properties less common to hide, currently skipped

---

## Pass 14: IndexerPlanner

**File:** `IndexerPlanner.cs`

### Purpose
Plan indexer representation (property vs methods).
- Single uniform indexer → keep as property
- Multiple/heterogeneous indexers → convert to get/set methods

**Policy-driven:** `ctx.Policy.Indexers.EmitPropertyWhenSingle` controls behavior

### Public API

#### `IndexerPlanner.Plan(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Plan(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. Find types with indexers: `graph.Namespaces.SelectMany(ns => ns.Types).Where(t => t.Members.Properties.Any(p => p.IsIndexer))`
2. For each type: `updatedGraph = PlanIndexersForType(ctx, updatedGraph, type, out wasConverted)`
3. Return updated graph

**Called by:** Shape phase (after HiddenMemberPlanner)

### Private Methods

#### `PlanIndexersForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type, out bool wasConverted)`
**Returns:** `SymbolGraph` - Updated graph

**Algorithm:**
1. Get indexers: `type.Members.Properties.Where(p => p.IsIndexer)`
2. Policy check:
   - If 1 indexer AND `policy.EmitPropertyWhenSingle` → keep as property, return unchanged
   - Otherwise → convert ALL to methods, remove ALL indexer properties
3. Convert to methods:
   - For each indexer: `ToIndexerMethods(ctx, type, indexer, policy.MethodName)`
   - Add methods and remove indexer properties:
     ```csharp
     graph.WithUpdatedType(type.ClrFullName, t =>
         t.WithAddedMethods(synthesizedMethods)
          .WithRemovedProperties(p => p.IsIndexer))
     ```
4. Verify removal (debug check)

#### `ToIndexerMethods(BuildContext ctx, TypeSymbol type, PropertySymbol indexer, string methodName)`
**Returns:** `IEnumerable<MethodSymbol>` - Getter/setter methods

**Creates:**
1. Getter: `T get_Item(TIndex index)`
   - StableId with method signature
   - Reserve name with Renamer: `"get_{methodName}"`
   - Provenance: `IndexerNormalized`
   - EmitScope: `ClassSurface`
2. Setter: `void set_Item(TIndex index, T value)`
   - Append value parameter to index parameters
   - Return type: `System.Void`
   - Reserve name: `"set_{methodName}"`

**Default method name:** `"Item"` → `get_Item`, `set_Item`

---

## Pass 15: FinalIndexersPass

**File:** `FinalIndexersPass.cs`

### Purpose
Final, definitive pass to ensure indexer policy is enforced. Runs at end of Shape phase to ensure no indexer properties leak through.

**Invariant:**
- 0 indexers → nothing
- 1 indexer → keep as property ONLY if `policy.EmitPropertyWhenSingle == true`
- ≥2 indexers → convert ALL to methods, remove ALL indexer properties

### Public API

#### `FinalIndexersPass.Run(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Run(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. For each namespace → type:
   - Get indexers: `type.Members.Properties.Where(p => p.IsIndexer)`
   - Invariant check:
     - 1 indexer + `EmitPropertyWhenSingle` → keep, skip
     - Otherwise → convert to methods
2. For types needing conversion:
   - Emit INFO diagnostic: `DiagnosticCodes.IndexerConflict`
   - Synthesize get/set methods: `ToIndexerMethods(ctx, type, idx, policy.MethodName)`
   - Update type:
     ```csharp
     graph.WithUpdatedType(type.ClrFullName, t =>
         t.WithMembers(t.Members with {
             Methods = t.Members.Methods.AddRange(methods),
             Properties = t.Members.Properties.RemoveAll(p => p.IsIndexer)
         }))
     ```
   - Verify removal (debug check)
3. Return updated graph

**Called by:** Shape phase (last pass, after IndexerPlanner)

### Private Methods

#### `ToIndexerMethods(BuildContext ctx, TypeSymbol type, PropertySymbol indexer, string methodName)`
**Returns:** `IEnumerable<MethodSymbol>` - Getter/setter methods

**Same as IndexerPlanner.ToIndexerMethods, but:**
- Sets `TsEmitName = ""` (will be set by NameReservation pass later)
- Doesn't reserve names immediately (centralized in later pass)

---

## Pass 16: StaticSideAnalyzer

**File:** `StaticSideAnalyzer.cs`

### Purpose
Analyze static-side inheritance issues. Detects when static members conflict with instance members from class hierarchy. TypeScript doesn't allow static side of class to extend static side of base class, causing TS2417 errors.

**Policy-driven:** `ctx.Policy.StaticSide.Action` controls behavior (Analyze, AutoRename, Error)

### Public API

#### `StaticSideAnalyzer.Analyze(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static void Analyze(BuildContext ctx, SymbolGraph graph)`

**What it does:**
- For each class with base type:
  - Collect static members from derived and base
  - Find name conflicts
  - Apply policy action (report/rename/error)
- DOES NOT modify graph (uses Renamer for renames)

**Called by:** Shape phase (after FinalIndexersPass)

### Private Methods

#### `AnalyzeClass(BuildContext ctx, SymbolGraph graph, TypeSymbol derivedClass, StaticSideAction action)`
**Returns:** `(int issues, int renamed)` - Counts of issues found and members renamed

**Algorithm:**
1. Find base class: `FindBaseClass(graph, derivedClass)`
2. Collect static members:
   - `derivedStatics = derivedClass.Members.Methods/Properties/Fields.Where(m => m.IsStatic)`
   - `baseStatics = baseClass.Members.Methods/Properties/Fields.Where(m => m.IsStatic)`
3. Get member names: `GetStaticMemberNames(derivedStatics/baseStatics)`
4. Find conflicts: `conflicts = derivedStaticNames.Intersect(baseStaticNames)`
5. For each conflict:
   - Build diagnostic message
   - Apply action:
     - `Error` → `ctx.Diagnostics.Error(DiagnosticCodes.StaticSideInheritanceIssue, ...)`
     - `AutoRename` → `RenameConflictingStatic(ctx, derivedClass, derivedStatics, conflictName)`
     - `Analyze` → `ctx.Diagnostics.Warning(...)`
6. Return (issue count, renamed count)

#### `RenameConflictingStatic(BuildContext ctx, TypeSymbol derivedClass, List<object> derivedStatics, string conflictName)`
**Returns:** `int` - Count of renamed members

**Algorithm:**
1. Find conflicting members: `derivedStatics.Where(m => GetMemberName(m) == conflictName)`
2. For each member:
   - Reserve renamed version through Renamer:
     ```csharp
     ctx.Renamer.ReserveMemberName(
         member.StableId,
         $"{member.ClrName}_static",
         typeScope,
         "StaticSideNameCollision",
         isStatic: true)
     ```
3. Return count

**Naming:** Adds `_static` suffix to conflicting static members

---

## Pass 17: ConstraintCloser

**File:** `ConstraintCloser.cs`

### Purpose
Close generic constraints for TypeScript. Computes final constraint sets by:
1. Resolving raw `System.Type` constraints into `TypeReference`s
2. Validating constraint compatibility
3. Applying merge strategy for multiple constraints

**Policy-driven:** `ctx.Policy.Constraints.MergeStrategy` controls how multiple constraints combine

### Public API

#### `ConstraintCloser.Close(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph Close(BuildContext ctx, SymbolGraph graph)`

**Algorithm:**
1. Resolve constraints: `updatedGraph = ResolveAllConstraints(ctx, graph)`
2. For each type:
   - Close type-level generic parameters: `CloseConstraints(ctx, gp)`
   - Close method-level generic parameters: `CloseConstraints(ctx, gp)`
3. Return updated graph

**Called by:** Shape phase (final pass, after StaticSideAnalyzer)

### Private Methods

#### `ResolveAllConstraints(BuildContext ctx, SymbolGraph graph)`
**Returns:** `SymbolGraph` - Graph with constraints resolved from raw types to TypeReferences

**Algorithm:**
1. Create `TypeReferenceFactory` (memoized with cycle detection)
2. For each namespace → type:
   - For each type-level generic parameter with `RawConstraintTypes`:
     - For each raw type: `resolved = typeFactory.Create(rawType)`
     - Update generic parameter: `gp with { Constraints = resolvedConstraints }`
   - For each method:
     - For each method-level generic parameter with `RawConstraintTypes`:
       - Same resolution process
     - Update method if changed
   - Update type if changed: `graph.WithUpdatedType(type.StableId, t => t with { GenericParameters = ..., Members = ... })`
3. Return updated graph

**Key:** Uses memoized factory to prevent infinite loops on recursive constraints

#### `CloseConstraints(BuildContext ctx, GenericParameterSymbol gp)`
**Purpose:** Validate and log constraint handling

**Algorithm:**
1. If no constraints → return
2. Check merge strategy:
   - `Intersection` → TypeScript uses `T extends A & B & C` automatically
   - `Union` → Not supported in TS, emit warning
   - `PreferLeft` → Log strategy, would need to mutate constraints
3. Validate constraints: `ValidateConstraints(ctx, gp)`

#### `ValidateConstraints(BuildContext ctx, GenericParameterSymbol gp)`
**Purpose:** Check for incompatible/unrepresentable constraints

**Checks:**
1. Both `struct` and `class` constraints → warning
2. Circular constraints (T : U, U : T) → rely on C# compiler
3. Unrepresentable types (pointers, byrefs) → warning

#### `IsTypeScriptRepresentable(TypeReference typeRef)`
**Returns:** `bool` - True if type can be represented in TypeScript

**Returns false for:**
- `PointerTypeReference` (loses semantics)
- `ByRefTypeReference` (loses semantics)

---

## Pass Order and Dependencies

**Critical:** Shape passes MUST run in this exact order:

1. **GlobalInterfaceIndex** - Build global interface index (required by all later passes)
2. **InterfaceDeclIndex** - Build declared-only index (required by InterfaceResolver)
3. **InterfaceInliner** - Flatten interface hierarchies BEFORE conformance checking
4. **StructuralConformance** - Analyze conformance and synthesize ViewOnly members
5. **ExplicitImplSynthesizer** - Synthesize missing EII members
6. **InterfaceResolver** - Resolve declaring interfaces (used by synthesis passes)
7. **DiamondResolver** - Detect/resolve diamond conflicts AFTER all synthesis
8. **BaseOverloadAdder** - Add base overloads AFTER diamond resolution
9. **OverloadReturnConflictResolver** - Detect return-type conflicts AFTER overload addition
10. **MemberDeduplicator** - Remove duplicates BEFORE view planning
11. **ViewPlanner** - Plan explicit views AFTER all ViewOnly members synthesized
12. **ClassSurfaceDeduplicator** - Deduplicate by emitted name AFTER view planning
13. **HiddenMemberPlanner** - Plan 'new' hidden members
14. **IndexerPlanner** - Convert indexers to methods
15. **FinalIndexersPass** - Final indexer policy enforcement
16. **StaticSideAnalyzer** - Analyze static-side conflicts
17. **ConstraintCloser** - Close generic constraints (final pass)

**Dependencies:**
- Passes 4-5 (synthesis) depend on pass 3 (inlining) - must work with flattened interfaces
- Pass 6 (resolver) depends on passes 1-2 (indexes) - needs global interface info
- Pass 11 (view planner) depends on passes 4-5 (synthesis) - needs ViewOnly members
- Pass 12 (surface dedup) depends on pass 11 (view planner) - can safely demote to ViewOnly
- Pass 15 (final indexers) depends on pass 14 (indexer planner) - ensures no leaks

---

## Key Transformations Summary

### Interface Flattening
**Before:**
```typescript
interface IEnumerable<T> extends IEnumerable {
    // Only GetEnumerator(): IEnumerator<T>
}
```
**After:**
```typescript
interface IEnumerable_1<T> {
    // Both members inlined:
    GetEnumerator(): IEnumerator_1<T>
    GetEnumerator(): IEnumerator
}
```

### ViewOnly Synthesis
**Before:**
```csharp
class Decimal : IConvertible {
    // Missing IConvertible.ToBoolean (explicit impl in C#)
}
```
**After:**
```csharp
class Decimal {
    // ClassSurface members...

    // ViewOnly members (accessible via As_IConvertible):
    [ViewOnly] ToBoolean(provider): boolean  // Synthesized
}
```

### Explicit Views
**Before:**
```csharp
class Decimal {
    [ViewOnly] ToBoolean(provider): boolean
    [ViewOnly] ToByte(provider): byte
}
```
**After:**
```typescript
class Decimal {
    // ViewOnly members accessible via view:
    As_IConvertible: {
        toBoolean(provider): boolean
        toByte(provider): byte
    }
}
```

### Base Overload Addition
**Before:**
```typescript
class Derived extends Base {
    method(x: int): void  // Only overrides one signature
}
```
**After:**
```typescript
class Derived extends Base {
    method(x: int): void       // Derived override
    method(s: string): void    // Base overload added
}
```

### Indexer Conversion
**Before:**
```csharp
class Array<T> {
    [indexer] this[int index]: T
    [indexer] this[Range range]: T[]
}
```
**After:**
```typescript
class Array_1<T> {
    get_Item(index: int): T
    set_Item(index: int, value: T): void
    get_Item(range: Range): T[]
    // All indexer properties removed
}
```

### Class Surface Deduplication
**Before:**
```typescript
class Enumerator<T> {
    current: object      // IEnumerator.Current
    current: T          // IEnumerator<T>.Current
}
```
**After:**
```typescript
class Enumerator_1<T> {
    current: T          // Winner (generic over object)

    As_IEnumerator: {
        current: object  // Demoted to ViewOnly
    }
}
```

---

## Output

**Shape phase produces:**
- Flattened interfaces (no `extends`)
- ViewOnly members synthesized for all non-conforming interfaces
- Explicit views planned (As_IInterface properties)
- Base overloads added for TypeScript compatibility
- Indexers converted to methods (policy-dependent)
- Diamond conflicts detected
- Return-type conflicts detected
- Static-side issues analyzed/renamed
- Generic constraints resolved and validated
- Clean graph ready for Renaming and Emit phases

**Next Phase:** Renaming (reserve all member names, apply transformations)
