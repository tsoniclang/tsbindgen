# Phase 2: Load (Reflection)

## Overview

The **Load phase** performs reflection over .NET assemblies to extract pure CLR metadata. This phase operates entirely in the CLR domain—no TypeScript concepts exist yet. It reads assemblies using `System.Reflection` and `MetadataLoadContext`, building a complete `SymbolGraph` that captures types, members, and their relationships.

**Key responsibilities:**
- Load assemblies with transitive closure resolution via BFS
- Validate assembly identity (version consistency, PublicKeyToken)
- Extract all public types and their members via reflection
- Build type references (named, generic, array, pointer, byref)
- Substitute type parameters in closed generic interfaces
- Output pure CLR facts in `SymbolGraph` structure

**Key constraint:** All data is pure CLR—no `TsEmitName`, no TypeScript-specific transformations. Those happen in later phases.

---

## File: AssemblyLoader.cs

### Purpose

Creates `MetadataLoadContext` for loading assemblies in isolation. Handles reference pack resolution for .NET BCL assemblies. Implements transitive closure loading via BFS over assembly references. Validates assembly identity consistency (PublicKeyToken, version drift).

### Record: LoadClosureResult

**Definition:**
```csharp
public sealed record LoadClosureResult(
    MetadataLoadContext LoadContext,
    IReadOnlyList<Assembly> Assemblies,
    IReadOnlyDictionary<AssemblyKey, string> ResolvedPaths);
```

**Purpose:** Result of loading transitive closure of assemblies.

**Fields:**
- `LoadContext` - The MetadataLoadContext with all assemblies loaded
- `Assemblies` - List of successfully loaded Assembly objects
- `ResolvedPaths` - Map from AssemblyKey to resolved file path

---

### Class: AssemblyLoader

**Fields:**
- `_ctx: BuildContext` - Context for logging and diagnostics

**Constructor:**
```csharp
public AssemblyLoader(BuildContext ctx)
```
- Stores BuildContext for use by all methods

---

### Method: CreateLoadContext

**Signature:**
```csharp
public MetadataLoadContext CreateLoadContext(IReadOnlyList<string> assemblyPaths)
```

**What it does:**
Creates a `MetadataLoadContext` for the given assemblies. Uses `PathAssemblyResolver` to look in:
1. The directory containing the target assemblies
2. The reference assemblies directory (same as target for version consistency)

**Parameters:**
- `assemblyPaths` - List of absolute paths to assembly DLL files to load

**Returns:**
- `MetadataLoadContext` - Configured load context ready to load assemblies

**Called by:**
- Legacy code path (direct assembly loading without closure resolution)
- Test code

**How it works:**
1. Calls `GetReferenceAssembliesPath()` to find reference pack directory
2. Calls `GetResolverPaths()` to collect all DLLs from reference and target directories
3. Creates `PathAssemblyResolver` with collected paths
4. Creates `MetadataLoadContext` with System.Private.CoreLib as core assembly

---

### Method: LoadAssemblies

**Signature:**
```csharp
public IReadOnlyList<Assembly> LoadAssemblies(
    MetadataLoadContext loadContext,
    IReadOnlyList<string> assemblyPaths)
```

**What it does:**
Loads all assemblies into the given context. Deduplicates by assembly identity to avoid loading the same assembly twice. Skips mscorlib as it's automatically loaded by MetadataLoadContext.

**Parameters:**
- `loadContext` - The MetadataLoadContext to load assemblies into
- `assemblyPaths` - List of absolute paths to assembly DLL files

**Returns:**
- `IReadOnlyList<Assembly>` - List of successfully loaded assemblies

**Called by:**
- `ReflectionReader.ReadAssemblies()` - Main entry point for reflection

**How it works:**
1. For each assembly path:
   - Gets assembly name via `AssemblyName.GetAssemblyName(path)` (without loading)
   - Creates identity string: `"Name, Version=X.Y.Z.W"`
   - Skips mscorlib (core assembly)
   - Skips if already loaded (by identity string)
   - Calls `loadContext.LoadFromAssemblyPath(path)`
   - Adds to results list
   - Logs success or errors

---

### Method: LoadClosure

**Signature:**
```csharp
public LoadClosureResult LoadClosure(
    IReadOnlyList<string> seedPaths,
    IReadOnlyList<string> refPaths,
    bool strictVersions = false)
```

**What it does:**
Loads transitive closure of assemblies starting from seed paths. Uses BFS to walk all assembly references and resolve full dependency graph. Returns single MetadataLoadContext with all assemblies loaded. This is the main entry point for full BCL generation.

**Parameters:**
- `seedPaths` - Initial assemblies to load (starting point)
- `refPaths` - Directories to search for referenced assemblies
- `strictVersions` - If true, error on major version drift; otherwise warn

**Returns:**
- `LoadClosureResult` - Contains MetadataLoadContext, loaded assemblies, and resolved paths map

**Called by:**
- Main generation pipeline when generating multiple assemblies with dependencies

**How it works (5 phases):**

**Phase 1: Build candidate map**
- Calls `BuildCandidateMap(refPaths)` to scan reference directories
- Creates map: `AssemblyKey → List<string>` (multiple versions of same assembly)

**Phase 2: BFS closure resolution**
- Calls `ResolveClosure(seedPaths, candidateMap, strictVersions)`
- Uses BFS to walk all assembly references
- Returns map: `AssemblyKey → string` (resolved file path)

**Phase 3: Validate assembly identity**
- Calls `ValidateAssemblyIdentity(resolvedPaths, strictVersions)`
- Guards: PG_LOAD_002 (mixed PublicKeyToken), PG_LOAD_003 (version drift), PG_LOAD_004 (retargetable)

**Phase 4: Find core library**
- Calls `FindCoreLibrary(resolvedPaths)` to locate System.Private.CoreLib
- Required for MetadataLoadContext creation

**Phase 5: Create MetadataLoadContext and load**
- Creates `PathAssemblyResolver` with all resolved paths
- Creates `MetadataLoadContext` with core library name
- Loads all assemblies in dependency order
- Returns `LoadClosureResult` with context, assemblies, and paths

---

### Method: BuildCandidateMap (private)

**Signature:**
```csharp
private Dictionary<AssemblyKey, List<string>> BuildCandidateMap(
    IReadOnlyList<string> refPaths)
```

**What it does:**
Builds map of available assemblies from reference directories. Maps `AssemblyKey` to list of file paths (for version selection).

**Parameters:**
- `refPaths` - List of directories to scan for assemblies

**Returns:**
- `Dictionary<AssemblyKey, List<string>>` - Map from assembly key to candidate paths

**Called by:**
- `LoadClosure()` - Phase 1 of closure resolution

**How it works:**
1. For each reference directory:
   - Checks directory exists
   - Scans for *.dll files
   - Gets AssemblyName via `AssemblyName.GetAssemblyName(dllPath)`
   - Creates `AssemblyKey` from assembly name
   - Adds path to list for that key (multiple versions possible)
   - Logs warnings for inaccessible directories
   - Silently skips unreadable DLLs

**Algorithm:**
- Uses `Dictionary<AssemblyKey, List<string>>` to group paths by key
- Multiple versions of same assembly accumulate in list
- Later, `ResolveClosure()` picks highest version from list

---

### Method: ResolveClosure (private)

**Signature:**
```csharp
private Dictionary<AssemblyKey, string> ResolveClosure(
    IReadOnlyList<string> seedPaths,
    Dictionary<AssemblyKey, List<string>> candidateMap,
    bool strictVersions)
```

**What it does:**
Resolves transitive closure via BFS over assembly references. Returns map of `AssemblyKey` → resolved file path (highest version wins).

**Parameters:**
- `seedPaths` - Initial assemblies to load
- `candidateMap` - Map of available assemblies (from `BuildCandidateMap`)
- `strictVersions` - If true, error on version drift; otherwise warn

**Returns:**
- `Dictionary<AssemblyKey, string>` - Map from assembly key to resolved file path

**Called by:**
- `LoadClosure()` - Phase 2 of closure resolution

**How it works (BFS algorithm):**

**Initialization:**
1. Create queue with seed paths
2. Create `visited` set to track processed assemblies (by AssemblyKey)
3. Create `resolved` map to store final paths (by AssemblyKey)

**BFS Loop:**
1. Dequeue current assembly path
2. Get AssemblyKey from path via `AssemblyName.GetAssemblyName()`
3. Skip if already visited (by key)
4. Mark as visited
5. **Version policy:** If already resolved, keep highest version:
   - Compare current version with existing version
   - If current > existing, replace path and log upgrade
   - Continue to next iteration
6. Add to resolved map
7. **Load metadata to read references:**
   - Open FileStream (read-only)
   - Create PEReader
   - Get MetadataReader
   - Walk `metadataReader.AssemblyReferences`
   - For each reference:
     - Extract name, version, culture, PublicKeyToken
     - Create reference AssemblyKey
     - Look up in candidateMap
     - If not found: PG_LOAD_001 (external reference) - skip silently, will be caught by PhaseGate
     - If found: Pick highest version from candidates (via OrderByDescending)
     - Enqueue for BFS traversal
