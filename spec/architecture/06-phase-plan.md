# Plan Phase Documentation

## Overview

The **Plan phase** is the final preparation stage before code emission. It builds the cross-namespace dependency graph, validates the entire symbol graph, plans import statements and emission order, and enforces all invariants through the PhaseGate validation system.

**Key Responsibilities:**
- Build import dependency graph between namespaces
- Detect foreign type references and plan imports
- Compute relative import paths for TypeScript modules
- Determine topological emission order
- Audit interface constraint losses
- Validate 50+ correctness rules (PhaseGate)
- Ensure symbol graph integrity before emission

**Input:** Fully-shaped `SymbolGraph` from Shape phase
**Output:** `ImportPlan`, `EmitOrder`, validation reports

---

## File: ImportPlanner.cs

### Purpose
Plans import statements and aliasing for TypeScript declarations. Generates import/export statements based on the dependency graph and handles namespace-to-module mapping with name collision resolution.

### Class: ImportPlanner

Static class with core import planning logic.

#### Method: PlanImports()

```csharp
public static ImportPlan PlanImports(
    BuildContext ctx,
    SymbolGraph graph,
    ImportGraphData importGraph)
```

**How it works:**

1. Creates empty `ImportPlan` with three dictionaries:
   - `NamespaceImports` - maps namespace name to list of import statements
   - `NamespaceExports` - maps namespace name to list of export statements
   - `ImportAliases` - maps namespace name to alias dictionary

2. For each namespace in the graph:
   - Calls `PlanNamespaceImports()` to analyze dependencies
   - Calls `PlanNamespaceExports()` to catalog public types

3. Returns complete `ImportPlan` with all import/export data

**Key data flow:**
- Uses `ImportGraphData.NamespaceDependencies` to find which namespaces each namespace depends on
- Uses `ImportGraphData.CrossNamespaceReferences` to get specific types referenced
- Looks up TypeScript emit names via `ctx.Renamer.GetFinalTypeName()`

#### Method: PlanNamespaceImports()

```csharp
private static void PlanNamespaceImports(
    BuildContext ctx,
    NamespaceSymbol ns,
    SymbolGraph graph,
    ImportGraphData importGraph,
    ImportPlan plan)
```

**Foreign type detection algorithm:**

1. **Get dependencies**: Look up namespace in `importGraph.NamespaceDependencies`
   - If no entry exists, namespace has no cross-namespace dependencies

2. **For each target namespace:**
   - Filter `CrossNamespaceReferences` where source is current namespace and target is dependency
   - Extract list of CLR full names for referenced types
   - Sort by CLR name for deterministic output

3. **Determine import path:**
   - Call `PathPlanner.GetSpecifier(sourceNamespace, targetNamespace)`
   - Returns relative path like `"../System.Collections/internal/index"`

4. **Check for name collisions:**
   - For each referenced type, get TypeScript emit name from Renamer
   - Call `DetermineAlias()` to check if alias is needed
   - Create `TypeImport(TypeName, Alias)` record

5. **Build import statement:**
   - Group all type imports from same target namespace
   - Create `ImportStatement(ImportPath, TargetNamespace, TypeImports)`
   - Add to `plan.NamespaceImports[ns.Name]`

**Alias assignment:**

Aliases are needed when:
- **Name collision detected**: Same TypeScript name already imported from different namespace
- **Policy requires it**: `ctx.Policy.Modules.AlwaysAliasImports == true`

Alias format: `{TypeName}_{TargetNamespaceShortName}`
- Example: `List_Generic` when importing `List` from `System.Collections.Generic`

#### Method: PlanNamespaceExports()

```csharp
private static void PlanNamespaceExports(
    BuildContext ctx,
    NamespaceSymbol ns,
    ImportPlan plan)
```

**How it works:**

1. Iterate through all types in namespace
2. Filter to public types only (`Accessibility.Public`)
3. For each public type:
   - Get final TypeScript name via `ctx.Renamer.GetFinalTypeName()`
   - Determine export kind based on type kind (class/interface/enum/type)
   - Create `ExportStatement(ExportName, ExportKind)`
4. Add all exports to `plan.NamespaceExports[ns.Name]`

**Export kind mapping:**
- `Class` → `ExportKind.Class`
- `Interface` → `ExportKind.Interface`
- `Struct` → `ExportKind.Interface` (structs emit as TypeScript interfaces)
- `Enum` → `ExportKind.Enum`
- `Delegate` → `ExportKind.Type` (delegates emit as type aliases)

#### Method: GetTypeScriptNameForExternalType()

```csharp
private static string GetTypeScriptNameForExternalType(string clrFullName)
```

**Lines:** 229-247

**Purpose:** Convert CLR full name to TypeScript emit name for external types (types from other namespaces not in local graph).

**Why needed:** When planning imports for cross-namespace references, we need to determine the TypeScript name that will be emitted for types we haven't processed yet. This method applies the same naming conventions as TypeNameResolver but works purely from CLR names.

**Algorithm:**

1. **Extract simple name from full CLR name:**
   ```csharp
   var simpleName = clrFullName.Contains('.')
       ? clrFullName.Substring(clrFullName.LastIndexOf('.') + 1)
       : clrFullName;
   ```
   - Example: `"System.Collections.Generic.IEnumerable\`1"` → `"IEnumerable\`1"`

2. **Sanitize backtick to underscore (generic arity marker):**
   ```csharp
   var sanitized = simpleName.Replace('\`', '_');
   ```
   - Example: `"IEnumerable\`1"` → `"IEnumerable_1"`

3. **Handle nested types (replace + with $):**
   ```csharp
   sanitized = sanitized.Replace('+', '$');
   ```
   - Example: `"Dictionary\`2+Enumerator"` → `"Dictionary_2$Enumerator"`

4. **Check TypeScript reserved words:**
   ```csharp
   var result = TypeScriptReservedWords.Sanitize(sanitized);
   return result.Sanitized;
   ```
   - Example: `"Type"` → `"Type_"`, `"Object"` → `"Object_"`

**Examples:**

```csharp
// Generic interface:
GetTypeScriptNameForExternalType("System.Collections.Generic.IEnumerable`1")
→ "IEnumerable_1"

// Nested type:
GetTypeScriptNameForExternalType("System.Collections.Generic.Dictionary`2+Enumerator")
→ "Dictionary_2$Enumerator"

// Reserved word:
GetTypeScriptNameForExternalType("System.Type")
→ "Type_"

