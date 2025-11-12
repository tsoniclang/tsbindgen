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

### FIX D: Generic Parameter Substitution (InterfaceInliner)

**Purpose:** When flattening interface hierarchies, generic parameters must be substituted correctly. If a derived interface extends a generic base with specific type arguments, those type arguments must replace the base's generic parameters in all inherited members.

**Problem Without FIX D:**
```csharp
// C# definition:
interface IBase<T> { T GetValue(); }
interface IDerived : IBase<string> { }

// WITHOUT FIX D - Generic parameter not substituted:
interface IDerived {
  GetValue(): T;  // ERROR: T is orphaned (not declared in IDerived)
}

// WITH FIX D - Correctly substituted:
interface IDerived {
  GetValue(): string;  // CORRECT
}
```

**What FIX D Handles:**
1. **Direct generic substitution:** `ICollection<T>` → `ICollection<string>`
2. **Nested generic substitution:** `ICollection<KeyValuePair<TKey, TValue>>`
3. **Chained generic substitution:** Grandparent generics substituted through parent
4. **Method-level generic protection:** Ensures method's own type parameters aren't substituted

**Integration in InlineInterface():**
```csharp
// Lines 83-87: Build substitution map for each base interface
var substitutionMap = BuildSubstitutionMapForInterface(baseIface, baseIfaceRef);

// Compose with parent substitution (for chained generics)
substitutionMap = ComposeSubstitutions(parentSubstitution, substitutionMap);

// Lines 90-92: Apply substitution to all inherited members
var substitutedMethods = SubstituteMethodMembers(baseIface.Members.Methods, substitutionMap);
var substitutedProperties = SubstitutePropertyMembers(baseIface.Members.Properties, substitutionMap);
var substitutedEvents = SubstituteEventMembers(baseIface.Members.Events, substitutionMap);
```

---

#### `BuildSubstitutionMapForInterface(TypeSymbol baseIface, TypeReference baseIfaceRef)`

**Signature:** `private static Dictionary<string, TypeReference> BuildSubstitutionMapForInterface(TypeSymbol baseIface, TypeReference baseIfaceRef)`

**Lines:** 232-261

**Purpose:** Builds a dictionary mapping interface generic parameter names to actual type arguments from the reference.

**Example:**
```csharp
// Input:
baseIface = ICollection<T> (TypeSymbol with generic param "T")
baseIfaceRef = ICollection<KeyValuePair<TKey, TValue>> (TypeReference with type arg)

// Output:
{ "T" -> KeyValuePair<TKey, TValue> }
```