8. Catch exceptions (log warnings for unreadable assemblies)
9. Continue until queue empty

**Key behaviors:**
- **Version upgrades:** If assembly A v1.0 and v2.0 both referenced, v2.0 wins
- **Missing references:** Silently skipped (PhaseGate validates later)
- **Lightweight loading:** Uses PEReader/MetadataReader (no actual Assembly.Load)

---

### Method: ValidateAssemblyIdentity (private)

**Signature:**
```csharp
private void ValidateAssemblyIdentity(
    Dictionary<AssemblyKey, string> resolvedPaths,
    bool strictVersions)
```

**What it does:**
Validates assembly identity consistency in resolved closure. Implements PhaseGate guards: PG_LOAD_002 (mixed PublicKeyToken), PG_LOAD_003 (version drift), PG_LOAD_004 (retargetable/ContentType).

**Parameters:**
- `resolvedPaths` - Map of resolved assemblies (from `ResolveClosure`)
- `strictVersions` - If true, version drift is ERROR; otherwise WARNING

**Returns:**
- `void` - Emits diagnostics to BuildContext

**Called by:**
- `LoadClosure()` - Phase 3 of closure resolution

**How it works:**

**Guard PG_LOAD_002: Mixed PublicKeyToken**
1. Group resolved assemblies by name
2. For each group:
   - Extract distinct PublicKeyTokens
   - If count > 1: Emit ERROR with diagnostic code `MixedPublicKeyTokenForSameName`
   - Lists all conflicting tokens

**Guard PG_LOAD_003: Version drift**
1. For each assembly name with multiple versions:
   - Parse all versions to Version objects
   - Find max major version and min major version
   - If max != min: Major version drift detected
   - If `strictVersions`: Emit ERROR
   - Otherwise: Emit WARNING
   - Lists all conflicting versions

**Guard PG_LOAD_004: Retargetable/ContentType**
- Placeholder for future implementation
- Requires extending AssemblyKey to track retargetable flag and ContentType

**Diagnostic codes:**
- `DiagnosticCodes.MixedPublicKeyTokenForSameName` - Multiple PKTs for same assembly name
- `DiagnosticCodes.VersionDriftForSameIdentity` - Major version drift detected

---

### Method: FindCoreLibrary (private)

**Signature:**
```csharp
private string FindCoreLibrary(Dictionary<AssemblyKey, string> resolvedPaths)
```

**What it does:**
Finds System.Private.CoreLib in resolved assembly set. This is the core library for MetadataLoadContext.

**Parameters:**
- `resolvedPaths` - Map of resolved assemblies (from `ResolveClosure`)

**Returns:**
- `string` - Absolute path to System.Private.CoreLib.dll

**Throws:**
- `InvalidOperationException` - If System.Private.CoreLib not found

**Called by:**
- `LoadClosure()` - Phase 4 of closure resolution

**How it works:**
1. Filters resolvedPaths for entries where `Key.Name == "System.Private.CoreLib"` (case-insensitive)
2. Extracts file paths from matching entries
3. If count == 0: Throws exception (missing core library)
4. Returns first candidate (should only be one)

---

### Method: GetReferenceAssembliesPath (private)

**Signature:**
```csharp
private string GetReferenceAssembliesPath(IReadOnlyList<string> assemblyPaths)
```

**What it does:**
Gets reference assemblies directory from the first assembly path. Uses the same directory as the assemblies being loaded to ensure version compatibility.

**Parameters:**
- `assemblyPaths` - List of assembly paths (at least one)

**Returns:**
- `string` - Absolute path to reference assemblies directory

**Throws:**
- `InvalidOperationException` - If cannot determine reference directory

**Called by:**
- `CreateLoadContext()` - To find reference pack location

**How it works:**
1. **Primary strategy:** Use directory containing first assembly
   - Gets directory via `Path.GetDirectoryName(assemblyPaths[0])`
   - Verifies directory exists
   - Logs and returns path
2. **Fallback strategy:** Use runtime directory
   - Gets directory via `Path.GetDirectoryName(typeof(object).Assembly.Location)`
   - Verifies directory exists
   - Logs fallback and returns path
3. **Failure:** Throws exception if both strategies fail

**Rationale:**
Using the same directory as target assemblies ensures consistent .NET version for all type resolution (avoids version mismatches).

---

### Method: GetResolverPaths (private)

**Signature:**
```csharp
private IEnumerable<string> GetResolverPaths(
    IReadOnlyList<string> assemblyPaths,
    string referenceAssembliesPath)
```

**What it does:**
Gets all paths that the resolver should search. Deduplicates by assembly name to avoid loading the same assembly twice.

**Parameters:**
- `assemblyPaths` - List of target assembly paths
- `referenceAssembliesPath` - Reference pack directory path

**Returns:**
- `IEnumerable<string>` - Deduplicated list of DLL paths for resolver

**Called by:**
- `CreateLoadContext()` - To configure PathAssemblyResolver

**How it works:**
1. Create dictionary: `pathsByName` (assembly name → file path)
2. **Phase 1:** Scan reference assemblies directory
   - Gets all *.dll files via `Directory.GetFiles()`
   - Extracts name via `Path.GetFileNameWithoutExtension()`
   - Adds to dictionary if not already present (first wins)
3. **Phase 2:** Scan directories containing target assemblies
   - For each assembly path, gets directory
   - Gets all *.dll files in that directory
   - Extracts name and adds to dictionary if not present
4. Returns `pathsByName.Values` (deduplicated paths)

**Deduplication strategy:**
- Uses assembly file name (without extension) as key
- First occurrence wins (reference pack takes precedence)
- Avoids PathAssemblyResolver confusion from duplicate assembly names

---

## File: ReflectionReader.cs

### Purpose

Reads assemblies via reflection and builds the complete `SymbolGraph`. Operates on pure CLR facts—no TypeScript concepts yet. Extracts all public types and their members (methods, properties, fields, events, constructors) with full metadata (accessibility, virtual/override, static, abstract, etc.).

### Class: ReflectionReader

**Fields:**
- `_ctx: BuildContext` - Context for logging and diagnostics
- `_typeFactory: TypeReferenceFactory` - Factory for creating TypeReference instances

**Constructor:**
```csharp
public ReflectionReader(BuildContext ctx)
```
- Stores BuildContext
- Creates TypeReferenceFactory instance

---

### Method: ReadAssemblies

**Signature:**
```csharp
public SymbolGraph ReadAssemblies(
    MetadataLoadContext loadContext,
    IReadOnlyList<string> assemblyPaths)
```

**What it does:**
Main entry point. Reads assemblies and builds the complete `SymbolGraph`. Groups types by namespace, creating `NamespaceSymbol` instances.

**Parameters:**
- `loadContext` - MetadataLoadContext with assemblies ready to load
- `assemblyPaths` - List of assembly paths to process

**Returns:**
- `SymbolGraph` - Complete graph with all namespaces, types, and members

**Called by:**
- Main pipeline entry point (after AssemblyLoader creates context)

**How it works:**
1. **Load assemblies:**
   - Creates `AssemblyLoader` instance
   - Calls `loader.LoadAssemblies(loadContext, assemblyPaths)`
2. **Initialize collections:**
   - `namespaceGroups: Dictionary<string, List<TypeSymbol>>` - Group types by namespace
   - `sourceAssemblies: HashSet<string>` - Track source assembly paths
3. **Process assemblies (sorted by name for determinism):**
   - For each assembly:
     - Add assembly location to sourceAssemblies
     - Log "Reading types from {assembly.Name}..."
     - For each type in assembly:
       - Skip compiler-generated types (via `IsCompilerGenerated()`)
       - Compute accessibility via `ComputeAccessibility(type)`
       - Skip non-public types
       - Call `ReadType(type)` to build TypeSymbol
       - Group by namespace in namespaceGroups
4. **Build namespace symbols:**
   - Sort namespaces alphabetically
   - For each namespace:
     - Create `TypeStableId` for namespace
     - Collect contributing assemblies (distinct)
     - Create `NamespaceSymbol` with types
     - Add to namespaces list
5. **Return SymbolGraph:**
   - Contains `Namespaces` (all namespace symbols)
   - Contains `SourceAssemblies` (set of assembly paths)

**Key behaviors:**
- **Deterministic:** Sorts assemblies and namespaces for reproducible output
- **Public-only:** Filters out non-public types
- **Compiler-generated filtered:** Skips angle-bracket types (`<>c__DisplayClass`, etc.)

---

### Method: ReadType (private)

**Signature:**
```csharp
private TypeSymbol ReadType(Type type)
```

**What it does:**
Converts a `System.Type` to `TypeSymbol`. Reads all type metadata: kind, accessibility, generic parameters, base type, interfaces, members, nested types.

**Parameters:**
- `type` - System.Type from reflection

**Returns:**
- `TypeSymbol` - Complete symbol with all metadata

**Called by:**
- `ReadAssemblies()` - For each public type in assemblies
- `ReadType()` - Recursively for nested types

**How it works:**
1. **Create StableId:**
   - AssemblyName from assembly
   - ClrFullName from type.FullName
   - Intern both strings