// Multi-arity generic:
GetTypeScriptNameForExternalType("System.Func`3")
→ "Func_3"
```

**Used in:** `PlanNamespaceImports()` at line 101 when type is not found in local graph

**Integration:**

```csharp
// In PlanNamespaceImports():
if (graph.TryGetType(clrName, out var typeSymbol) && typeSymbol != null)
{
    // Type is in local graph - use Renamer's final name
    tsName = ctx.Renamer.GetFinalTypeName(typeSymbol);
}
else
{
    // Type is external - construct TS name from CLR name
    tsName = GetTypeScriptNameForExternalType(clrName);
    ctx.Log("ImportPlanner", $"External type {clrName} → {tsName}");
}
```

**Critical for:** Cross-namespace generic type imports like `IEnumerable_1`, `Func_2`, etc.

**Pre-emit guard:** After getting `tsName`, the code checks for assembly-qualified garbage to prevent regressions of the import garbage bug (commit 70d21db). If `tsName` contains `[`, `Culture=`, or `PublicKeyToken=`, an error is raised and the import is skipped.

**Consistency:** Applies same transformations as `TypeNameResolver` for external types to ensure import names match emitted names across namespaces.

### Class: ImportPlan

Data structure containing all import/export information.

**Properties:**
- `NamespaceImports: Dictionary<string, List<ImportStatement>>`
  - Maps namespace name to its import statements
- `NamespaceExports: Dictionary<string, List<ExportStatement>>`
  - Maps namespace name to its export statements
- `ImportAliases: Dictionary<string, Dictionary<string, string>>`
  - Maps namespace name to alias dictionary (original name → alias)

**Method:** `GetImportsFor(string namespaceName)`
- Returns import statements for specific namespace
- Returns empty list if namespace has no imports
- Convenience method for Emit phase

### Records: ImportStatement, TypeImport, ExportStatement

**ImportStatement:**
```csharp
record ImportStatement(
    string ImportPath,         // "../System/internal/index"
    string TargetNamespace,    // "System"
    List<TypeImport> TypeImports // [List, Dictionary, ...]
)
```

**TypeImport:**
```csharp
record TypeImport(
    string TypeName,  // "List_1"
    string? Alias     // "List_Collections" or null
)
```

**ExportStatement:**
```csharp
record ExportStatement(
    string ExportName,    // "List_1"
    ExportKind ExportKind // Class/Interface/Enum/Type
)
```

---

## File: ImportGraph.cs

### Purpose
Builds cross-namespace dependency graph for import planning. Analyzes type references throughout the symbol graph to determine which namespaces need to import from which other namespaces.

### Class: ImportGraph

Static class with graph building logic.

#### Method: Build()

```csharp
public static ImportGraphData Build(BuildContext ctx, SymbolGraph graph)
```

**How it works:**

1. Create empty `ImportGraphData` structure with:
   - `NamespaceDependencies` - maps namespace → set of dependent namespaces
   - `NamespaceTypeIndex` - maps namespace → set of type full names (set-based, legacy)
   - `ClrFullNameToNamespace` - **NEW:** fast O(1) lookup: CLR name → namespace
   - `CrossNamespaceReferences` - list of all foreign type references
   - `UnresolvedClrKeys` - **NEW:** types not found in graph (Fix E infrastructure)
   - `UnresolvedToAssembly` - **NEW:** unresolved type → assembly mapping (Fix E)

2. **Build namespace type index first**:
   - Call `BuildNamespaceTypeIndex()` to catalog all public types
   - Creates TWO lookups:
     - Set-based: `NamespaceTypeIndex[ns] = {type1, type2, ...}` (legacy)
     - Map-based: `ClrFullNameToNamespace["Type`1"] = "Namespace"` (fast lookup)

3. **Analyze dependencies**:
   - For each namespace, call `AnalyzeNamespaceDependencies()`
   - Recursively scans all type references in signatures
   - **NEW:** Tracks unresolved types in `UnresolvedClrKeys`

4. Return complete `ImportGraphData`

**Key improvements in jumanji7:**
- Fast O(1) namespace lookup via `ClrFullNameToNamespace` map
- Unresolved type tracking for cross-assembly resolution (Fix E Phase 1)
- Constructor parameter analysis (missing constructor imports fix)

#### Method: BuildNamespaceTypeIndex()

```csharp
private static void BuildNamespaceTypeIndex(
    BuildContext ctx,
    SymbolGraph graph,
    ImportGraphData graphData)
```

**Algorithm:**

1. For each namespace in graph:
   - Get all public types (`Accessibility.Public`)
   - Extract CLR full names (in backtick form, e.g., `"IEnumerable\`1"`)
   - Add to **both** indexes:
     - `graphData.NamespaceTypeIndex[ns.Name].Add(type.ClrFullName)` (set-based, legacy)
     - `graphData.ClrFullNameToNamespace[type.ClrFullName] = ns.Name` (map-based, **NEW**)

**Why only public types:**
- Internal types won't be emitted, so shouldn't be in import index
- Prevents imports of non-existent declarations
- Enforces access boundaries at namespace level

**Dual indexing (jumanji7):**
- **Set-based** (`NamespaceTypeIndex`): Legacy, used for set operations
- **Map-based** (`ClrFullNameToNamespace`): **NEW**, enables O(1) lookups in `FindNamespaceForType()`
  - Before: O(n) iteration through all namespaces
  - After: O(1) dictionary lookup
  - Critical for BCL generation with 4,000+ types

**CLR full name format:**
- **Generic types**: Use backtick arity notation (e.g., `"IEnumerable\`1"`, `"Dictionary\`2"`)
- **Non-generic types**: Simple full name (e.g., `"System.String"`, `"System.Exception"`)
- **Nested types**: Use `+` separator (e.g., `"ImmutableArray\`1+Builder"`)
- This matches `TypeSymbol.ClrFullName` and `NamedTypeReference.FullName` format

#### Method: AnalyzeNamespaceDependencies()

```csharp
private static void AnalyzeNamespaceDependencies(
    BuildContext ctx,
    SymbolGraph graph,
    NamespaceSymbol ns,
    ImportGraphData graphData)
```

**Comprehensive scanning algorithm:**

For each **public type** in namespace:

1. **Base class analysis:**
   - If type has base class, call `CollectTypeReferences(type.BaseType)`
   - Recursively finds ALL referenced types (including generic arguments)
   - For each foreign type, add to dependencies and create `CrossNamespaceReference`
   - Reference kind: `ReferenceKind.BaseClass`

2. **Interface analysis:**
   - For each implemented interface, call `CollectTypeReferences()`
   - Same recursive collection of nested type references
   - Reference kind: `ReferenceKind.Interface`

3. **Generic constraint analysis:**
   - For each type generic parameter with constraints
   - Call `CollectTypeReferences()` on each constraint
   - Reference kind: `ReferenceKind.GenericConstraint`

4. **Member analysis:**
   - Call `AnalyzeMemberDependencies()` to scan all members
   - Analyzes methods, properties, fields, events

**Result:**
- `dependencies` set contains all foreign namespace names
- `graphData.CrossNamespaceReferences` has detailed reference records
- Added to `graphData.NamespaceDependencies[ns.Name]`

#### Method: AnalyzeMemberDependencies()

```csharp
private static void AnalyzeMemberDependencies(
    BuildContext ctx,
    SymbolGraph graph,
    ImportGraphData graphData,
    NamespaceSymbol ns,
    TypeSymbol type,
    HashSet<string> dependencies)
```

**Scans all member signatures:**

1. **Methods:**
   - Return type: `CollectTypeReferences(method.ReturnType)`
     - Reference kind: `ReferenceKind.MethodReturn`
   - Parameters: `CollectTypeReferences(param.Type)` for each parameter
     - Reference kind: `ReferenceKind.MethodParameter`
   - Generic constraints: For method-level type parameters
     - Reference kind: `ReferenceKind.GenericConstraint`

2. **Constructors** (**NEW in jumanji7**):
   - **Parameters**: `CollectTypeReferences(param.Type)` for each parameter
     - Reference kind: `ReferenceKind.ConstructorParameter` (**NEW enum value**)
   - **Why added**: Major bug fix - constructors create objects, parameter types must be imported
   - **Impact**: -157 errors in BCL validation (missing constructor parameter imports)
   - **Example**: `new List<string>(IEnumerable<string> collection)` needs both `List\`1` and `IEnumerable\`1` imports

3. **Properties:**
   - Property type: `CollectTypeReferences(property.PropertyType)`
     - Reference kind: `ReferenceKind.PropertyType`
   - Index parameters: For indexers, scan parameter types
     - Adds to dependencies but no detailed reference (indexers omitted from output)

4. **Fields:**
   - Field type: `CollectTypeReferences(field.FieldType)`
     - Reference kind: `ReferenceKind.FieldType`

5. **Events:**
   - Event handler type: `CollectTypeReferences(event.EventHandlerType)`
     - Reference kind: `ReferenceKind.EventType`

#### Method: CollectTypeReferences()

```csharp
private static void CollectTypeReferences(
    TypeReference? typeRef,
    SymbolGraph graph,
    ImportGraphData graphData,
    HashSet<(string FullName, string? Namespace)> collected)
```

**Recursive type tree traversal:**

This is the **critical deep scanning function** that finds ALL foreign types.

**Algorithm by type reference kind:**

1. **NamedTypeReference:**
   - Find namespace: `ns = FindNamespaceForType(graph, graphData, named)`
   - **Get open generic CLR key**: `clrKey = GetOpenGenericClrKey(named)` (**NEW**)
   - **INVARIANT GUARD** (jumanji7 - prevents import garbage regression):
     ```csharp
     if (clrKey.Contains('[') || clrKey.Contains(','))
         ERROR: "INVARIANT VIOLATION: assembly-qualified key detected"
     ```
     - Detects if `GetOpenGenericClrKey()` failed to strip assembly info
     - Example bad key: `"IEnumerable\`1[[System.String, mscorlib, ...]]"`
     - Example good key: `"System.Collections.Generic.IEnumerable\`1"`
   - Add to collected set: `collected.Add((clrKey, ns))`
   - **Track unresolved types** (Fix E infrastructure):
     ```csharp
     if (ns == null && !string.IsNullOrEmpty(clrKey))
         graphData.UnresolvedClrKeys.Add(clrKey);
     ```
     - Captures types not in current graph (external assemblies)
     - Later resolved by `DeclaringAssemblyResolver` (Fix E Phase 1)
   - **Recurse into type arguments:** For `List<Dictionary<K, V>>`, recursively processes Dictionary, K, V
   - Example: `Dictionary<string, List<int>>` finds both Dictionary and List

2. **NestedTypeReference:**
   - Find namespace: `nestedNs = FindNamespaceForType(graph, graphData, nested)`
   - **Get open generic CLR key**: `nestedClrKey = GetOpenGenericClrKey(nested.FullReference)` (**NEW**)
   - **INVARIANT GUARD**: Same check as NamedTypeReference
   - Add to collected set: `collected.Add((nestedClrKey, nestedNs))`
   - **Track unresolved nested types** (Fix E):
     ```csharp
     if (nestedNs == null && !string.IsNullOrEmpty(nestedClrKey))
         graphData.UnresolvedClrKeys.Add(nestedClrKey);
     ```
   - **Recurse into type arguments** of nested type
   - Example: `ImmutableArray<T>.Builder` processes both outer and nested type

3. **ArrayTypeReference:**
   - Recurse into element type: `CollectTypeReferences(ctx, arr.ElementType, ...)`
   - Example: `List<int>[]` finds List

4. **PointerTypeReference / ByRefTypeReference:**
   - Recurse into pointee/referenced type
   - Example: `ref List<T>` finds List
   - TypeScript doesn't have pointers/refs, so we erase to underlying type

5. **GenericParameterReference:**
   - Skip - generic parameters are declared locally, don't need imports
   - Example: `T` in `class List<T>` doesn't add to collected set

**Why this is recursive:**
- Generic type arguments can be complex types: `Dictionary<string, List<MyClass>>`
- Need to find `Dictionary`, `List`, AND `MyClass`
- Arrays can contain generic types: `List<T>[]`
- Constraints can reference complex types: `where T : IEnumerable<string>`

#### Method: FindNamespaceForType()

```csharp
private static string? FindNamespaceForType(
    SymbolGraph graph,
    ImportGraphData graphData,
    TypeReference typeRef)
```

**Namespace lookup algorithm (jumanji7 - optimized):**

1. Get normalized CLR lookup key: `clrKey = GetClrLookupKey(typeRef)`
   - Returns null for generic parameters, placeholders (no import needed)
   - Returns open generic form for constructed generics (e.g., `"IEnumerable\`1"` not `"IEnumerable\`1[[System.String]]"`)

2. **Fast O(1) dictionary lookup** (NEW):
   ```csharp
   if (graphData.ClrFullNameToNamespace.TryGetValue(clrKey, out var ns))
       return ns;
   ```
   - Before: O(n) iteration through all namespaces
   - After: O(1) hash table lookup
   - Critical for BCL with 4,000+ types

3. Return null if not found (external type)

**Why null is valid:**
- Type might be from external assembly not in our graph
- Could be built-in TypeScript type (string, number)
- Could be type-forwarded or from different assembly version
- ImportPlanner will handle missing types appropriately

**Old algorithm (pre-jumanji7):**
1. Extract full CLR name from TypeReference
2. **Iterate through `graphData.NamespaceTypeIndex`** (O(n))
3. Check if namespace's type set contains the full name
4. Return namespace name if found

**Performance improvement:**
- Old: O(n × m) where n = namespaces, m = lookups per namespace
- New: O(1) per lookup via hash table

#### Method: GetClrLookupKey() (**NEW in jumanji7**)

```csharp
private static string? GetClrLookupKey(TypeReference typeRef)
```

**Purpose:** Get normalized CLR lookup key for any TypeReference kind. Always returns the OPEN generic definition name (not constructed).

**Algorithm by TypeReference kind:**

```csharp
NamedTypeReference named => GetOpenGenericClrKey(named)
NestedTypeReference nested => GetClrLookupKey(nested.FullReference)  // Recurse to NamedTypeReference
ArrayTypeReference arr => GetClrLookupKey(arr.ElementType)           // Recurse to element type
PointerTypeReference ptr => GetClrLookupKey(ptr.PointeeType)         // Recurse to pointee type
ByRefTypeReference byref => GetClrLookupKey(byref.ReferencedType)   // Recurse to referenced type
GenericParameterReference => null                                    // Type parameters are local
PlaceholderTypeReference => null                                     // Placeholders are unknown
_ => null                                                            // Unknown reference types
```

**Why this is needed:**
- TypeReference.FullName may be constructed with type arguments: `"IEnumerable\`1[[System.String, mscorlib...]]"`
- Index uses open generic keys: `"System.Collections.Generic.IEnumerable\`1"`
- Lookup would fail without normalization

**Examples:**
- `IEnumerable<string>` → `"System.Collections.Generic.IEnumerable\`1"` (strips `[[System.String]]`)
- `List<Dictionary<K,V>>` → `"System.Collections.Generic.List\`1"` (strips all type args)
- `Exception` → `"System.Exception"` (non-generic, pass through)
- `T` (GenericParameterReference) → `null` (no import needed)
- `int*` (PointerTypeReference) → `"System.Int32"` (recurses to pointee)

#### Method: GetOpenGenericClrKey() (**NEW in jumanji7**)

```csharp
private static string GetOpenGenericClrKey(NamedTypeReference named)
```

**Purpose:** Construct open generic CLR key from NamedTypeReference. This is the CRITICAL method that fixed the import garbage bug (commit 70d21db).

**Algorithm:**

1. **Extract components:**
   ```csharp
   var ns = named.Namespace;       // "System.Collections.Generic"
   var name = named.Name;          // "IEnumerable`1" or potentially "IEnumerable"
   var arity = named.Arity;        // 1 (0 for non-generic)
   ```

2. **Validate inputs (defensive):**
   ```csharp
   if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(name))
       return named.FullName;  // Fallback to FullName if namespace/name empty
   ```

3. **Strip assembly qualification from name (defensive):**
   ```csharp
   if (name.Contains(','))
       name = name.Substring(0, name.IndexOf(',')).Trim();
   ```
   - Prevents garbage like `"IEnumerable, mscorlib, Version=..."`
   - Defensive guard against malformed TypeReferences

4. **Non-generic path:**
   ```csharp
   if (arity == 0)
       return $"{ns}.{name}";  // e.g., "System.Exception"
   ```

5. **Generic path:**
   ```csharp
   // Strip backtick from name if present
   var nameWithoutArity = name.Contains('`')
       ? name.Substring(0, name.IndexOf('`'))
       : name;

   // Reconstruct with backtick arity
   return $"{ns}.{nameWithoutArity}`{arity}";
   ```
   - Handles both `"IEnumerable\`1"` and `"IEnumerable"` inputs
   - Always produces consistent `"IEnumerable\`1"` output

**Examples:**

| Input (NamedTypeReference) | Namespace | Name | Arity | Output CLR Key |
|---|---|---|---|---|
| `IEnumerable<T>` | `System.Collections.Generic` | `IEnumerable\`1` | 1 | `System.Collections.Generic.IEnumerable\`1` |
| `List<T>` | `System.Collections.Generic` | `List` | 1 | `System.Collections.Generic.List\`1` |
| `Dictionary<K,V>` | `System.Collections.Generic` | `Dictionary\`2` | 2 | `System.Collections.Generic.Dictionary\`2` |
| `Exception` | `System` | `Exception` | 0 | `System.Exception` |