**Algorithm:**
1. **Check type reference kind:**
   - If not `NamedTypeReference`: return empty map (can't have type arguments)

2. **Check for type arguments:**
   - If `TypeArguments.Count == 0`: return empty map (non-generic interface)

3. **Validate arity match:**
   - If `GenericParameters.Length != TypeArguments.Count`: return empty map (defensive)

4. **Build mapping:**
   - For `i` in `0..GenericParameters.Length`:
     - `param = baseIface.GenericParameters[i]`
     - `arg = namedRef.TypeArguments[i]`
     - `map[param.Name] = arg`

5. **Return map**

**Why needed:** Interface references carry actual type arguments (e.g., `string`), but interface symbols use generic parameters (e.g., `T`). This method creates the mapping to substitute `T` with `string` in all inherited members.

**Handles non-generic interfaces:** Returns empty map, no substitution needed.

---

#### `ComposeSubstitutions(Dictionary<string, TypeReference> parent, Dictionary<string, TypeReference> current)`

**Signature:** `private static Dictionary<string, TypeReference> ComposeSubstitutions(Dictionary<string, TypeReference> parent, Dictionary<string, TypeReference> current)`

**Lines:** 267-289

**Purpose:** Composes two substitution maps for chained generics (grandparent → parent → child).

**Example:**
```csharp
// Scenario: IDictionary<TKey, TValue> : ICollection<KeyValuePair<TKey, TValue>> : IEnumerable<KeyValuePair<TKey, TValue>>
// When processing IEnumerable (grandparent):

parent = { "T" -> KeyValuePair<TKey, TValue> }  // From ICollection
current = { "T" -> T }  // IEnumerable's own generic param

// Composed result:
{ "T" -> KeyValuePair<TKey, TValue> }
```

**Algorithm:**
1. **Check for empty parent:**
   - If `parent.Count == 0`: return `current` (no composition needed)

2. **Apply parent substitution to current values:**
   - For each `(key, value)` in `current`:
     - `composed[key] = InterfaceMemberSubstitution.SubstituteTypeReference(value, parent)`
     - This recursively substitutes any generic parameters in the value

3. **Add parent-only mappings:**
   - For each `(key, value)` in `parent`:
     - If `key` not in `composed`: `composed[key] = value`

4. **Return composed map**

**Why needed:** When flattening deep interface hierarchies, generic substitutions must be transitively applied. If grandparent uses `T` but parent substitutes it with `KeyValuePair<K,V>`, the grandparent's members must get `KeyValuePair<K,V>`, not `T`.

**Example - Multi-level chain:**
```csharp
// IEnumerable<T> defines: T Current { get; }
// ICollection<T> : IEnumerable<T>
// IList<string> : ICollection<string>

// When inlining IList<string>:
// - Parent map: { "T" -> string }
// - Current map: { "T" -> T } (from IEnumerable)
// - Composed: { "T" -> string }
// Result: IList<string> gets "string Current { get; }" (not "T Current")
```

---

#### `SubstituteMethodMembers(ImmutableArray<MethodSymbol> methods, Dictionary<string, TypeReference> substitutionMap)`

**Signature:** `private static IReadOnlyList<MethodSymbol> SubstituteMethodMembers(ImmutableArray<MethodSymbol> methods, Dictionary<string, TypeReference> substitutionMap)`

**Lines:** 295-344

**Purpose:** Apply generic parameter substitution to method members from base interfaces.

**CRITICAL:** Only substitutes **type-level** generic parameters. Does NOT substitute **method-level** generic parameters.

**Example - Type-level substitution:**
```csharp
// Base interface:
interface IBase<T> {
  T GetValue();  // T is type-level generic
}

// Derived interface:
interface IDerived : IBase<string> { }

// Substitution map: { "T" -> string }
// Result: string GetValue();
```

**Example - Method-level protection:**
```csharp
// Base interface:
interface IBase<T> {
  U Transform<U>(T input);  // T is type-level, U is method-level
}

// Derived interface:
interface IDerived : IBase<string> { }

// Substitution map: { "T" -> string }
// Result: U Transform<U>(string input);  // T substituted, U preserved
```

**Algorithm:**
1. **Check for empty map:**
   - If `substitutionMap.Count == 0`: return methods unchanged

2. **For each method:**

   a. **Build method-level generic parameter exclusion set:**
   ```csharp
   var methodLevelParams = new HashSet<string>(
       method.GenericParameters.Select(gp => gp.Name));
   ```

   b. **Filter substitution map to exclude method-level generics:**
   ```csharp
   var filteredMap = substitutionMap
       .Where(kv => !methodLevelParams.Contains(kv.Key))
       .ToDictionary(kv => kv.Key, kv => kv.Value);
   ```

   c. **If filtered map is empty:** Add method unchanged (all params are method-level)

   d. **Substitute return type:**
   ```csharp
   var newReturnType = InterfaceMemberSubstitution.SubstituteTypeReference(
       method.ReturnType, filteredMap);
   ```

   e. **Substitute parameters:**
   ```csharp
   var newParameters = method.Parameters
       .Select(p => p with { Type = SubstituteTypeReference(p.Type, filteredMap) })
       .ToImmutableArray();
   ```

   f. **Create substituted method symbol:**
   ```csharp
   substitutedMethods.Add(method with {
       ReturnType = newReturnType,
       Parameters = newParameters
   });
   ```

3. **Return substituted methods**

**Why method-level protection is critical:**
```csharp
// WITHOUT protection:
interface IBase<T> {
  T Parse<T>(string input);  // Method declares own T
}
interface IDerived : IBase<int> { }

// WRONG (without protection):
int Parse<int>(string input);  // ERROR: <int> conflicts with method's generic param

// CORRECT (with protection):
int Parse<T>(string input);  // Method's T preserved, return type T substituted
```

**Delegates to:** `InterfaceMemberSubstitution.SubstituteTypeReference()` for recursive substitution

---

#### `SubstitutePropertyMembers(ImmutableArray<PropertySymbol> properties, Dictionary<string, TypeReference> substitutionMap)`

**Signature:** `private static IReadOnlyList<PropertySymbol> SubstitutePropertyMembers(ImmutableArray<PropertySymbol> properties, Dictionary<string, TypeReference> substitutionMap)`

**Lines:** 349-381

**Purpose:** Apply generic parameter substitution to property members from base interfaces.

**Example:**
```csharp
// Base interface:
interface IBase<T> {
  T Value { get; set; }
  T this[int index] { get; }  // Indexer
}

// Derived interface:
interface IDerived : IBase<string> { }

// Substitution map: { "T" -> string }
// Result:
// - string Value { get; set; }
// - string this[int index] { get; }
```

**Algorithm:**
1. **Check for empty map:**
   - If `substitutionMap.Count == 0`: return properties unchanged

2. **For each property:**

   a. **Substitute property type:**
   ```csharp
   var newPropertyType = InterfaceMemberSubstitution.SubstituteTypeReference(
       prop.PropertyType, substitutionMap);
   ```

   b. **Substitute index parameters (for indexers):**
   ```csharp
   var newIndexParameters = prop.IndexParameters
       .Select(p => p with {
           Type = SubstituteTypeReference(p.Type, substitutionMap)
       })
       .ToImmutableArray();
   ```

   c. **Create substituted property symbol:**
   ```csharp
   substitutedProperties.Add(prop with {
       PropertyType = newPropertyType,
       IndexParameters = newIndexParameters
   });
   ```

3. **Return substituted properties**

**Handles indexers:** Both the property type (return value) AND index parameters are substituted.

**Example - Indexer with generic parameter:**
```csharp
// Base interface:
interface IBase<T> {
  T this[T key] { get; }  // Both return type and parameter use T
}

// Derived interface:
interface IDerived : IBase<string> { }

// Substitution map: { "T" -> string }
// Result: string this[string key] { get; }
```

**Simpler than methods:** Properties don't have property-level generic parameters, so no exclusion logic needed.

---

#### `SubstituteEventMembers(ImmutableArray<EventSymbol> events, Dictionary<string, TypeReference> substitutionMap)`

**Signature:** `private static IReadOnlyList<EventSymbol> SubstituteEventMembers(ImmutableArray<EventSymbol> events, Dictionary<string, TypeReference> substitutionMap)`

**Lines:** 386-409

**Purpose:** Apply generic parameter substitution to event members from base interfaces.

**Example:**
```csharp
// Base interface:
interface IBase<T> {
  event EventHandler<T> ValueChanged;
}

// Derived interface:
interface IDerived : IBase<string> { }

// Substitution map: { "T" -> string }
// Result: event EventHandler<string> ValueChanged;
```

**Algorithm:**
1. **Check for empty map:**
   - If `substitutionMap.Count == 0`: return events unchanged

2. **For each event:**

   a. **Substitute event handler type:**
   ```csharp
   var newHandlerType = InterfaceMemberSubstitution.SubstituteTypeReference(
       evt.EventHandlerType, substitutionMap);
   ```

   b. **Create substituted event symbol:**
   ```csharp
   substitutedEvents.Add(evt with {
       EventHandlerType = newHandlerType
   });
   ```

3. **Return substituted events**

**Simplest substitution:** Events only have one type to substitute (handler type).

**Example - Generic delegate:**
```csharp
// Base interface:
interface IBase<T> {
  event Action<T, int> SomeEvent;  // First arg uses T, second is concrete
}

// Derived interface:
interface IDerived : IBase<string> { }

// Substitution map: { "T" -> string }
// Result: event Action<string, int> SomeEvent;
```

**Recursive substitution:** `SubstituteTypeReference()` handles nested generics in delegate types automatically.

---

### FIX D Complete Example

**Scenario:** Three-level interface hierarchy with generic substitution

```csharp
// C# BCL interfaces:
interface IEnumerable<T> {
  IEnumerator<T> GetEnumerator();
}

interface ICollection<T> : IEnumerable<T> {
  int Count { get; }
  void Add(T item);
}

interface IList<T> : ICollection<T> {
  T this[int index] { get; set; }
}

// Concrete implementation:
class MyStringList : IList<string> { }
```

**Inlining IList<string> with FIX D:**

**Step 1: Process ICollection<string> (parent)**
- Build substitution map: `{ "T" -> string }`
- Substitute ICollection members:
  - `int Count { get; }` (no generics, unchanged)
  - `void Add(T item)` → `void Add(string item)`
- Queue IEnumerable<string> (grandparent) with substitution map

**Step 2: Process IEnumerable<string> (grandparent)**
- Parent substitution: `{ "T" -> string }`
- Build current substitution: `{ "T" -> T }` (IEnumerable's param)
- Compose substitutions: `{ "T" -> string }` (parent overrides current)
- Substitute IEnumerable members:
  - `IEnumerator<T> GetEnumerator()` → `IEnumerator<string> GetEnumerator()`

**Step 3: Collect all members in IList<string>**
- Own members: `string this[int index] { get; set; }`
- From ICollection: `int Count { get; }`, `void Add(string item)`
- From IEnumerable: `IEnumerator<string> GetEnumerator()`

**Step 4: Deduplicate and finalize**
- Remove duplicates by signature
- Clear `Interfaces` array (no more extends)

**Result:**
```typescript
interface IList_1<T> {
  // Own members:
  [index: int]: string;

  // From ICollection (substituted):
  readonly Count: int;
  Add(item: string): void;

  // From IEnumerable (substituted):
  GetEnumerator(): IEnumerator_1<string>;
}
```

**Without FIX D:** All inherited members would have orphaned `T` parameters, causing TypeScript errors.

---

### Integration Notes

**Called by:** `InterfaceInliner.InlineInterface()` during BFS traversal of interface hierarchy

**Call sequence:**
1. For each base interface reference in queue:
   - `BuildSubstitutionMapForInterface()` - Create param→arg mapping
   - `ComposeSubstitutions()` - Compose with parent substitution
   - `SubstituteMethodMembers()` - Substitute method signatures
   - `SubstitutePropertyMembers()` - Substitute property types
   - `SubstituteEventMembers()` - Substitute event handler types
   - Add substituted members to collection
   - Queue grandparents with composed substitution map

**Dependencies:**
- `InterfaceMemberSubstitution.SubstituteTypeReference()` - Recursive type reference substitution
- Uses immutable records (`with` expressions) for all transformations

**Related to ClassPrinter FIX D:** ClassPrinter has similar substitution logic for class members that come from interfaces/base classes. InterfaceInliner handles interface→interface inheritance, ClassPrinter handles interface/base→class inheritance.

**Impact:** Eliminates orphaned generic parameter errors in flattened interfaces. Essential for deep generic hierarchies like `IDictionary<K,V> : ICollection<KeyValuePair<K,V>> : IEnumerable<KeyValuePair<K,V>>`.

---

## Pass 4.5: InternalInterfaceFilter

**File:** `InternalInterfaceFilter.cs`

### Purpose
Filter internal BCL interfaces from type interface lists. Internal interfaces are BCL implementation details that aren't publicly accessible but appear in reflection metadata. Removing them prevents TypeScript errors for interfaces that aren't meant for public consumption.

**Why needed?** BCL types often implement internal interfaces like `IValueTupleInternal`, `IDebuggerDisplay`, or `ISimdVector<TSelf, T>` that exist in metadata but aren't accessible to user code. These cause TS2304 errors ("Cannot find name") when emitted.

### Public API

#### `InternalInterfaceFilter.FilterGraph(BuildContext ctx, SymbolGraph graph)`
**Signature:** `public static SymbolGraph FilterGraph(BuildContext ctx, SymbolGraph graph)`

**What it does:**
- Iterates through all types in all namespaces
- For each type: calls `FilterInterfaces(ctx, type)` to remove internal interfaces
- Returns new `SymbolGraph` with filtered interface lists
- Logs total count of removed interfaces

**Algorithm:**
1. For each namespace in `graph.Namespaces`:
   - For each type in `ns.Types`:
     - Count before: `beforeCount = type.Interfaces.Length`
     - Filter: `filtered = FilterInterfaces(ctx, type)`
     - Count after: `afterCount = filtered.Interfaces.Length`
     - Track removed: `totalRemoved += (beforeCount - afterCount)`
     - Add filtered type to namespace types list
   - Create new namespace: `ns with { Types = filteredTypes.ToImmutableArray() }`
2. Log summary: `"Removed {totalRemoved} internal interfaces across {namespaceCount} namespaces"`
3. Return new graph: `graph with { Namespaces = filteredNamespaces.ToImmutableArray() }`

**Called by:** Shape phase (early pass, before interface indexes built)

**Impact:** Eliminates TS2304 errors for internal interfaces

#### `InternalInterfaceFilter.FilterInterfaces(BuildContext ctx, TypeSymbol type)`
**Signature:** `public static TypeSymbol FilterInterfaces(BuildContext ctx, TypeSymbol type)`

**What it does:**
- Filters internal interfaces from a single type's interface list
- Returns new TypeSymbol with filtered interfaces (or original if no internal interfaces)

**Algorithm:**
1. Check empty: `if (type.Interfaces.Length == 0) return type`
2. For each interface in `type.Interfaces`:
   - Check: `if (IsInternalInterface(iface))`
     - If internal: increment `removedCount`, log removal
     - If public: add to `filtered` list
3. If nothing removed: `if (removedCount == 0) return type`
4. Return updated type: `type with { Interfaces = filtered.ToImmutableArray() }`

**Logging:** Each removal logged with interface name and declaring type for traceability

### Private Methods

#### `IsInternalInterface(TypeReference typeRef)`
**Returns:** `bool` - True if type reference represents an internal interface

**Algorithm:**
1. Get full CLR name: `fullName = GetFullName(typeRef)` (with namespace and backtick)
2. Check explicit list: `if (ExplicitInternalInterfaces.Contains(fullName)) return true`
3. Get simple name: `name = GetInterfaceName(typeRef)` (without namespace)
4. Check patterns: For each `pattern` in `InternalPatterns`:
   - `if (name.Contains(pattern, StringComparison.Ordinal)) return true`
5. Return false (not internal)

**Two-stage matching:**
- **Explicit list** - Full name matches (e.g., `"System.Runtime.Intrinsics.ISimdVector\`2"`)
- **Pattern matching** - Simple name contains pattern (e.g., `"Internal"` matches `IValueTupleInternal`)

**CRITICAL:** Pattern matching uses simple name ONLY, not full name, to avoid false positives like filtering `System.Runtime` namespace.

#### `GetFullName(TypeReference typeRef)`
**Returns:** `string` - Full CLR name with namespace and backtick arity

**Examples:**
- `NamedTypeReference` → `"System.Collections.Generic.IEnumerable\`1"`
- `NestedTypeReference` → `"System.Collections.Immutable.ImmutableArray\`1+Builder"`

**Handles:**
- `NamedTypeReference named` → `named.FullName`
- `NestedTypeReference nested` → `nested.FullReference.FullName`
- Other types → `""` (not applicable)

#### `GetInterfaceName(TypeReference typeRef)`
**Returns:** `string` - Display name without namespace (for logging and pattern matching)

**Examples:**
- `IValueTupleInternal` (not `System.IValueTupleInternal`)
- `IDebuggerDisplay` (not `System.Diagnostics.IDebuggerDisplay`)

**Handles:**
- `NamedTypeReference named` → `named.Name`
- `NestedTypeReference nested` → `nested.FullReference.Name`
- Other types → `""` (not applicable)

### Pattern Lists

#### InternalPatterns HashSet
**Purpose:** Common patterns in internal interface names

**Patterns:**
```csharp
{
    "Internal",              // IValueTupleInternal, ITupleInternal, IImmutableDictionaryInternal_2
    "Debugger",              // IDebuggerDisplay
    "ParseAndFormatInfo",    // IBinaryIntegerParseAndFormatInfo_1, IBinaryFloatParseAndFormatInfo_1
    "Runtime",               // IRuntimeAlgorithm
    "StateMachineBox",       // IStateMachineBoxAwareAwaiter
    "SecurePooled",          // ISecurePooledObjectUser
    "BuiltInJson",           // IBuiltInJsonTypeInfoResolver
    "DeferredDisposable"     // IDeferredDisposable
}
```

**Comparison:** `StringComparer.Ordinal` for exact substring matching

**Why patterns?** Many internal interfaces follow naming conventions. Pattern matching handles new/undiscovered internal interfaces automatically.

#### ExplicitInternalInterfaces HashSet
**Purpose:** Internal interfaces that don't match simple patterns

**Entries:**
```csharp
{
    "System.Runtime.Intrinsics.ISimdVector`2",         // ISimdVector_2<TSelf, T>
    "System.IUtfChar`1",                               // IUtfChar_1<TSelf>
    "System.Collections.Immutable.IStrongEnumerator`1", // IStrongEnumerator_1<T>
    "System.Collections.Immutable.IStrongEnumerable`2", // IStrongEnumerable_2<TKey, TValue>
    "System.Runtime.CompilerServices.ITaskAwaiter",     // ITaskAwaiter
    "System.Collections.Immutable.IImmutableArray"      // IImmutableArray
}
```

**Format:** Full CLR names with backtick arity (e.g., `` `2 `` for two type parameters)