2. **Determine type kind:**
   - Call `DetermineTypeKind(type)` → `TypeKind` enum
3. **Compute accessibility:**
   - Call `ComputeAccessibility(type)` → `Accessibility` enum
4. **Read generic parameters:**
   - If `type.IsGenericType`: Get generic arguments
   - Call `_typeFactory.CreateGenericParameterSymbol()` for each
   - Store as `ImmutableArray<GenericParameterSymbol>`
5. **Read base type and interfaces:**
   - `baseType = type.BaseType != null ? _typeFactory.Create(type.BaseType) : null`
   - `interfaces = type.GetInterfaces().Select(_typeFactory.Create)`
6. **Read members:**
   - Call `ReadMembers(type)` → `TypeMembers`
7. **Read nested types:**
   - Get nested types via `type.GetNestedTypes(BindingFlags.Public)`
   - Filter out compiler-generated (via `IsCompilerGenerated()`)
   - Recursively call `ReadType()` for each
8. **Build TypeSymbol:**
   - All CLR metadata: IsValueType, IsAbstract, IsSealed, IsStatic
   - `IsStatic = type.IsAbstract && type.IsSealed && !type.IsValueType` (static classes)
   - Return TypeSymbol

---

### Method: ComputeAccessibility (private static)

**Signature:**
```csharp
private static Accessibility ComputeAccessibility(Type type)
```

**What it does:**
Computes accessibility for a type, correctly handling nested types. For nested types, accessibility is the intersection of the declaring type's accessibility and the nested type's visibility.

**Parameters:**
- `type` - System.Type to check

**Returns:**
- `Accessibility` - Public or Internal

**Called by:**
- `ReadAssemblies()` - To filter public types
- `ReadType()` - To store accessibility

**How it works:**

**Top-level types:**
- `type.IsPublic` → `Accessibility.Public`
- Otherwise → `Accessibility.Internal`

**Nested types:**
- If `type.IsNestedPublic`:
  - Recursively call `ComputeAccessibility(type.DeclaringType!)`
  - If declaring type is Public → return `Accessibility.Public`
  - Otherwise → return `Accessibility.Internal`
- Any other nested visibility (family, assembly, etc.) → `Accessibility.Internal`

**Rationale:**
A nested public type is only truly public if its declaring type is also public. This prevents generating declarations for "publicly visible" nested types inside internal containers.

---

### Method: DetermineTypeKind (private)

**Signature:**
```csharp
private TypeKind DetermineTypeKind(Type type)
```

**What it does:**
Determines the `TypeKind` enum value for a System.Type.

**Parameters:**
- `type` - System.Type to classify

**Returns:**
- `TypeKind` - Enum value (Enum, Interface, Delegate, StaticNamespace, Struct, Class)

**Called by:**
- `ReadType()` - To set TypeSymbol.Kind

**How it works (checked in order):**
1. `type.IsEnum` → `TypeKind.Enum`
2. `type.IsInterface` → `TypeKind.Interface`
3. `type.IsSubclassOf(typeof(Delegate))` or `typeof(MulticastDelegate)` → `TypeKind.Delegate`
4. `type.IsAbstract && type.IsSealed && !type.IsValueType` → `TypeKind.StaticNamespace` (static classes)
5. `type.IsValueType` → `TypeKind.Struct`
6. Otherwise → `TypeKind.Class`

---

### Method: ReadMembers (private)

**Signature:**
```csharp
private TypeMembers ReadMembers(Type type)
```

**What it does:**
Reads all public members from a type: methods, properties, fields, events, constructors. Uses `BindingFlags.DeclaredOnly` to avoid reading inherited members.

**Parameters:**
- `type` - System.Type to read members from

**Returns:**
- `TypeMembers` - Record with all member collections

**Called by:**
- `ReadType()` - To populate TypeSymbol.Members

**How it works:**
1. **Initialize collections:**
   - `methods: List<MethodSymbol>`
   - `properties: List<PropertySymbol>`
   - `fields: List<FieldSymbol>`
   - `events: List<EventSymbol>`
   - `constructors: List<ConstructorSymbol>`
2. **Define binding flags:**
   - `publicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly`
   - `publicStatic = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly`
3. **Read methods:**
   - Get via `type.GetMethods(publicInstance | publicStatic)`
   - Skip special names (property/event accessors)
   - Track by `methodKey = "{method.Name}|{method.MetadataToken}"` to detect duplicates
   - Call `ReadMethod(method, type)` for each
   - Check for duplicate StableIds (log ERROR and skip)
   - Add to methods list
4. **Read properties:**
   - Get via `type.GetProperties(publicInstance | publicStatic)`
   - Call `ReadProperty(property, type)` for each
5. **Read fields:**
   - Get via `type.GetFields(publicInstance | publicStatic)`
   - Call `ReadField(field, type)` for each
6. **Read events:**
   - Get via `type.GetEvents(publicInstance | publicStatic)`
   - Call `ReadEvent(evt, type)` for each
7. **Read constructors:**
   - Get via `type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)`
   - Call `ReadConstructor(ctor, type)` for each
8. **Return TypeMembers:**
   - Convert all lists to ImmutableArrays

**Key behaviors:**
- **DeclaredOnly:** Only reads members declared on this type (not inherited)
- **Duplicate detection:** Prevents reflection bugs from creating duplicate members
- **Special names skipped:** Property/event accessors excluded (special names)

---

### Method: ReadMethod (private)

**Signature:**
```csharp
private MethodSymbol ReadMethod(MethodInfo method, Type declaringType)
```

**What it does:**
Converts a `MethodInfo` to `MethodSymbol`. Handles explicit interface implementations (qualified names like `System.Collections.ICollection.SyncRoot`).

**Parameters:**
- `method` - MethodInfo from reflection
- `declaringType` - Type that declares this method

**Returns:**
- `MethodSymbol` - Complete method symbol

**Called by:**
- `ReadMembers()` - For each public method

**How it works:**
1. **Detect explicit interface implementation:**
   - `clrName = method.Name`
   - If `clrName.Contains('.')`:
     - Explicit interface implementation (e.g., "System.IDisposable.Dispose")
     - Use qualified name for both ClrName and MemberName
   - Otherwise: Use simple name
2. **Create MemberStableId:**
   - AssemblyName from declaringType
   - DeclaringClrFullName from declaringType
   - MemberName (qualified for explicit interface implementations)
   - CanonicalSignature from `CreateMethodSignature(method)`
   - MetadataToken from method
3. **Read parameters:**
   - Get via `method.GetParameters()`
   - Call `ReadParameter()` for each
4. **Read generic parameters:**
   - If `method.IsGenericMethod`: Get generic arguments
   - Call `_typeFactory.CreateGenericParameterSymbol()` for each
5. **Build MethodSymbol:**
   - ReturnType via `_typeFactory.Create(method.ReturnType)`
   - IsStatic, IsAbstract, IsVirtual, IsSealed from method
   - IsOverride via `IsMethodOverride(method)`
   - Visibility via `GetVisibility(method)`
   - Provenance = `MemberProvenance.Original`
   - EmitScope = `EmitScope.ClassSurface` (all reflected members start on class)

---

### Method: ReadProperty (private)

**Signature:**
```csharp
private PropertySymbol ReadProperty(PropertyInfo property, Type declaringType)
```

**What it does:**
Converts a `PropertyInfo` to `PropertySymbol`. Handles explicit interface implementations and indexers.

**Parameters:**
- `property` - PropertyInfo from reflection
- `declaringType` - Type that declares this property

**Returns:**
- `PropertySymbol` - Complete property symbol

**Called by:**
- `ReadMembers()` - For each public property

**How it works:**
1. **Detect explicit interface implementation:**
   - Same logic as `ReadMethod()` (check for '.' in name)
2. **Create MemberStableId:**
   - CanonicalSignature from `CreatePropertySignature(property)`
3. **Read index parameters:**
   - Get via `property.GetIndexParameters()`
   - Call `ReadParameter()` for each
4. **Read accessors:**
   - `getter = property.GetGetMethod()`
   - `setter = property.GetSetMethod()`
5. **Build PropertySymbol:**
   - PropertyType via `_typeFactory.Create(property.PropertyType)`
   - IndexParameters (for indexers)
   - HasGetter, HasSetter (bool flags)
   - IsStatic, IsVirtual, IsAbstract from getter/setter (whichever exists)
   - IsOverride via `IsMethodOverride(getter)` if getter exists
   - Visibility via `GetPropertyVisibility(property)`
   - Provenance = `MemberProvenance.Original`
   - EmitScope = `EmitScope.ClassSurface`

---

### Method: ReadField (private)

**Signature:**
```csharp
private FieldSymbol ReadField(FieldInfo field, Type declaringType)
```

**What it does:**
Converts a `FieldInfo` to `FieldSymbol`.

**Parameters:**
- `field` - FieldInfo from reflection
- `declaringType` - Type that declares this field

**Returns:**
- `FieldSymbol` - Complete field symbol

**Called by:**
- `ReadMembers()` - For each public field