**Why this fixed import garbage bug:**

**Before (broken):**
- Used `NamedTypeReference.FullName` directly: `"IEnumerable\`1[[System.String, mscorlib, Version=4.0.0.0, ...]]"`
- Lookup failed (not in index)
- No import generated
- TS2304 error

**After (fixed):**
- Uses `GetOpenGenericClrKey()`: `"System.Collections.Generic.IEnumerable\`1"`
- Matches index key exactly
- Import generated correctly
- No error

**Commit reference:** 70d21db ("fix: Use open generic CLR keys in import planning, eliminating garbage imports")

#### Method: GetTypeFullName()

Helper to extract CLR full name from various TypeReference kinds. Used for logging and diagnostics, not for lookups.

### Class: ImportGraphData

Result of import graph analysis.

**Properties:**

1. **NamespaceDependencies**: `Dictionary<string, HashSet<string>>`
   - Maps namespace name → set of namespace names it depends on
   - Example: `"System.Collections.Generic" → { "System", "System.Collections" }`
   - Used by ImportPlanner to generate import statements

2. **NamespaceTypeIndex**: `Dictionary<string, HashSet<string>>`
   - Maps namespace name → set of CLR full names of public types in that namespace
   - **Legacy set-based index** (kept for backwards compatibility)
   - Example: `"System.Collections.Generic" → { "List\`1", "Dictionary\`2", ... }`
   - Used for set operations and existence checks