**Why explicit list?** These interfaces have generic names (e.g., `ITaskAwaiter`) that don't contain obvious internal patterns.

### Integration Point

**Called in:** Shape phase, early pass (before building interface indexes)

**Why early?** Cleaner to filter before indexes built rather than filter during index building or emit.

**Immutable transformation:** Returns new graph with filtered types; original graph unchanged.

### Examples

#### Example 1: Pattern Match

**Before filtering:**
```csharp
// Tuple_8<T1, T2, T3, T4, T5, T6, T7, TRest>
class Tuple_8<...> : IComparable, IStructuralComparable, IStructuralEquatable,
                      IValueTupleInternal, ITupleInternal
{
    // ...
}
```

**After filtering:**
```csharp
// Tuple_8<T1, T2, T3, T4, T5, T6, T7, TRest>
class Tuple_8<...> : IComparable, IStructuralComparable, IStructuralEquatable
{
    // IValueTupleInternal and ITupleInternal removed (matched "Internal" pattern)
}
```

**Impact:** Prevents TS2304 errors for `IValueTupleInternal` and `ITupleInternal`

#### Example 2: Explicit Match

**Before filtering:**
```csharp
// Vector_1<T>
class Vector_1<T> : ISimdVector_2<Vector_1<T>, T>, IEquatable_1<Vector_1<T>>
{
    // ...
}
```