**How it works:**
1. **Create MemberStableId:**
   - CanonicalSignature = field type's FullName
2. **Build FieldSymbol:**
   - FieldType via `_typeFactory.Create(field.FieldType)`
   - IsStatic, IsReadOnly (`field.IsInitOnly`), IsConst (`field.IsLiteral`)
   - ConstValue via `field.GetRawConstantValue()` if IsConst
   - Visibility via `GetFieldVisibility(field)`
   - Provenance = `MemberProvenance.Original`
   - EmitScope = `EmitScope.ClassSurface`

---

### Method: ReadEvent (private)

**Signature:**
```csharp
private EventSymbol ReadEvent(EventInfo evt, Type declaringType)
```

**What it does:**
Converts an `EventInfo` to `EventSymbol`. Handles explicit interface implementations.

**Parameters:**
- `evt` - EventInfo from reflection
- `declaringType` - Type that declares this event

**Returns:**
- `EventSymbol` - Complete event symbol

**Called by:**
- `ReadMembers()` - For each public event

**How it works:**
1. **Detect explicit interface implementation:**
   - Same logic as `ReadMethod()` (check for '.' in name)
2. **Create MemberStableId:**
   - CanonicalSignature = event handler type's FullName
3. **Read add method:**
   - `addMethod = evt.GetAddMethod()`
4. **Build EventSymbol:**
   - EventHandlerType via `_typeFactory.Create(evt.EventHandlerType!)`
   - IsStatic, IsVirtual from addMethod
   - IsOverride via `IsMethodOverride(addMethod)` if exists
   - Visibility via `GetEventVisibility(evt)`
   - Provenance = `MemberProvenance.Original`
   - EmitScope = `EmitScope.ClassSurface`

---

### Method: ReadConstructor (private)

**Signature:**
```csharp
private ConstructorSymbol ReadConstructor(ConstructorInfo ctor, Type declaringType)
```

**What it does:**
Converts a `ConstructorInfo` to `ConstructorSymbol`.

**Parameters:**
- `ctor` - ConstructorInfo from reflection
- `declaringType` - Type that declares this constructor

**Returns:**
- `ConstructorSymbol` - Complete constructor symbol

**Called by:**
- `ReadMembers()` - For each public constructor

**How it works:**
1. **Create MemberStableId:**
   - MemberName = ".ctor"
   - CanonicalSignature from `CreateConstructorSignature(ctor)`
2. **Read parameters:**
   - Get via `ctor.GetParameters()`
   - Call `ReadParameter()` for each
3. **Build ConstructorSymbol:**
   - Parameters array
   - IsStatic (for static constructors)
   - Visibility via `GetConstructorVisibility(ctor)`

---

### Method: ReadParameter (private)

**Signature:**
```csharp
private ParameterSymbol ReadParameter(ParameterInfo param)
```

**What it does:**
Converts a `ParameterInfo` to `ParameterSymbol`. Sanitizes parameter name for TypeScript reserved words.

**Parameters:**
- `param` - ParameterInfo from reflection

**Returns:**
- `ParameterSymbol` - Complete parameter symbol

**Called by:**
- `ReadMethod()`, `ReadProperty()`, `ReadConstructor()` - For each parameter

**How it works:**
1. **Get parameter name:**
   - `paramName = param.Name ?? $"arg{param.Position}"` (fallback for unnamed)
2. **Sanitize for TypeScript:**
   - Call `TypeScriptReservedWords.SanitizeParameterName(paramName)`
   - Renames reserved words (e.g., "function" → "function_")
3. **Build ParameterSymbol:**
   - Name (interned and sanitized)
   - Type via `_typeFactory.Create(param.ParameterType)`
   - IsRef = `param.ParameterType.IsByRef && !param.IsOut`
   - IsOut = `param.IsOut`
   - IsParams = check for ParamArrayAttribute
   - HasDefaultValue, DefaultValue from param

---

### Method: CreateMethodSignature (private)

**Signature:**
```csharp
private string CreateMethodSignature(MethodInfo method)
```

**What it does:**
Creates canonical signature string for method identity.

**Parameters:**
- `method` - MethodInfo from reflection

**Returns:**
- `string` - Canonical signature (interned)

**Called by:**
- `ReadMethod()` - For MemberStableId.CanonicalSignature

**How it works:**
1. Extract parameter types: `method.GetParameters().Select(p => p.ParameterType.FullName)`
2. Extract return type: `method.ReturnType.FullName`
3. Call `_ctx.CanonicalizeMethod(method.Name, paramTypes, returnType)`
4. Return interned string

---

### Method: CreatePropertySignature (private)

**Signature:**
```csharp
private string CreatePropertySignature(PropertyInfo property)
```

**What it does:**
Creates canonical signature string for property identity.

**Parameters:**
- `property` - PropertyInfo from reflection

**Returns:**
- `string` - Canonical signature (interned)

**Called by:**
- `ReadProperty()` - For MemberStableId.CanonicalSignature

**How it works:**
1. Extract index parameter types: `property.GetIndexParameters().Select(p => p.ParameterType.FullName)`
2. Extract property type: `property.PropertyType.FullName`
3. Call `_ctx.CanonicalizeProperty(property.Name, indexTypes, propType)`
4. Return interned string

---

### Method: CreateConstructorSignature (private)

**Signature:**
```csharp
private string CreateConstructorSignature(ConstructorInfo ctor)
```

**What it does:**
Creates canonical signature string for constructor identity.

**Parameters:**
- `ctor` - ConstructorInfo from reflection

**Returns:**
- `string` - Canonical signature (interned)

**Called by:**
- `ReadConstructor()` - For MemberStableId.CanonicalSignature

**How it works:**
1. Extract parameter types: `ctor.GetParameters().Select(p => p.ParameterType.FullName)`
2. Call `_ctx.CanonicalizeMethod(".ctor", paramTypes, "void")`
3. Return interned string

---

### Method: GetVisibility (private)

**Signature:**
```csharp
private Visibility GetVisibility(MethodInfo method)
```

**What it does:**
Converts MethodInfo accessibility to Visibility enum.

**Parameters:**
- `method` - MethodInfo from reflection

**Returns:**
- `Visibility` - Enum value (Public, Protected, ProtectedInternal, PrivateProtected, Internal, Private)

**Called by:**
- `ReadMethod()` - To set MethodSymbol.Visibility

**How it works (checked in order):**
1. `method.IsPublic` → `Visibility.Public`
2. `method.IsFamily` → `Visibility.Protected`
3. `method.IsFamilyOrAssembly` → `Visibility.ProtectedInternal`
4. `method.IsFamilyAndAssembly` → `Visibility.PrivateProtected`
5. `method.IsAssembly` → `Visibility.Internal`
6. Otherwise → `Visibility.Private`

---

### Method: GetPropertyVisibility (private)

**Signature:**
```csharp
private Visibility GetPropertyVisibility(PropertyInfo property)
```

**What it does:**
Converts PropertyInfo accessibility to Visibility enum (uses getter or setter visibility).

**Parameters:**
- `property` - PropertyInfo from reflection

**Returns:**
- `Visibility` - Enum value

**Called by:**
- `ReadProperty()` - To set PropertySymbol.Visibility

**How it works:**
1. Get getter via `property.GetGetMethod(true)` (include non-public)
2. Get setter via `property.GetSetMethod(true)`
3. Use getter ?? setter (whichever exists)
4. Call `GetVisibility(method)` if method exists
5. Return `Visibility.Private` if neither exists

---

### Method: GetFieldVisibility (private)

**Signature:**
```csharp
private Visibility GetFieldVisibility(FieldInfo field)
```

**What it does:**
Converts FieldInfo accessibility to Visibility enum.

**Parameters:**
- `field` - FieldInfo from reflection

**Returns:**
- `Visibility` - Enum value

**Called by:**
- `ReadField()` - To set FieldSymbol.Visibility

**How it works:**
Same logic as `GetVisibility()` but for FieldInfo flags.

---

### Method: GetEventVisibility (private)

**Signature:**
```csharp
private Visibility GetEventVisibility(EventInfo evt)
```

**What it does:**
Converts EventInfo accessibility to Visibility enum (uses add method visibility).

**Parameters:**
- `evt` - EventInfo from reflection

**Returns:**
- `Visibility` - Enum value

**Called by:**
- `ReadEvent()` - To set EventSymbol.Visibility

**How it works:**
1. Get add method via `evt.GetAddMethod(true)` (include non-public)
2. Call `GetVisibility(addMethod)` if exists
3. Return `Visibility.Private` if no add method

---

### Method: IsMethodOverride (private static)

**Signature:**
```csharp
private static bool IsMethodOverride(MethodInfo method)
```

**What it does:**
Checks if a method is an override (vs new virtual or original virtual). Uses MethodAttributes flags which work with MetadataLoadContext. Overrides are virtual and do NOT have NewSlot set (they reuse vtable slot).

**Parameters:**
- `method` - MethodInfo from reflection

**Returns:**
- `bool` - True if method is an override

**Called by:**
- `ReadMethod()`, `ReadProperty()`, `ReadEvent()` - To set IsOverride flag