3. **ClrFullNameToNamespace**: `Dictionary<string, string>` (**NEW in jumanji7**)
   - **Fast O(1) lookup map**: CLR full name (backtick form) → owning namespace
   - Example: `"System.Collections.Generic.IEnumerable\`1" → "System.Collections.Generic"`
   - Built once during `BuildNamespaceTypeIndex()` for O(1) lookups
   - **Critical performance improvement**: Replaces O(n) iteration with O(1) hash lookup
   - Used by `FindNamespaceForType()` for all namespace resolution

4. **CrossNamespaceReferences**: `List<CrossNamespaceReference>`
   - Detailed record of every foreign type reference
   - Used by ImportPlanner to know which specific types to import
   - Includes context: where reference occurs, what kind of reference
   - Example: `(SourceNs: "System.Linq", SourceType: "Enumerable", TargetNs: "System.Collections.Generic", TargetType: "IEnumerable\`1", Kind: MethodReturn)`

5. **UnresolvedClrKeys**: `HashSet<string>` (**NEW in jumanji7 - Fix E infrastructure**)
   - Set of CLR keys that couldn't be resolved to a namespace in the current graph
   - Example: `{ "System.Runtime.CompilerServices.Unsafe", "System.Reflection.Metadata.MetadataReader" }`
   - These are candidates for cross-assembly resolution
   - Populated during `CollectTypeReferences()` when `FindNamespaceForType()` returns null
   - Used by `DeclaringAssemblyResolver` to find declaring assemblies via reflection

6. **UnresolvedToAssembly**: `Dictionary<string, string>` (**NEW in jumanji7 - Fix E Phase 1**)
   - Maps unresolved CLR key → declaring assembly name (resolved via MetadataLoadContext)
   - Example: `{ "System.Runtime.CompilerServices.Unsafe" → "System.Runtime.CompilerServices.Unsafe" }`
   - Populated by `DeclaringAssemblyResolver.ResolveBatch()` after import graph analysis
   - Enables cross-assembly type resolution for external dependencies
   - Used in Fix E Phase 2 (planned) for cross-assembly import generation

### Record: CrossNamespaceReference

```csharp
record CrossNamespaceReference(
    string SourceNamespace,    // Namespace containing the reference
    string SourceType,         // Type making the reference (CLR name)
    string TargetNamespace,    // Namespace containing referenced type
    string TargetType,         // Referenced type (CLR name)
    ReferenceKind ReferenceKind // What kind of reference
)
```

### Enum: ReferenceKind

Categorizes where in the type system a reference occurs:

- `BaseClass` - Inheritance: `class Foo : Bar`
- `Interface` - Implementation: `class Foo : IBar`
- `GenericConstraint` - Constraint: `class Foo<T> where T : Bar`
- `MethodReturn` - Return type: `Bar GetBar()`
- `MethodParameter` - Parameter: `void SetBar(Bar b)`
- `ConstructorParameter` - **NEW in jumanji7**: Constructor parameter: `new Foo(Bar b)`
  - **Why added**: Major bug fix - constructors were not analyzed for import dependencies
  - **Impact**: -157 errors in BCL validation
  - **Example**: `new List<T>(IEnumerable<T> collection)` needs `IEnumerable\`1` import
- `PropertyType` - Property: `Bar MyBar { get; }`
- `FieldType` - Field: `Bar myBar;`
- `EventType` - Event: `event BarHandler OnBar;`

---

## File: EmitOrderPlanner.cs

### Purpose
Plans stable, deterministic emission order for all symbols. Ensures reproducible .d.ts files across runs by using `Renamer.GetFinalTypeName()` for sorting.

### Class: EmitOrderPlanner

Instance class (requires BuildContext for Renamer access).

#### Constructor

```csharp
public EmitOrderPlanner(BuildContext ctx)
```

Stores BuildContext to access Renamer during sorting.

#### Method: PlanOrder()

```csharp
public EmitOrder PlanOrder(SymbolGraph graph)
```

**Algorithm:**

1. Create empty list of `NamespaceEmitOrder`
2. **Sort namespaces** by namespace name alphabetically
3. For each namespace:
   - Call `OrderTypes()` to sort types within namespace
   - Create `NamespaceEmitOrder(Namespace, OrderedTypes)`
4. Return `EmitOrder` with ordered namespaces

**Why namespace-first ordering:**
- TypeScript uses modules (files) as organizational unit
- Each namespace → one directory → one `index.d.ts`
- Namespaces are independent (imports handle cross-references)

#### Method: OrderTypes()

```csharp
private List<TypeEmitOrder> OrderTypes(IReadOnlyList<TypeSymbol> types)
```

**Stable deterministic sorting algorithm:**

**Primary sort keys (in order):**

1. **Kind sort order** (see `GetKindSortOrder()`):
   - Enums first (0)
   - Delegates next (1)
   - Interfaces (2)
   - Structs (3)
   - Classes (4)
   - Static namespaces last (5)

2. **Final TypeScript name** from `ctx.Renamer.GetFinalTypeName(type)`:
   - Uses finalized, post-collision name
   - Ensures stable diffs when renaming occurs
   - Example: `"List_1"`, `"Dictionary_2"`

3. **Arity** (number of generic parameters):
   - For overloaded generic types
   - Example: `Action`, `Action<T>`, `Action<T1,T2>`

**For each type:**
- Recursively order nested types: `OrderTypes(type.NestedTypes)`
- Order members within type: `OrderMembers(type)`
- Create `TypeEmitOrder(Type, OrderedMembers, OrderedNestedTypes)`

**Why this ordering:**
- Forward reference safe: Enums/delegates can be used before defined
- Interfaces before structs/classes: Common TypeScript pattern
- Alphabetical by final name: Predictable, git-friendly
- Arity disambiguation: Handles generic overloads

#### Method: OrderMembers()

```csharp
private MemberEmitOrder OrderMembers(TypeSymbol type)
```

**Member category ordering:**

**Emission order:**
1. Constructors
2. Fields
3. Properties
4. Events
5. Methods

**Within each category, sort by:**

1. **IsStatic**: Instance members first, then static members
   - Matches typical TypeScript class structure
   - Matches C# convention

2. **Final TypeScript member name** via `ctx.Renamer.GetFinalMemberName()`:
   - Must compute proper `EmitScope` for renaming context
   - Uses `ScopeFactory.ClassSurface(type, isStatic)`
   - Gets name after collision resolution

3. **Arity** (for methods): Method-level generic parameter count

4. **Canonical signature** (for overloads): From `StableId.CanonicalSignature`
   - Disambiguates overloaded methods
   - Example: `DoWork()`, `DoWork(int)`, `DoWork(string, bool)`

**Filtering:**
- Only include members with `EmitScope == ClassSurface` or `StaticSurface`
- Excludes view-only members (`InterfaceView`, `ExplicitView`)
- Constructors always included (no filtering)

**Result:**
- `MemberEmitOrder` record with ordered lists for each member kind