**After filtering:**
```csharp
// Vector_1<T>
class Vector_1<T> : IEquatable_1<Vector_1<T>>
{
    // ISimdVector_2 removed (explicit list match)
}
```

**Impact:** Prevents TS2304 error for `ISimdVector_2<TSelf, T>` (internal SIMD interface)

#### Example 3: Multiple Interfaces

**Before filtering:**
```csharp
// ImmutableArray_1<T>
struct ImmutableArray_1<T> : IEnumerable_1<T>, IEnumerable,
                              IReadOnlyList_1<T>, IReadOnlyCollection_1<T>,
                              IStrongEnumerable_2<ImmutableArray_1<T>, T>,
                              IImmutableArray,
                              IEquatable_1<ImmutableArray_1<T>>
{
    // ...
}
```

**After filtering:**
```csharp
// ImmutableArray_1<T>
struct ImmutableArray_1<T> : IEnumerable_1<T>, IEnumerable,
                              IReadOnlyList_1<T>, IReadOnlyCollection_1<T>,
                              IEquatable_1<ImmutableArray_1<T>>
{
    // IStrongEnumerable_2 and IImmutableArray removed (explicit list)
}
```

**Impact:** Prevents TS2304 errors for 2 internal Immutable Collections interfaces

### Validation Results