**How it works:**
```csharp
return method.IsVirtual && !method.Attributes.HasFlag(MethodAttributes.NewSlot);
```

**Explanation:**
- `IsVirtual` - Method participates in virtual dispatch
- `NewSlot` - Method introduces new vtable slot (new virtual or hiding member)
- Override = virtual method that reuses parent's vtable slot

---

### Method: GetConstructorVisibility (private)

**Signature:**
```csharp
private Visibility GetConstructorVisibility(ConstructorInfo ctor)
```

**What it does:**
Converts ConstructorInfo accessibility to Visibility enum.

**Parameters:**
- `ctor` - ConstructorInfo from reflection

**Returns:**
- `Visibility` - Enum value

**Called by:**
- `ReadConstructor()` - To set ConstructorSymbol.Visibility

**How it works:**
Same logic as `GetVisibility()` but for ConstructorInfo flags.

---

### Method: IsCompilerGenerated (private static)

**Signature:**
```csharp
private static bool IsCompilerGenerated(string typeName)
```

**What it does:**
Checks if a type name indicates compiler-generated code. Compiler-generated types have unspeakable names containing `<` or `>`.

**Parameters:**
- `typeName` - Name of the type

**Returns:**
- `bool` - True if compiler-generated

**Called by:**
- `ReadAssemblies()` - To skip compiler-generated types
- `ReadType()` - To filter nested types

**How it works:**
```csharp
return typeName.Contains('<') || typeName.Contains('>');
```

**Examples of compiler-generated types:**
- `<Module>` - Module initializer
- `<PrivateImplementationDetails>` - Private implementation details
- `<Name>e__FixedBuffer` - Fixed buffer struct
- `<>c__DisplayClass` - Lambda closure class
- `<>d__Iterator` - Iterator state machine
- `<>f__AnonymousType` - Anonymous type

---

## File: InterfaceMemberSubstitution.cs

### Purpose

Substitutes generic type parameters in interface members for closed generic interfaces. For `IComparable<T>.CompareTo(T)` implemented as `IComparable<int>`, substitutes `T → int`. Creates closed member surfaces used by interface flattening, structural conformance, and explicit views. **Note:** This file only builds the substitution maps—actual member substitution is performed by Shape phase components.

### Class: InterfaceMemberSubstitution (static)

---

### Method: SubstituteClosedInterfaces

**Signature:**
```csharp
public static void SubstituteClosedInterfaces(BuildContext ctx, SymbolGraph graph)
```

**What it does:**
Processes all types in the graph, building substitution maps for closed generic interfaces. The actual substituted members will be used by Shape phase components (InterfaceInliner, StructuralConformance, ViewPlanner).

**Parameters:**
- `ctx` - BuildContext for logging
- `graph` - SymbolGraph with all types and members

**Returns:**
- `void` - Builds internal substitution maps for later use

**Called by:**
- Main pipeline (Load phase) - After ReflectionReader completes

**How it works:**
1. **Log start:**
   - "Building closed interface member maps..."
2. **Build interface index:**
   - Call `BuildInterfaceIndex(graph)` → `Dictionary<string, TypeSymbol>`
   - Maps interface ClrFullName to TypeSymbol
3. **Process all types:**
   - For each namespace in graph:
     - Log progress every 10 namespaces
     - For each type in namespace:
       - Call `ProcessType(ctx, type, interfaceIndex)`
       - Accumulate total substitution count
4. **Log completion:**
   - "Created {totalSubstitutions} interface member mappings"

**Note:** The substitution maps are built but not stored in the graph. Shape phase components will rebuild them as needed using `BuildSubstitutionMap()` and `SubstituteTypeReference()`.

---

### Method: BuildInterfaceIndex (private static)

**Signature:**
```csharp
private static Dictionary<string, TypeSymbol> BuildInterfaceIndex(SymbolGraph graph)
```

**What it does:**
Builds index of all interface types in graph, mapping ClrFullName to TypeSymbol.

**Parameters:**
- `graph` - SymbolGraph to index

**Returns:**
- `Dictionary<string, TypeSymbol>` - Map from interface full name to symbol

**Called by:**
- `SubstituteClosedInterfaces()` - Phase 1

**How it works:**
1. Create empty dictionary
2. For each namespace in graph:
   - For each type in namespace:
     - If `type.Kind == TypeKind.Interface`:
       - Add to dictionary: `index[type.ClrFullName] = type`
3. Return index

**Purpose:**
Fast lookup of interface definitions needed for substitution.

---

### Method: ProcessType (private static)

**Signature:**
```csharp
private static int ProcessType(
    BuildContext ctx,
    TypeSymbol type,
    Dictionary<string, TypeSymbol> interfaceIndex)
```

**What it does:**
Processes one type, building substitution maps for all closed generic interfaces it implements.

**Parameters:**
- `ctx` - BuildContext for logging
- `type` - Type to process
- `interfaceIndex` - Index of interface definitions

**Returns:**
- `int` - Count of substitution maps created

**Called by:**
- `SubstituteClosedInterfaces()` - For each type

**How it works:**
1. If type has no interfaces, return 0
2. For each interface reference in `type.Interfaces`:
   - Check if it's a closed generic interface:
     - Cast to `NamedTypeReference`
     - Check `TypeArguments.Count > 0`
   - If closed generic:
     - Extract generic definition name via `GetGenericDefinitionName()`
     - Look up interface definition in index
     - If found:
       - Call `BuildSubstitutionMap(ifaceSymbol, namedRef)`
       - Increment substitution count
3. Return total substitution count

**Example:**
Type `List<int>` implements `ICollection<int>`:
- `ifaceRef` is `NamedTypeReference` for `ICollection<int>`
- Generic definition name is `System.Collections.Generic.ICollection`1`
- Look up `ICollection<T>` definition in index
- Build substitution map: `T → int`

---

### Method: BuildSubstitutionMap (private static)

**Signature:**
```csharp
private static Dictionary<string, TypeReference> BuildSubstitutionMap(
    TypeSymbol interfaceSymbol,
    NamedTypeReference closedInterfaceRef)
```

**What it does:**
Builds substitution map from generic parameter names to type arguments for a closed generic interface.

**Parameters:**
- `interfaceSymbol` - Generic interface definition (e.g., `IComparable<T>`)
- `closedInterfaceRef` - Closed interface reference (e.g., `IComparable<int>`)

**Returns:**
- `Dictionary<string, TypeReference>` - Map from parameter name to argument type

**Called by:**
- `ProcessType()` - For each closed generic interface

**How it works:**
1. Create empty map
2. Validate arity matches:
   - `interfaceSymbol.GenericParameters.Length == closedInterfaceRef.TypeArguments.Count`
   - If mismatch: Return empty map
3. For each generic parameter index:
   - Get parameter from interfaceSymbol (e.g., "T")
   - Get argument from closedInterfaceRef (e.g., TypeReference for "int")
   - Add to map: `map[param.Name] = arg`
4. Return map

**Example:**
- Interface: `IComparable<T>` (parameter "T")
- Closed: `IComparable<int>` (argument TypeReference for "int")
- Map: `{ "T" → TypeReference(int) }`

---

### Method: SubstituteTypeReference

**Signature:**
```csharp
public static TypeReference SubstituteTypeReference(
    TypeReference original,
    Dictionary<string, TypeReference> substitutionMap)
```

**What it does:**
Substitutes type parameters in a type reference using the given substitution map. This is used by Shape phase components when they need to create substituted member signatures.

**Parameters:**
- `original` - Original type reference (may contain generic parameters)
- `substitutionMap` - Map from parameter name to replacement type

**Returns:**
- `TypeReference` - New type reference with substitutions applied

**Called by:**
- Shape phase components (InterfaceInliner, StructuralConformance, ViewPlanner)

**How it works (recursive pattern matching):**

**Case 1: GenericParameterReference**
- If parameter name is in substitution map:
  - Return the mapped type reference
- Otherwise: Return original (unsubstituted)

**Case 2: ArrayTypeReference**
- Recursively substitute element type
- Return new ArrayTypeReference with substituted element type and same rank

**Case 3: PointerTypeReference**
- Recursively substitute pointee type
- Return new PointerTypeReference with substituted pointee type and same depth

**Case 4: ByRefTypeReference**
- Recursively substitute referenced type
- Return new ByRefTypeReference with substituted referenced type

**Case 5: NamedTypeReference with type arguments**
- Recursively substitute each type argument
- Return new NamedTypeReference with:
  - Same AssemblyName, Namespace, Name, FullName, Arity, IsValueType
  - Substituted type arguments list

**Case 6: Other (no substitution needed)**
- Return original (primitives, non-generic named types, etc.)

**Example:**
- Original: `Array<T>` (ArrayTypeReference with GenericParameterReference("T"))
- Substitution map: `{ "T" → TypeReference(int) }`
- Result: `Array<int>` (ArrayTypeReference with TypeReference(int))

---

### Method: GetGenericDefinitionName (private static)

**Signature:**
```csharp
private static string GetGenericDefinitionName(string fullName)
```

**What it does:**
Converts closed generic type name to generic definition name. Handles both angle brackets (TypeScript) and backtick notation (CLR).

**Parameters:**
- `fullName` - Full name of closed generic type

**Returns:**
- `string` - Generic definition name with arity

**Called by:**
- `ProcessType()` - To look up interface definition

**How it works:**
1. **Check for backtick:**
   - Find index of '`' character
   - If found:
     - Extract arity digits after backtick
     - Return substring from start to end of arity