#### Method: GetKindSortOrder()

```csharp
private int GetKindSortOrder(TypeKind kind)
```

**Type kind priority mapping:**
- `Enum` → 0
- `Delegate` → 1
- `Interface` → 2
- `Struct` → 3
- `Class` → 4
- `StaticNamespace` → 5
- Unknown → 999

**Rationale:**
- Enums are simplest, no dependencies
- Delegates are function signatures, minimal structure
- Interfaces define contracts
- Structs implement interfaces
- Classes are most complex
- Static namespaces are synthetic containers

### Class: EmitOrder

Root data structure for planned emission order.

**Property:**
- `Namespaces: IReadOnlyList<NamespaceEmitOrder>`

### Record: NamespaceEmitOrder

Emission order for one namespace.

**Properties:**
- `Namespace: NamespaceSymbol` - The namespace to emit
- `OrderedTypes: IReadOnlyList<TypeEmitOrder>` - Types in emission order

### Record: TypeEmitOrder

Emission order for one type.

**Properties:**
- `Type: TypeSymbol` - The type to emit
- `OrderedMembers: MemberEmitOrder` - Members in emission order
- `OrderedNestedTypes: IReadOnlyList<TypeEmitOrder>` - Nested types in emission order (recursive)

### Record: MemberEmitOrder

Emission order for members within a type.

**Properties:**
- `Constructors: IReadOnlyList<ConstructorSymbol>`
- `Fields: IReadOnlyList<FieldSymbol>`
- `Properties: IReadOnlyList<PropertySymbol>`
- `Events: IReadOnlyList<EventSymbol>`
- `Methods: IReadOnlyList<MethodSymbol>`

All lists are already sorted and filtered for emission.

---

## File: PathPlanner.cs

### Purpose
Plans module specifiers for TypeScript imports. Generates relative paths based on source/target namespaces and emission area (public vs internal). Handles root namespace (`_root`) and nested namespace directories.

### Class: PathPlanner

Static class with path computation logic.

#### Method: GetSpecifier()

```csharp
public static string GetSpecifier(string sourceNamespace, string targetNamespace)
```

**Relative path computation algorithm:**

**Input:** Source and target namespace names (empty string for root namespace)

**Output:** Relative module specifier suitable for TypeScript import

**Path generation rules:**

1. **Determine if root:**
   - `isSourceRoot = string.IsNullOrEmpty(sourceNamespace)`
   - `isTargetRoot = string.IsNullOrEmpty(targetNamespace)`
   - Root namespace uses `_root` directory name

2. **Compute target path:**
   - If target is root: `targetDir = "_root"`, `targetFile = "index"`
   - If target is named: `targetDir = targetNamespace`, `targetFile = "internal/index"`

3. **Compute relative path:**

   **Source is root:**
   - Root → Root: `./_root/index`
   - Root → Non-root: `./{targetNamespace}/internal/index`

   **Source is non-root:**
   - Non-root → Root: `../_root/index`
   - Non-root → Non-root: `../{targetNamespace}/internal/index`

**Examples:**

| Source NS | Target NS | Import Specifier |
|-----------|-----------|------------------|
| (root) | (root) | `./_root/index` |
| (root) | `System` | `./System/internal/index` |
| `System.Collections` | (root) | `../_root/index` |
| `System.Collections` | `System` | `../System/internal/index` |
| `System.Collections` | `System.Text` | `../System.Text/internal/index` |

**Why always `internal/index`:**
- Public API uses `index.d.ts` at namespace root
- Internal declarations use `internal/index.d.ts` subdirectory
- Import statements always target internal (full type definitions)
- Public API re-exports from internal

#### Method: GetNamespaceDirectory()

```csharp
public static string GetNamespaceDirectory(string namespaceName)
```

Returns directory name for namespace on disk:
- Empty/null → `"_root"`
- Named namespace → namespace name as-is

#### Method: GetInternalSubdirectory()

```csharp
public static string GetInternalSubdirectory(string namespaceName)
```

Returns subdirectory name for internal declarations:
- Empty/null → `"_root"` (root has internal declarations at root level)
- Named namespace → `"internal"`

**Why different for root:**
- Root namespace: `_root/index.d.ts` contains declarations
- Named namespace: `{Namespace}/internal/index.d.ts` contains declarations

---

## File: InterfaceConstraintAuditor.cs

### Purpose
Audits constructor constraint loss per (Type, Interface) pair. Detects when TypeScript loses C# `new()` constraint information. Prevents duplicate diagnostics for view members by auditing at interface implementation level.

**M4/M5 Fix:** Constructor-constraint loss is assessed ONCE per implemented interface, not per cloned view member.

### Class: InterfaceConstraintAuditor

Static class with constraint auditing logic.

#### Method: Audit()

```csharp
public static InterfaceConstraintFindings Audit(
    BuildContext ctx,
    SymbolGraph graph)
```

**Algorithm:**

1. Create findings builder: `ImmutableArray.CreateBuilder<InterfaceConstraintFinding>()`
2. Initialize counters for logging

3. **For each namespace:**
   - For each type in namespace:
     - Skip if type implements no interfaces
     - **For each interface reference:**
       - Resolve interface TypeSymbol: `ResolveInterface(graph, ifaceRef)`
       - Check constraints: `CheckInterfaceConstraints()`
       - If finding detected, add to builder

4. Return `InterfaceConstraintFindings` with all findings

**Why (Type, Interface) pairs:**
- Same interface implemented by multiple types → separate findings
- Same type implementing multiple interfaces → separate findings
- Prevents finding duplication when multiple view members exist

#### Method: CheckInterfaceConstraints()

```csharp
private static InterfaceConstraintFinding? CheckInterfaceConstraints(
    BuildContext ctx,
    SymbolGraph graph,
    TypeSymbol implementingType,
    TypeSymbol interfaceType,
    TypeReference interfaceReference)
```

**Constraint loss detection algorithm:**

1. **Skip if interface has no generic parameters**
   - No type parameters → no constraints to lose

2. **For each generic parameter in interface:**
   - Check `SpecialConstraints` flags for `DefaultConstructor` bit
   - If `(gp.SpecialConstraints & GenericParameterConstraints.DefaultConstructor) != 0`:
     - **Constructor constraint loss detected**

3. **Create finding:**
   ```csharp
   new InterfaceConstraintFinding {
       ImplementingTypeStableId = implementingType.StableId,
       InterfaceStableId = interfaceType.StableId,
       LossKind = ConstraintLossKind.ConstructorConstraintLoss,
       GenericParameterName = gp.Name,
       TypeFullName = implementingType.ClrFullName,
       InterfaceFullName = interfaceType.ClrFullName
   }
   ```

**What is constructor constraint loss:**

C# code:
```csharp
interface IFactory<T> where T : new() {
    T Create();
}

class StringFactory : IFactory<string> {
    public string Create() => new string();
}
```

TypeScript output (constraint lost):
```typescript
interface IFactory_1<T> { // No way to express "new()" constraint
    Create(): T;
}

class StringFactory implements IFactory_1<string> {
    Create(): string { ... }
}
```

**Why this matters:**
- TypeScript can't enforce `new()` constraint at compile time
- Runtime binding code needs to know constraint exists
- Metadata sidecar tracks this information
- PhaseGate emits PG_CT_001 diagnostic

#### Method: ResolveInterface()

```csharp
private static TypeSymbol? ResolveInterface(
    SymbolGraph graph,
    TypeReference ifaceRef)
```

**Interface type lookup:**

1. Extract full CLR name: `GetTypeReferenceName(ifaceRef)`
2. Search all namespaces for type with:
   - Matching CLR full name
   - Kind is `TypeKind.Interface`
3. Return first match (null if not found)

**Why it might return null:**
- Interface from external assembly not in graph
- Interface from system library (e.g., `IDisposable`)

#### Method: GetTypeReferenceName()

Helper to extract full name from TypeReference (handles Named/Nested types).

### Class: InterfaceConstraintFindings

Collection of audit results.

**Property:**
- `Findings: ImmutableArray<InterfaceConstraintFinding>` - All constraint loss findings

### Record: InterfaceConstraintFinding