**Metrics from BCL validation:**
- Total errors before: 2,991
- Total errors after: 2,916
- **Reduction: -75 errors (-2.5%)**
- TS2304 errors before: 99
- TS2304 errors after: 24
- **TS2304 reduction: -75 errors (-75.8%)**

**Breakdown:**
- 74 internal interface errors eliminated
- 1 unrelated error also fixed

**Affected namespaces:**
- System.Collections.Immutable (heaviest: 13 removals from ImmutableArray_1)
- System.Runtime.Intrinsics
- System.Diagnostics
- System.Numerics
- Others (tuples, async state machines, etc.)

### Design Notes

#### Why Not Filter During Reflection?
**Question:** Why filter in Shape phase instead of reflection?

**Answer:**
- Reflection reads ALL metadata faithfully (CLR truth)
- Shape phase transforms CLR → TypeScript semantics
- Internal interface filtering is a TypeScript-specific concern
- Keeps reflection pure and unaware of TypeScript

#### Why Not Filter During Emit?
**Question:** Why filter before indexes instead of during emit?

**Answer:**
- Cleaner graph for all downstream passes
- Interface indexes don't need to handle filtered interfaces
- Emit logic simpler (doesn't need filtering logic)
- Single place to filter (Shape) instead of everywhere

#### False Positive Prevention
**Question:** Why check patterns on simple name, not full name?

**Original bug:** Checking `"Runtime"` pattern against full names like `"System.Runtime.InteropServices.ISerializable"` caused false positives.

**Fix:** Pattern matching uses simple name ONLY:
- `ISerializable` (simple name) doesn't contain "Runtime" → NOT filtered ✅
- `System.Runtime.InteropServices.ISerializable` (full name) contains "Runtime" → Would be filtered ❌

**Result:** Eliminated 341 false positive errors

#### Extensibility
**Adding new internal interfaces:**
1. If follows pattern (e.g., contains "Internal") → automatically filtered
2. If generic name → add to `ExplicitInternalInterfaces` list with full CLR name

**Example:** New interface `System.Foo.IBarInternal` → automatically filtered by "Internal" pattern

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