2. **No backtick found:**
   - Return fullName unchanged (might already be definition name)

**Examples:**
- Input: `System.IComparable<int>` → Output: `System.IComparable`1` (extracts arity from CLR name)
- Input: `System.IComparable`1` → Output: `System.IComparable`1` (already definition)
- Input: `System.String` → Output: `System.String` (not generic)

---

## File: TypeReferenceFactory.cs

### Purpose

Converts `System.Type` to our `TypeReference` model. Handles all type constructs: named, generic, array, pointer, byref, nested. Uses memoization with cycle detection to prevent stack overflow on recursive constraints (e.g., `IComparable<T> where T : IComparable<T>`).

### Class: TypeReferenceFactory

**Fields:**
- `_ctx: BuildContext` - Context for logging and string interning
- `_cache: Dictionary<Type, TypeReference>` - Memoization cache
- `_inProgress: HashSet<Type>` - Cycle detection set

**Constructor:**
```csharp
public TypeReferenceFactory(BuildContext ctx)
```
- Stores BuildContext

---

### Method: Create

**Signature:**
```csharp
public TypeReference Create(Type type)
```

**What it does:**
Converts a `System.Type` to `TypeReference`. Memoized with cycle detection to prevent infinite recursion.

**Parameters:**
- `type` - System.Type from reflection

**Returns:**
- `TypeReference` - Immutable type reference model

**Called by:**
- ReflectionReader methods (ReadType, ReadMethod, ReadProperty, ReadField, etc.)
- Recursively by itself for nested types

**How it works:**

**Step 1: Check cache**
- If type already converted, return cached result
- Early exit for performance

**Step 2: Detect cycle**
- If type is in `_inProgress` set:
  - Recursive constraint detected (e.g., `IComparable<T> where T : IComparable<T>`)
  - Return `PlaceholderTypeReference` with debug name
  - Breaks recursion cycle

**Step 3: Mark as in-progress**
- Add type to `_inProgress` set
- Protected by try-finally to ensure cleanup

**Step 4: Convert type**
- Call `CreateInternal(type)` to perform actual conversion
- Cache result

**Step 5: Cleanup**
- Remove type from `_inProgress` set
- Return result

---

### Method: CreateInternal (private)

**Signature:**
```csharp
private TypeReference CreateInternal(Type type)
```

**What it does:**
Performs actual type conversion. Dispatches to appropriate handler based on type kind.

**Parameters:**
- `type` - System.Type to convert

**Returns:**
- `TypeReference` - Appropriate subclass

**Called by:**
- `Create()` - After cache miss

**How it works (checked in order):**

**Case 1: ByRef types**
- `type.IsByRef` → Return `ByRefTypeReference`
- Element type via `type.GetElementType()`
- Recursively call `Create()` for element type

**Case 2: Pointer types**
- `type.IsPointer` → Return `PointerTypeReference`
- Count pointer depth (e.g., `int***` has depth 3)
- Walk element types until non-pointer reached
- Recursively call `Create()` for final element type

**Case 3: Array types**
- `type.IsArray` → Return `ArrayTypeReference`
- Element type via `type.GetElementType()`
- Rank via `type.GetArrayRank()` (1 for `T[]`, 2 for `T[,]`, etc.)
- Recursively call `Create()` for element type

**Case 4: Generic parameters**
- `type.IsGenericParameter` → Call `CreateGenericParameter(type)`

**Case 5: Named types**
- All other types (class, struct, interface, enum, delegate)
- Call `CreateNamed(type)`

---

### Method: CreateNamed (private)

**Signature:**
```csharp
private TypeReference CreateNamed(Type type)
```

**What it does:**
Creates `NamedTypeReference` for class, struct, interface, enum, or delegate.

**Parameters:**
- `type` - System.Type to convert

**Returns:**
- `NamedTypeReference` - Complete named type reference

**Called by:**
- `CreateInternal()` - For named types

**How it works:**

**Step 1: Extract basic metadata**
- `assemblyName = type.Assembly.GetName().Name ?? "Unknown"`
- **CRITICAL - Open generic form for constructed generics:**
  ```csharp
  var fullName = type.IsGenericType && type.IsConstructedGenericType
      ? type.GetGenericTypeDefinition().FullName ?? type.Name
      : type.FullName ?? type.Name;
  ```
  - For constructed generics (e.g., `IEquatable<StandardFormat>`), use open generic form
  - Open form: `"System.IEquatable\`1"` (clean, backtick arity only)
  - Constructed form: `"System.IEquatable\`1[[System.Buffers.StandardFormat, ...]]"` (has assembly-qualified type args)
  - **Why needed:** Constructed form breaks StableId lookup and causes import garbage bugs
  - **Related:** ImportGraph.GetOpenGenericClrKey() uses same logic to prevent assembly-qualified type arg pollution
- `namespaceName = type.Namespace ?? ""`
- `name = type.Name`

**Step 2: HARDENING - Guarantee Name is never empty**
- If `name` is null/empty/whitespace:
  - If `fullName` is valid:
    - Extract last segment after '.' or '+' (for nested types)
    - Example: `"System.Foo.Bar+Nested"` → `"Nested"`
  - Otherwise:
    - Use synthetic name: `"UnknownType"`
    - Log warning

**Step 3: Handle generic types**
- Initialize `arity = 0` and `typeArgs = []`
- If `type.IsGenericType`:
  - Get arity via `type.GetGenericArguments().Length`
  - If `type.IsConstructedGenericType`:
    - For each generic argument:
      - Recursively call `Create()` to convert
      - Add to typeArgs list

**Step 4: HARDENING - Stamp interface StableId at load time**
- If `type.IsInterface`:
  - Format: `"{assemblyName}:{fullName}"` (same as ScopeFactory.GetInterfaceStableId)
  - Intern the string
  - Store in `interfaceStableId` field
- Purpose: Eliminates repeated computation and graph lookups in later phases

**Step 5: Build NamedTypeReference**
- All strings interned via `_ctx.Intern()`
- Return NamedTypeReference with all fields populated

---

### Method: CreateGenericParameter (private)

**Signature:**
```csharp
private TypeReference CreateGenericParameter(Type type)
```

**What it does:**
Creates `GenericParameterReference` for a generic type parameter (e.g., `T` in `List<T>`).

**Parameters:**
- `type` - System.Type that is a generic parameter

**Returns:**
- `GenericParameterReference` - Generic parameter reference

**Called by:**
- `CreateInternal()` - For generic parameters

**How it works:**

**Step 1: Extract declaring context**
- `declaringType = type.DeclaringType ?? type.DeclaringMethod?.DeclaringType`
- `declaringName = declaringType?.FullName ?? "Unknown"`

**Step 2: Create GenericParameterId**
- `DeclaringTypeName` - Interned declaring type full name
- `Position` - Position in generic parameter list
- `IsMethodParameter` - True if declared on method (vs type)

**Step 3: IMPORTANT - Constraints are NOT resolved here**
- To avoid infinite recursion on recursive constraints like `IComparable<T> where T : IComparable<T>`
- `Constraints` field is left empty
- ConstraintCloser (Shape phase) will resolve constraints later

**Step 4: Build GenericParameterReference**
- `Id` - Generic parameter identity
- `Name` - Interned parameter name (e.g., "T")
- `Position` - Position in parameter list
- `Constraints` - Empty list (filled by ConstraintCloser)

---

### Method: CreateGenericParameterSymbol

**Signature:**
```csharp
public GenericParameterSymbol CreateGenericParameterSymbol(Type type)
```

**What it does:**
Creates `GenericParameterSymbol` from a System.Type. Stores variance and special constraints; ConstraintCloser resolves type constraints later.

**Parameters:**
- `type` - System.Type that is a generic parameter

**Returns:**
- `GenericParameterSymbol` - Complete generic parameter symbol

**Throws:**
- `ArgumentException` - If type is not a generic parameter

**Called by:**
- ReflectionReader.ReadType() - For type generic parameters
- ReflectionReader.ReadMethod() - For method generic parameters

**How it works:**

**Step 1: Validate**
- If `!type.IsGenericParameter`: Throw ArgumentException

**Step 2: Extract declaring context**
- Same as `CreateGenericParameter()`

**Step 3: Create GenericParameterId**
- Same as `CreateGenericParameter()`

**Step 4: Extract variance**
- Get `GenericParameterAttributes` via `type.GenericParameterAttributes`
- If `Covariant` flag set → `Variance.Covariant` (e.g., `out T` in `IEnumerable<out T>`)
- If `Contravariant` flag set → `Variance.Contravariant` (e.g., `in T` in `Action<in T>`)
- Otherwise → `Variance.None`

**Step 5: Extract special constraints**
- `ReferenceTypeConstraint` → `GenericParameterConstraints.ReferenceType` (class constraint)
- `NotNullableValueTypeConstraint` → `GenericParameterConstraints.ValueType` (struct constraint)
- `DefaultConstructorConstraint` → `GenericParameterConstraints.DefaultConstructor` (new() constraint)
- Combine with bitwise OR (flags enum)

**Step 6: Store raw constraint types**
- Call `type.GetGenericParameterConstraints()` to get raw System.Type[] array
- Store in `RawConstraintTypes` field
- ConstraintCloser will convert these to TypeReferences during Shape phase
- Prevents infinite recursion on recursive constraints

**Step 7: Build GenericParameterSymbol**
- `Id` - Generic parameter identity
- `Name` - Interned parameter name
- `Position` - Position in parameter list
- `Constraints` - Empty (filled by ConstraintCloser)
- `RawConstraintTypes` - Raw System.Type[] for ConstraintCloser
- `Variance` - Covariant/Contravariant/None
- `SpecialConstraints` - Flags for class/struct/new() constraints

---

### Method: ClearCache

**Signature:**
```csharp
public void ClearCache()
```

**What it does:**
Clears the memoization cache (for testing).

**Parameters:**
- None

**Returns:**
- `void`

**Called by:**
- Test code

**How it works:**
- Calls `_cache.Clear()`

---

## Call Flow

### High-Level Flow

```
Main Pipeline
  ↓