Single finding for a (Type, Interface) pair with constructor constraint loss.

**Properties:**
- `ImplementingTypeStableId: StableId` - Type implementing interface
- `InterfaceStableId: StableId` - Interface being implemented
- `LossKind: ConstraintLossKind` - What kind of constraint is lost
- `GenericParameterName: string` - Which type parameter has `new()` constraint
- `TypeFullName: string` - CLR full name for reporting
- `InterfaceFullName: string` - CLR full name for reporting

### Enum: ConstraintLossKind

```csharp
enum ConstraintLossKind {
    None,
    ConstructorConstraintLoss
}
```

Currently only `ConstructorConstraintLoss` is detected. Future expansion for other constraint kinds.

---

## File: TsAssignability.cs

### Purpose
TypeScript assignability checking for erased type shapes. Implements simplified TypeScript structural typing rules to validate that interface implementations satisfy contracts in the emitted TypeScript world.

### Class: TsAssignability

Static class with assignability logic.

#### Method: IsAssignable()

```csharp
public static bool IsAssignable(TsTypeShape source, TsTypeShape target)
```

**TypeScript structural typing rules:**

**1. Exact match:**
```csharp
if (source.Equals(target)) return true;
```

**2. Unknown type (conservative):**
```csharp
if (source is TsTypeShape.Unknown || target is TsTypeShape.Unknown)
    return true;
```
- Used for external/unresolved types
- Conservative for validation (assume compatible)

**3. Type parameter compatibility:**
```csharp
if (source is TsTypeShape.TypeParameter sourceParam &&
    target is TsTypeShape.TypeParameter targetParam)
{
    return sourceParam.Name == targetParam.Name;
}
```
- Type parameters match by name
- Example: `T` is compatible with `T`

**4. Array covariance:**
```csharp
if (source is TsTypeShape.Array sourceArr &&
    target is TsTypeShape.Array targetArr)
{
    return IsAssignable(sourceArr.ElementType, targetArr.ElementType);
}
```
- TypeScript arrays are readonly in our model → covariant
- Example: `string[]` assignable to `object[]`

**5. Generic application:**
```csharp
if (source is TsTypeShape.GenericApplication sourceApp &&
    target is TsTypeShape.GenericApplication targetApp)
{
    // Generic type definitions must match
    if (!IsAssignable(sourceApp.GenericType, targetApp.GenericType))
        return false;

    // Type arguments must match (invariant)
    return sourceApp.TypeArguments.Zip(targetApp.TypeArguments)
        .All(pair => IsAssignable(pair.First, pair.Second));
}
```
- Base generic type must match: `List<>` vs `List<>`
- Type arguments checked pairwise (currently invariant)
- Could be improved with variance annotations

**6. Named type widening:**
```csharp
if (source is TsTypeShape.Named sourceNamed &&
    target is TsTypeShape.Named targetNamed)
{
    return IsWideningConversion(sourceNamed.FullName, targetNamed.FullName);
}
```

#### Method: IsWideningConversion()

```csharp
private static bool IsWideningConversion(string sourceFullName, string targetFullName)
```

**Known widening conversions:**

1. **Same type:**
   ```csharp
   if (sourceFullName == targetFullName) return true;
   ```

2. **Numeric type widening:**
   ```csharp
   var numericTypes = new[] {
       "System.Byte", "System.SByte", "System.Int16", "System.UInt16",
       "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
       "System.Single", "System.Double", "System.Decimal"
   };

   if (numericTypes.Contains(source) && numericTypes.Contains(target))
       return true;
   ```
   - All numeric types widen to each other (in TypeScript terms)
   - All map to `number` brand at runtime

3. **Everything widens to Object:**
   ```csharp
   if (targetFullName == "System.Object") return true;
   ```

4. **ValueType widens to Object:**
   ```csharp
   if (sourceFullName == "System.ValueType" && targetFullName == "System.Object")
       return true;
   ```

**What this enables:**
- Validates covariant return types in interfaces
- Checks if overridden methods satisfy base contracts
- Detects breaking changes in TypeScript world

#### Method: IsMethodAssignable()

```csharp
public static bool IsMethodAssignable(
    TsMethodSignature source,
    TsMethodSignature target)
```

**Method signature compatibility:**

**Checks in order:**

1. **Name match:**
   ```csharp
   if (source.Name != target.Name) return false;
   ```

2. **Arity match:**
   ```csharp
   if (source.Arity != target.Arity) return false;
   ```
   - Number of generic type parameters must match

3. **Parameter count match:**
   ```csharp
   if (source.Parameters.Count != target.Parameters.Count) return false;
   ```

4. **Return type covariance:**
   ```csharp
   if (!IsAssignable(source.ReturnType, target.ReturnType)) return false;
   ```
   - Source return type can be subtype of target return type
   - Example: Interface requires `object`, impl can return `string`

5. **Parameter type checking:**
   ```csharp
   for (int i = 0; i < source.Parameters.Count; i++)
   {
       if (!source.Parameters[i].Equals(target.Parameters[i]))
       {
           // Allow if both directions assignable (invariant for safety)
           if (!IsAssignable(source.Parameters[i], target.Parameters[i]) &&
               !IsAssignable(target.Parameters[i], source.Parameters[i]))
           {
               return false;
           }
       }
   }
   ```
   - For validation, use invariant parameter check (stricter)
   - Real TypeScript uses contravariance (parameter types can be widened)

**Why invariant parameters:**
- Safer for catching real breaks
- TypeScript allows contravariance, but we're more strict
- Prevents subtle runtime errors

#### Method: IsPropertyAssignable()

```csharp
public static bool IsPropertyAssignable(
    TsPropertySignature source,
    TsPropertySignature target)
```

**Property compatibility:**

1. **Name match:**
   ```csharp
   if (source.Name != target.Name) return false;
   ```

2. **Readonly covariance:**
   ```csharp
   if (source.IsReadonly && target.IsReadonly)
   {
       return IsAssignable(source.PropertyType, target.PropertyType);
   }
   ```
   - Readonly properties are covariant (can be more specific)
   - Example: Interface requires `readonly object`, impl can have `readonly string`

3. **Mutable invariance:**
   ```csharp
   return source.PropertyType.Equals(target.PropertyType);
   ```
   - Mutable properties must have exact type match
   - Prevents unsound reads/writes

**Why readonly is covariant:**
- Can't write to readonly property → no contravariance needed
- Reading more specific type is always safe
- Matches TypeScript semantics

---

## File: TsErase.cs

### Purpose
Erases CLR-specific details to produce TypeScript-level signatures. Used for assignability checking in PhaseGate validation. Strips away C# concepts (ref/out, pointers) that don't exist in TypeScript.

### Class: TsErase

Static class with erasure logic.

#### Method: EraseMember(MethodSymbol)

```csharp
public static TsMethodSignature EraseMember(MethodSymbol method)
```

**Method erasure algorithm:**

1. Take final TypeScript name: `method.TsEmitName`
2. Take arity: `method.Arity` (generic parameter count)
3. **Erase each parameter type:**
   - Map `method.Parameters` → `EraseType(p.Type)` for each parameter
   - **Removes ref/out modifiers** (erased to underlying type)
4. **Erase return type:** `EraseType(method.ReturnType)`

**Result:** `TsMethodSignature` - Pure TypeScript signature with no CLR concepts

**Example:**
```csharp
// C# method:
public ref int GetValue(ref string s, out int x) { ... }

// After erasure:
TsMethodSignature(
    Name: "GetValue",
    Arity: 0,
    Parameters: [TsTypeShape.Named("System.String"), TsTypeShape.Named("System.Int32")],
    ReturnType: TsTypeShape.Named("System.Int32")
)
```

#### Method: EraseMember(PropertySymbol)

```csharp
public static TsPropertySignature EraseMember(PropertySymbol property)
```

**Property erasure algorithm:**

1. Take final TypeScript name: `property.TsEmitName`
2. Erase property type: `EraseType(property.PropertyType)`
3. Determine readonly: `IsReadonly = !property.HasSetter`
   - No setter → readonly property

**Result:** `TsPropertySignature`

#### Method: EraseType(TypeReference)