AssemblyLoader.LoadClosure(seedPaths, refPaths, strictVersions)
  ↓ (creates MetadataLoadContext)
  ↓
ReflectionReader.ReadAssemblies(loadContext, assemblyPaths)
  ↓
SymbolGraph (output)
  ↓
InterfaceMemberSubstitution.SubstituteClosedInterfaces(ctx, graph)
  ↓
SymbolGraph (with substitution maps built)
```

---

### Detailed Call Flow: AssemblyLoader.LoadClosure

```
LoadClosure(seedPaths, refPaths, strictVersions)
  │
  ├─► BuildCandidateMap(refPaths)
  │     └─► Scans directories for *.dll
  │     └─► Returns: Dictionary<AssemblyKey, List<string>>
  │
  ├─► ResolveClosure(seedPaths, candidateMap, strictVersions)
  │     ├─► BFS loop over assembly references
  │     ├─► Uses PEReader/MetadataReader for lightweight loading
  │     ├─► Version policy: highest version wins
  │     └─► Returns: Dictionary<AssemblyKey, string>
  │
  ├─► ValidateAssemblyIdentity(resolvedPaths, strictVersions)
  │     ├─► Guard PG_LOAD_002: Check for mixed PublicKeyToken
  │     ├─► Guard PG_LOAD_003: Check for version drift
  │     └─► Guard PG_LOAD_004: Placeholder (retargetable/ContentType)
  │
  ├─► FindCoreLibrary(resolvedPaths)
  │     └─► Finds System.Private.CoreLib
  │
  └─► Create MetadataLoadContext
        ├─► PathAssemblyResolver with all resolved paths
        └─► Load all assemblies
        └─► Return LoadClosureResult
```

---

### Detailed Call Flow: ReflectionReader.ReadAssemblies

```
ReadAssemblies(loadContext, assemblyPaths)
  │
  ├─► AssemblyLoader.LoadAssemblies(loadContext, assemblyPaths)
  │     └─► Returns: List<Assembly>
  │
  ├─► For each assembly (sorted):
  │     ├─► For each type in assembly:
  │     │     ├─► Skip if IsCompilerGenerated(type.Name)
  │     │     ├─► ComputeAccessibility(type)
  │     │     ├─► Skip if not public
  │     │     ├─► ReadType(type)
  │     │     └─► Group by namespace
  │     │
  │     └─► ReadType(type)
  │           ├─► DetermineTypeKind(type)
  │           ├─► ComputeAccessibility(type)
  │           ├─► TypeReferenceFactory.CreateGenericParameterSymbol() for each generic param
  │           ├─► TypeReferenceFactory.Create(type.BaseType)
  │           ├─► TypeReferenceFactory.Create() for each interface
  │           ├─► ReadMembers(type)
  │           │     ├─► ReadMethod() for each method
  │           │     ├─► ReadProperty() for each property
  │           │     ├─► ReadField() for each field
  │           │     ├─► ReadEvent() for each event
  │           │     └─► ReadConstructor() for each constructor
  │           └─► ReadType() recursively for nested types
  │
  └─► Build SymbolGraph
        ├─► Create NamespaceSymbol for each namespace
        └─► Return SymbolGraph
```

---

### Detailed Call Flow: TypeReferenceFactory.Create

```
Create(type)
  │
  ├─► Check cache → return if found
  ├─► Check _inProgress → return PlaceholderTypeReference if cycle detected
  │
  └─► CreateInternal(type)
        ├─► If IsByRef:
        │     └─► Create(type.GetElementType())
        │     └─► Return ByRefTypeReference
        │
        ├─► If IsPointer:
        │     └─► Count pointer depth
        │     └─► Create(elementType)
        │     └─► Return PointerTypeReference
        │
        ├─► If IsArray:
        │     └─► Create(type.GetElementType())
        │     └─► Return ArrayTypeReference
        │
        ├─► If IsGenericParameter:
        │     └─► CreateGenericParameter(type)
        │           ├─► Extract declaring context
        │           ├─► Create GenericParameterId
        │           └─► Return GenericParameterReference (constraints empty)
        │
        └─► Otherwise (named type):
              └─► CreateNamed(type)
                    ├─► Extract metadata (assembly, namespace, name)
                    ├─► HARDENING: Guarantee non-empty Name
                    ├─► Handle generic types:
                    │     └─► Create() recursively for each type argument
                    ├─► HARDENING: Stamp InterfaceStableId for interfaces
                    └─► Return NamedTypeReference
```

---

### Detailed Call Flow: InterfaceMemberSubstitution

```
SubstituteClosedInterfaces(ctx, graph)
  │
  ├─► BuildInterfaceIndex(graph)
  │     └─► Returns: Dictionary<string, TypeSymbol> (interface full name → symbol)
  │
  ├─► For each namespace:
  │     └─► For each type:
  │           └─► ProcessType(ctx, type, interfaceIndex)
  │                 ├─► For each implemented interface:
  │                 │     ├─► Check if closed generic (has type arguments)
  │                 │     ├─► GetGenericDefinitionName(ifaceRef.FullName)
  │                 │     ├─► Look up interface definition in index
  │                 │     └─► BuildSubstitutionMap(ifaceSymbol, closedInterfaceRef)
  │                 │           ├─► Validate arity matches
  │                 │           ├─► For each generic parameter:
  │                 │           │     └─► Map parameter name → type argument
  │                 │           └─► Return: Dictionary<string, TypeReference>
  │                 └─► Return substitution count
  │
  └─► Log total substitution count
```

**Note:** The substitution maps are built but not stored. Shape phase components will call `BuildSubstitutionMap()` and `SubstituteTypeReference()` as needed.

---

## Key Algorithms

### BFS Transitive Closure (ResolveClosure)

**Purpose:** Resolve all assemblies transitively referenced from seed assemblies.

**Algorithm:**
1. Initialize BFS queue with seed assembly paths
2. Initialize visited set (by AssemblyKey)
3. Initialize resolved map (AssemblyKey → path)
4. While queue not empty:
   - Dequeue current assembly path
   - Get AssemblyKey from path
   - Skip if already visited
   - Mark as visited
   - If already resolved:
     - Compare versions (keep highest)
     - Continue
   - Add to resolved map
   - Read assembly metadata (PEReader/MetadataReader)
   - For each assembly reference:
     - Create reference AssemblyKey
     - Look up in candidate map
     - If found: Pick highest version and enqueue
     - If not found: Skip (external reference)
5. Return resolved map

**Time complexity:** O(N) where N is total assemblies in closure
**Space complexity:** O(N) for visited set and resolved map

---

### Type Reference Memoization with Cycle Detection (Create)

**Purpose:** Convert System.Type to TypeReference without infinite recursion.

**Algorithm:**
1. Check cache:
   - If type already converted → return cached result
2. Check cycle:
   - If type in `_inProgress` set → return PlaceholderTypeReference
3. Mark as in-progress:
   - Add type to `_inProgress` set
4. Convert type:
   - Call `CreateInternal(type)` (may recursively call Create)
5. Cache result:
   - Add to `_cache`
6. Cleanup:
   - Remove from `_inProgress` set (in finally block)
7. Return result

**Time complexity:** O(D) where D is type depth (for first call), O(1) for cached calls
**Space complexity:** O(D) for recursion stack, O(T) for cache where T is total types

**Cycle detection example:**
- `IComparable<T> where T : IComparable<T>`
- Call Create(T):
  - Mark T as in-progress
  - Resolve constraint: IComparable<T>
  - Create generic argument: T
  - T is in-progress → return PlaceholderTypeReference
  - Cycle broken!

---

### Accessibility Computation for Nested Types (ComputeAccessibility)

**Purpose:** Determine effective public accessibility for nested types.

**Algorithm:**
1. If top-level type:
   - `IsPublic` → `Accessibility.Public`
   - Otherwise → `Accessibility.Internal`
2. If nested type:
   - If `IsNestedPublic`:
     - Recursively call `ComputeAccessibility(DeclaringType)`
     - If declaring type is Public → return `Accessibility.Public`
     - Otherwise → return `Accessibility.Internal`
   - If any other nested visibility → return `Accessibility.Internal`

**Time complexity:** O(N) where N is nesting depth
**Space complexity:** O(N) for recursion stack

**Example:**
```csharp
public class Outer         // Public
{
    public class Inner1    // Effectively Public (Outer is public)
    {
        public class Inner2 // Effectively Public (all ancestors public)
        { }
    }
}