```csharp
public static TsTypeShape EraseType(TypeReference typeRef)
```

**Type erasure by reference kind:**

**1. NamedTypeReference (with type arguments):**
```csharp
NamedTypeReference named when named.TypeArguments.Count > 0 =>
    new TsTypeShape.GenericApplication(
        new TsTypeShape.Named(named.FullName),
        named.TypeArguments.Select(EraseType).ToList())
```
- Constructed generic: `List<int>` → `GenericApplication(Named("List`1"), [Named("Int32")])`
- **Recursively erase type arguments**

**2. NamedTypeReference (simple):**
```csharp
NamedTypeReference named =>
    new TsTypeShape.Named(named.FullName)
```
- Simple type: `string` → `Named("System.String")`

**3. NestedTypeReference:**
```csharp
NestedTypeReference nested =>
    new TsTypeShape.Named(nested.FullReference.FullName)
```
- Nested type: `Outer.Inner` → `Named("Outer.Inner")`
- Uses full reference for proper comparison

**4. GenericParameterReference:**
```csharp
GenericParameterReference gp =>
    new TsTypeShape.TypeParameter(gp.Name)
```
- Type parameter: `T` → `TypeParameter("T")`

**5. ArrayTypeReference:**
```csharp
ArrayTypeReference arr =>
    new TsTypeShape.Array(EraseType(arr.ElementType))
```
- Array: `int[]` → `Array(Named("System.Int32"))`
- **Recursively erase element type**

**6. PointerTypeReference:**
```csharp
PointerTypeReference ptr =>
    EraseType(ptr.PointeeType)
```
- **Erase pointer:** `int*` → `Named("System.Int32")`
- TypeScript doesn't have pointers

**7. ByRefTypeReference:**
```csharp
ByRefTypeReference byref =>
    EraseType(byref.ReferencedType)
```
- **Erase ref/out:** `ref string` → `Named("System.String")`
- TypeScript doesn't have ref parameters

**8. Fallback:**
```csharp
_ => new TsTypeShape.Unknown(typeRef.ToString() ?? "unknown")
```

### Record: TsMethodSignature

TypeScript-level method signature (after CLR erasure).

```csharp
record TsMethodSignature(
    string Name,                  // TypeScript emit name
    int Arity,                    // Generic parameter count
    List<TsTypeShape> Parameters, // Erased parameter types
    TsTypeShape ReturnType        // Erased return type
)
```

### Record: TsPropertySignature

TypeScript-level property signature (after CLR erasure).

```csharp
record TsPropertySignature(
    string Name,             // TypeScript emit name
    TsTypeShape PropertyType, // Erased property type
    bool IsReadonly          // true if no setter
)
```

### Abstract Record: TsTypeShape

Simplified type representation for TypeScript world.

**Hierarchy:**

```csharp
abstract record TsTypeShape
{
    // Simple named type: "System.String"
    sealed record Named(string FullName) : TsTypeShape;

    // Type parameter: "T"
    sealed record TypeParameter(string Name) : TsTypeShape;

    // Array type: "T[]"
    sealed record Array(TsTypeShape ElementType) : TsTypeShape;

    // Generic application: "List<int>"
    sealed record GenericApplication(
        TsTypeShape GenericType,
        List<TsTypeShape> TypeArguments
    ) : TsTypeShape;

    // Unknown/unresolved type
    sealed record Unknown(string Description) : TsTypeShape;
}
```