internal class Hidden      // Internal
{
    public class Inner     // Effectively Internal (Outer is internal)
    { }
}
```

---

### Generic Parameter Substitution (SubstituteTypeReference)

**Purpose:** Substitute generic parameters in type references for closed generic interfaces.

**Algorithm (recursive pattern matching):**
1. **GenericParameterReference:**
   - If parameter name in substitution map → return mapped type
   - Otherwise → return original
2. **ArrayTypeReference:**
   - Recursively substitute element type
   - Return new ArrayTypeReference with substituted element
3. **PointerTypeReference:**
   - Recursively substitute pointee type
   - Return new PointerTypeReference with substituted pointee
4. **ByRefTypeReference:**
   - Recursively substitute referenced type
   - Return new ByRefTypeReference with substituted referenced type
5. **NamedTypeReference with type arguments:**
   - Recursively substitute each type argument
   - Return new NamedTypeReference with substituted arguments
6. **Other:**
   - Return original (no substitution needed)

**Time complexity:** O(D) where D is type depth
**Space complexity:** O(D) for recursion stack

**Example:**
- Original: `IComparable<T>.CompareTo(T other)`
- Substitution map: `{ "T" → int }`
- Result: `IComparable<int>.CompareTo(int other)`

---

## Summary

The **Load phase** is responsible for:

1. **Loading assemblies** with transitive closure resolution (AssemblyLoader)
2. **Validating assembly identity** consistency (PublicKeyToken, version drift)
3. **Extracting types and members** via reflection (ReflectionReader)
4. **Building type references** with memoization and cycle detection (TypeReferenceFactory)
5. **Building substitution maps** for closed generic interfaces (InterfaceMemberSubstitution)

**Output:** `SymbolGraph` with pure CLR metadata—no TypeScript concepts yet. This data flows into the Shape phase for further processing.

**Key design decisions:**
- **MetadataLoadContext isolation:** Assemblies loaded in isolation, enabling reflection on BCL without version conflicts
- **Name-based type comparisons:** Required for MetadataLoadContext compatibility (typeof() doesn't work)
- **Cycle detection:** Prevents stack overflow on recursive generic constraints
- **DeclaredOnly members:** Avoids reading inherited members (inheritance flattening happens in Shape phase)
- **Compiler-generated types filtered:** Skips angle-bracket types that aren't valid in declarations
- **Deduplication:** Assembly identity, member MetadataToken, type keys all deduplicated
- **Determinism:** Sorted iteration for reproducible output
- **Cross-assembly resolution:** DeclaringAssemblyResolver maps unresolved type references to their declaring assemblies

---

## File: DeclaringAssemblyResolver.cs

### Purpose

Resolves CLR type full names to their declaring assembly names using the reflection context. Used for cross-assembly dependency resolution to identify types that exist outside the current generation set. Enables future generation of ambient stubs for external dependencies.

**Context:** When ImportGraph encounters type references that don't resolve to any namespace in the current SymbolGraph, those CLR keys are collected as "unresolved". DeclaringAssemblyResolver uses the MetadataLoadContext to search through all loaded assemblies and determine which assembly declares each unresolved type.

**Use case:** If generating System.Linq and encountering a reference to a type from System.IO (not in generation set), the resolver identifies "System.IO" as the declaring assembly. Future phases can then generate ambient stub declarations for cross-assembly imports.

### Class: DeclaringAssemblyResolver

**Fields:**
- `_loadContext: MetadataLoadContext` - Reflection context with all assemblies loaded
- `_ctx: BuildContext` - Context for logging
- `_cache: Dictionary<string, string?>` - CLR key → assembly name cache (null = not found)

**Constructor:**
```csharp
public DeclaringAssemblyResolver(MetadataLoadContext loadContext, BuildContext ctx)
```
- Stores load context and BuildContext
- Initializes empty cache for memoization

---

### Method: ResolveAssembly

**Signature:**
```csharp
public string? ResolveAssembly(string clrFullName)
```

**What it does:**
Resolves a single CLR type full name (backtick form) to its declaring assembly name. Returns null if type cannot be found in any loaded assembly.

**Parameters:**
- `clrFullName` - CLR full name with backtick arity, e.g., `"System.Collections.Generic.IEnumerable`1"`

**Returns:**
- Assembly name (e.g., `"System.Private.CoreLib"`) if found
- `null` if type not found in any loaded assembly or on error

**Called by:**
- `ResolveBatch()` - For batch resolution
- Plan phase after ImportGraph.Build() collects unresolved keys

**How it works:**
1. Check cache - return cached result if available (null or assembly name)
2. Iterate through all assemblies in MetadataLoadContext via `GetAssemblies()`
3. For each assembly, try `assembly.GetType(clrFullName, throwOnError: false)`
4. If type found, cache and return assembly name
5. If not found in any assembly, cache null and return null
6. On exception, log error, cache null, return null

**Why:** MetadataLoadContext doesn't have a global FindType() method, so must search assemblies linearly. Caching prevents repeated expensive searches.

**Examples:**
- `"System.IO.Stream"` → `"System.Private.CoreLib"`
- `"System.Linq.Enumerable"` → `"System.Linq"`
- `"FooBar.NonExistent"` → `null`

---

### Method: ResolveBatch

**Signature:**
```csharp
public Dictionary<string, string> ResolveBatch(IEnumerable<string> clrKeys)
```

**What it does:**
Batch resolves multiple CLR keys to their declaring assemblies. Only returns successfully resolved types (not null results).

**Parameters:**
- `clrKeys` - Collection of CLR full names to resolve

**Returns:**
- Dictionary mapping CLR key → assembly name (only successful resolutions)

**Called by:**
- SinglePhaseBuilder.PlanPhase() after ImportGraph collects unresolved keys

**How it works:**
1. Create empty results dictionary
2. For each CLR key, call `ResolveAssembly()`
3. If result is non-null, add to results dictionary
4. Log batch resolution stats (X resolved out of Y total)
5. Return results

**Example:**
```
Input: ["System.IO.Stream", "System.Linq.Enumerable", "Unknown.Type"]
Output: {
  "System.IO.Stream" → "System.Private.CoreLib",
  "System.Linq.Enumerable" → "System.Linq"
}
```

---

### Method: GroupByAssembly

**Signature:**
```csharp
public Dictionary<string, List<string>> GroupByAssembly(
    Dictionary<string, string> resolvedTypes)
```

**What it does:**
Groups resolved types by their declaring assembly name. Useful for diagnostic output and planning stub generation.

**Parameters:**
- `resolvedTypes` - Dictionary from ResolveBatch (CLR key → assembly name)

**Returns:**
- Dictionary mapping assembly name → list of CLR keys declared in that assembly

**Called by:**
- SinglePhaseBuilder.PlanPhase() for diagnostic logging

**How it works:**
1. Group resolved types by assembly name using LINQ GroupBy
2. Convert each group to dictionary entry: assembly name → list of CLR keys
3. Return grouped dictionary

**Example:**
```
Input: {
  "System.IO.Stream" → "System.Private.CoreLib",
  "System.IO.File" → "System.Private.CoreLib",
  "System.Linq.Enumerable" → "System.Linq"
}

Output: {
  "System.Private.CoreLib" → ["System.IO.Stream", "System.IO.File"],
  "System.Linq" → ["System.Linq.Enumerable"]
}
```

**Use case:** Diagnostic logging shows:
```
Resolved 15 types across 3 assemblies:
  - System.Private.CoreLib: 8 types
  - System.Linq: 5 types
  - System.IO: 2 types
```

---

### Integration Point

**Used in:** SinglePhaseBuilder.PlanPhase()

```csharp
// After ImportGraph.Build()
if (importGraph.UnresolvedClrKeys.Count > 0)
{
    ctx.Log("CrossAssembly", $"Found {importGraph.UnresolvedClrKeys.Count} unresolved type references");

    var resolver = new DeclaringAssemblyResolver(loadContext, ctx);
    var unresolvedToAssembly = resolver.ResolveBatch(importGraph.UnresolvedClrKeys);

    // Store in graph data for future use
    importGraph.UnresolvedToAssembly = unresolvedToAssembly;

    // Diagnostic logging
    var byAssembly = resolver.GroupByAssembly(unresolvedToAssembly);
    foreach (var (assembly, types) in byAssembly)
    {
        ctx.Log("CrossAssembly", $"  - {assembly}: {types.Count} types");
    }
}
```

---