**Why this representation:**
- Structural equality by default (C# records)
- No CLR-specific details (pointers, ref, etc.)
- Recursive structure for generic applications
- Easy pattern matching in validation

---

## File: PhaseGate.cs Overview

### Purpose
Validates the symbol graph before emission. Performs comprehensive validation checks and policy enforcement. Acts as quality gate between Shape/Plan phases and Emit phase.

**This is the gatekeeper** - no symbols pass to Emit without PhaseGate approval.

### Class: PhaseGate

Static class with master validation orchestration.

#### Method: Validate()

```csharp
public static void Validate(
    BuildContext ctx,
    SymbolGraph graph,
    ImportPlan imports,
    InterfaceConstraintFindings constraintFindings)
```

**Validation orchestration:**

1. **Create ValidationContext:**
   ```csharp
   var validationContext = new ValidationContext {
       ErrorCount = 0,
       WarningCount = 0,
       Diagnostics = new List<string>(),
       SanitizedNameCount = 0,
       InterfaceConformanceIssuesByType = new Dictionary<string, List<string>>()
   };
   ```

2. **Run core validation checks** (delegated to `Validation.Core`):
   - `ValidateTypeNames()` - Check type naming rules
   - `ValidateMemberNames()` - Check member naming rules
   - `ValidateGenericParameters()` - Validate generic parameter constraints
   - `ValidateInterfaceConformance()` - Check interface implementation correctness
   - `ValidateInheritance()` - Validate inheritance hierarchies
   - `ValidateEmitScopes()` - Check EmitScope assignments
   - `ValidateImports()` - Validate import consistency
   - `ValidatePolicyCompliance()` - Enforce policy rules

3. **Run PhaseGate Hardening checks** (50+ additional rules):

   **M1: Identifier sanitization:**
   - `Names.ValidateIdentifiers()` - PG_NAME_001/002

   **M2: Overload collision detection:**
   - `Names.ValidateOverloadCollisions()` - PG_NAME_006

   **M3: View integrity validation:**
   - `Views.Validate()` - Basic view checks
   - `Views.ValidateIntegrity()` - PG_VIEW_001/002/003 (3 hard rules)

   **M4: Constructor constraint loss:**
   - `Constraints.EmitDiagnostics()` - PG_CT_001/002

   **M5: Scoping and naming:**
   - `Views.ValidateMemberScoping()` - PG_NAME_003/004
   - `Scopes.ValidateEmitScopeInvariants()` - PG_INT_002/003
   - `Scopes.ValidateScopeMismatches()` - PG_SCOPE_003/004
   - `Names.ValidateClassSurfaceUniqueness()` - PG_NAME_005

   **M6: Finalization sweep:**
   - `Finalization.Validate()` - PG_FIN_001 through PG_FIN_009
   - Catches symbols without proper finalization

   **M7: Type reference validation:**
   - `Types.ValidatePrinterNameConsistency()` - PG_PRINT_001
   - `Types.ValidateTypeMapCompliance()` - PG_TYPEMAP_001 (MUST RUN EARLY)
   - `Types.ValidateExternalTypeResolution()` - PG_LOAD_001 (AFTER TypeMap)

   **M8: Public API surface:**
   - `ImportExport.ValidatePublicApiSurface()` - PG_API_001/002 (BEFORE imports)

   **M9: Import completeness:**
   - `ImportExport.ValidateImportCompleteness()` - PG_IMPORT_001

   **M10: Export completeness:**
   - `ImportExport.ValidateExportCompleteness()` - PG_EXPORT_001

4. **Report results:**
   ```csharp
   ctx.Log("PhaseGate", $"{errorCount} errors, {warningCount} warnings, {infoCount} info");
   ctx.Log("PhaseGate", $"Sanitized {sanitizedNameCount} reserved word identifiers");
   ```

5. **Print diagnostic summary table:**
   - Group diagnostics by code (PG_NAME_001, PG_VIEW_002, etc.)
   - Sort by count (most frequent first)
   - Show description for each code

6. **Handle errors:**
   ```csharp
   if (validationContext.ErrorCount > 0)
   {
       var sampleText = errors.Take(20).Join("\n");
       ctx.Diagnostics.Error(DiagnosticCodes.ValidationFailed,
           $"PhaseGate validation failed with {errorCount} errors\n\n{sampleText}");
   }
   ```

7. **Write diagnostic files:**
   - `Context.WriteDiagnosticsFile()` - Full detailed report
   - `Context.WriteSummaryJson()` - Machine-readable summary for CI

**Why this order matters:**
- TypeMap validation must run before external type checks (foundational)
- API surface validation before import checks (more fundamental)
- View integrity before member scoping (views must exist first)
- Finalization last (catches anything missed)

### Validation Module Structure

PhaseGate delegates to specialized validation modules in `Validation/` directory:

- **Core.cs** - Core validation checks (8 categories)
- **Names.cs** - Name collision, sanitization, uniqueness (5 checks)
- **Views.cs** - View integrity, member scoping (4 checks)
- **Scopes.cs** - EmitScope validation (3 checks)
- **Types.cs** - Type reference validation (3 checks)
- **ImportExport.cs** - Import/export completeness (3 checks)
- **Constraints.cs** - Generic constraint auditing (2 diagnostics)
- **Finalization.cs** - Finalization sweep (9 checks)
- **Context.cs** - Diagnostic tracking and reporting
- **Shared.cs** - Shared validation utilities

**Total validation rules:** 50+ distinct checks covering:
- Naming correctness
- Scope assignments
- View integrity
- Type reference validity
- Import/export consistency
- Interface conformance
- Generic constraint tracking
- Finalization completeness
- Policy compliance

**Diagnostic code format:**
- `PG_CATEGORY_NNN` (e.g., `PG_NAME_001`, `PG_VIEW_003`)
- Categories: NAME, VIEW, SCOPE, INT (interface), FIN, PRINT, TYPEMAP, LOAD, API, IMPORT, EXPORT, CT (constraint)

**Full PhaseGate validation details will be documented separately in `07-phasegate.md`.**

---

## Key Algorithms

### Import Graph Construction

**Algorithm: Build cross-namespace dependency graph**

**Input:** SymbolGraph with all types and members

**Output:** ImportGraphData with namespace dependencies and detailed references

**Steps:**

1. **Build reverse index:**
   - For each namespace, collect all public type CLR names
   - Store in `NamespaceTypeIndex: namespace → set of type names`
   - Used for fast "which namespace contains this type?" lookup

2. **Scan all types:**
   - For each public type in each namespace:
     - Collect base class type references
     - Collect interface type references
     - Collect generic constraint type references
     - Collect member signature type references

3. **Recursive collection:**
   - For each type reference encountered:
     - Recursively descend into generic type arguments
     - Recursively descend into array element types
     - Erase pointers/byrefs to underlying type
     - Skip generic parameters (local, no import needed)

4. **Namespace resolution:**
   - For each named type found, look up in NamespaceTypeIndex
   - If found in different namespace, add edge to dependency graph
   - Create detailed CrossNamespaceReference record

5. **Aggregation:**
   - NamespaceDependencies: Set of edges (source → target namespaces)
   - CrossNamespaceReferences: Detailed list of every foreign type usage
   - Used by ImportPlanner to generate import statements

**Why this works:**
- Reverse index makes lookup O(1) instead of O(n)
- Recursive collection finds deeply nested types (e.g., `Dictionary<string, List<MyClass>>`)
- Public-only scanning ensures we only import emitted types

**Complexity:**
- O(N × M) where N = types, M = average references per type
- Typically fast (thousands of types in milliseconds)

### Topological Sort (NOT IMPLEMENTED)

**Note:** Current implementation does **not** use topological sort. EmitOrderPlanner sorts namespaces alphabetically and types by kind/name.

**Why topological sort is not needed:**
- TypeScript allows forward references within a module
- Imports handle cross-namespace dependencies
- Types within namespace can reference each other freely
- Only ordering needed is for determinism (alphabetical by name)

**If topological sort were implemented:**

1. Build dependency graph (type → set of referenced types)
2. Initialize in-degree for each type
3. Add all zero in-degree types to queue
4. While queue not empty:
   - Dequeue type
   - Add to output order
   - For each dependent type:
     - Decrement in-degree
     - If in-degree reaches zero, enqueue
5. If output contains all types: success
6. If types remain: cycle detected (error)

**Cycle detection:**
- Cycles would indicate recursive type references
- TypeScript handles these fine (nominal vs structural typing)
- Not currently validated

### Relative Path Computation

**Algorithm: Compute relative module path from source to target namespace**

**Input:** Source namespace name, target namespace name

**Output:** Relative import specifier string

**Rules:**

1. **Determine directory structure:**
   - Root namespace: `_root/` directory
   - Named namespace: `{NamespaceName}/` directory
   - Internal declarations: `internal/index.d.ts` subdirectory

2. **Compute relative path:**
   - Same directory level: Use `./` prefix
   - Different directory level: Use `../` prefix
   - Always target `internal/index` (full type definitions)

3. **Special cases:**
   - Root → Root: `./_root/index` (no internal subdirectory)
   - Root → Named: `./System/internal/index`
   - Named → Root: `../_root/index`
   - Named → Named: `../System.Text/internal/index`

**Why always `internal/index`:**
- Public API (`index.d.ts`) re-exports from internal
- Imports need full type definitions, not just public surface
- Consistent import paths across all namespaces

**Examples:**

Directory structure:
```
output/
  _root/
    index.d.ts
  System/
    internal/
      index.d.ts
  System.Collections/
    internal/
      index.d.ts
  System.Collections.Generic/
    internal/
      index.d.ts
```

Import specifiers:
- From `System.Collections.Generic` to `System.Collections`:
  - Path: `../System.Collections/internal/index`
  - Imports: `import { ... } from "../System.Collections/internal/index"`

- From `System` to `_root`:
  - Path: `../_root/index`
  - Imports: `import { ... } from "../_root/index"`

### Constraint Loss Detection

**Algorithm: Detect when TypeScript loses C# generic constraints**

**Focus:** Constructor constraints (`new()`) only

**Input:** (Type, Interface) pair where Type implements Interface

**Output:** InterfaceConstraintFinding if constraint loss detected

**Steps:**

1. **Get interface generic parameters:**
   - If interface has no generic parameters: skip (no constraints)
   - For each generic parameter: `interface IFactory<T> where T : new()`

2. **Check SpecialConstraints:**
   ```csharp
   if ((gp.SpecialConstraints & GenericParameterConstraints.DefaultConstructor) != 0)
   {
       // Constraint loss detected
   }
   ```

3. **Create finding:**
   - Record implementing type StableId
   - Record interface StableId
   - Record which generic parameter has constraint
   - Tag as `ConstraintLossKind.ConstructorConstraintLoss`

4. **Emit diagnostic:**
   - PhaseGate emits PG_CT_001 (ERROR severity)
   - Metadata sidecar tracks this information
   - Runtime binding code can enforce constraint if needed

**Why constructor constraint specifically:**
- `new()` constraint guarantees type has parameterless constructor
- TypeScript has no equivalent concept
- Information is lost in TypeScript declarations
- Must be tracked separately for runtime binding

**Other constraints NOT tracked (yet):**
- `class` constraint: TypeScript has nominal types, somewhat equivalent
- `struct` constraint: TypeScript doesn't distinguish value/reference types
- Interface constraints: Preserved in TypeScript (`T extends IFoo`)
- Base class constraints: Preserved in TypeScript (`T extends BaseClass`)

**Future expansion:**
- Could track `class` constraint for runtime checks
- Could track `struct` constraint for boxing/unboxing decisions
- Currently only `new()` is critical for binding

---

## Summary

The **Plan phase** performs final validation and preparation before emission:

1. **ImportGraph** builds complete namespace dependency graph
2. **ImportPlanner** generates TypeScript import statements with aliases
3. **EmitOrderPlanner** creates deterministic emission order
4. **PathPlanner** computes relative module paths
5. **InterfaceConstraintAuditor** detects constraint losses
6. **TsErase/TsAssignability** validate TypeScript compatibility
7. **PhaseGate** enforces 50+ correctness rules

**PhaseGate validation categories:**
- Type/member naming correctness
- EmitScope integrity
- View correctness (3 hard rules)
- Import/export completeness
- Type reference validity
- Generic constraint tracking
- Finalization completeness
- Policy compliance

**After Plan phase:**
- Symbol graph is validated and correct
- Import dependencies are resolved
- Emission order is determined
- All PhaseGate invariants hold
- Ready for code emission

**Next phase:** Emit (generate `.d.ts`, `.metadata.json`, `.bindings.json`)

**See also:**
- `07-phasegate.md` - Detailed PhaseGate validation documentation
- `05-phase-shape.md` - How symbols were shaped before validation
- `08-phase-emit.md` - How validated symbols are emitted to files
