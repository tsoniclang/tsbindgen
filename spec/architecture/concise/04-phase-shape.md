# Phase 4: Shape - CLR to TypeScript Semantic Transformation

Transforms CLR semantics → TypeScript-compatible semantics. All passes are pure functions returning new `SymbolGraph`.

## Pass Execution Order

```
1. GlobalInterfaceIndex      → Build global interface signature index
2. InterfaceDeclIndex         → Build declared-only member index
3. InterfaceInliner          → Flatten interface hierarchies (remove extends)
4. StructuralConformance     → Synthesize ViewOnly for non-conforming interfaces
5. ExplicitImplSynthesizer   → Synthesize missing EII members
6. InterfaceResolver         → Resolve which interface declares member
7. DiamondResolver           → Detect diamond inheritance conflicts
8. BaseOverloadAdder         → Add base class overloads to derived
9. OverloadReturnConflictResolver → Detect return-type conflicts
10. MemberDeduplicator       → Remove StableId duplicates
11. ViewPlanner              → Create As_IInterface properties
12. ClassSurfaceDeduplicator → Deduplicate by emitted name, demote to ViewOnly
13. HiddenMemberPlanner      → Reserve names for 'new' hidden members
14. IndexerPlanner           → Convert indexers to methods (policy)
15. FinalIndexersPass        → Final indexer policy enforcement
16. StaticSideAnalyzer       → Detect/rename static-side conflicts
17. ConstraintCloser         → Resolve and validate generic constraints
```

**Critical Dependencies:**
- 3 before 4-5: Synthesis needs flattened interfaces
- 1-2 before 6: Resolver needs indexes
- 4-5 before 11: View planning needs ViewOnly members
- 11 before 12: Surface dedup can safely demote to ViewOnly
- 14 before 15: Final pass ensures no indexer leaks

---

## Pass 1: GlobalInterfaceIndex

Builds cross-assembly interface index for member resolution.

**Data Structures:**
```csharp
record InterfaceInfo(
    TypeSymbol Symbol,
    HashSet<string> MethodSignatures,    // All (declared + inherited)
    HashSet<string> PropertySignatures)
```

**Algorithm:**
- Index ALL interfaces by `ClrFullName`
- Compute canonical signatures for methods/properties
- Store in `_globalIndex` dictionary

**Used by:** InterfaceResolver, StructuralConformance

---

## Pass 2: InterfaceDeclIndex

Indexes ONLY declared members (excludes inherited).

**Why separate?** Needed to determine which interface in chain actually declares a member.

**Algorithm:**
1. For each interface:
   - Collect inherited signatures via BFS of base interfaces
   - Subtract inherited from total → declared only
2. Store as `DeclaredMembers`

**Used by:** InterfaceResolver.FindDeclaringInterface

---

## Pass 3: InterfaceInliner

Flattens interface hierarchies - copies all inherited members into each interface, clears `extends`.

**Why?** TypeScript `extends` causes variance issues. Safer to flatten.

**Algorithm:**
1. BFS traversal of `iface.Interfaces`, collect all members
2. Deduplicate:
   - Methods: by canonical signature
   - Properties: by name (TS doesn't allow property overloads)
   - Events: by canonical signature
3. Clear `Interfaces` array

**Example:**
```
Before: IEnumerable<T> extends IEnumerable
After:  IEnumerable_1<T> { both GetEnumerator() variants inlined }
```

---

## Pass 4: StructuralConformance

Analyzes structural conformance and synthesizes ViewOnly members for interfaces that can't be satisfied on class surface.

**Algorithm:**
1. Build class surface (exclude ViewOnly)
2. For each interface:
   - Build substituted interface surface (flattened + type args)
   - Check TS assignability: `ClassSurface.IsTsAssignableMethod/Property(ifaceMember)`
   - For missing: synthesize ViewOnly clone with **interface's StableId**
3. Add ViewOnly members to type

**Key:** Uses interface member's StableId (not class's) to prevent ID conflicts across types.

**ClassSurface.IsTsAssignableMethod:**
1. Find candidates by name (case-insensitive)
2. Erase to TS signatures (remove CLR-specific info)
3. Check `TsAssignability.IsMethodAssignable(classSig, ifaceSig)`

---

## Pass 5: ExplicitImplSynthesizer

Synthesizes missing interface members. In C#, explicit interface implementations are invisible on class - we must synthesize them.

**Algorithm:**
1. Collect required members from all implemented interfaces
2. Find missing: check `type.Members.*.Any(m => m.StableId.Equals(required.StableId))`
3. Synthesize missing with:
   - **Interface member's StableId** (not class's)
   - `Provenance = ExplicitView`
   - `EmitScope = ViewOnly`
4. Deduplicate by StableId (multiple interfaces may require same member, e.g., ICollection.CopyTo and IList.CopyTo)

**Key:** Compare by StableId directly, not re-canonicalizing signatures.

---

## Pass 6: InterfaceResolver

Resolves which interface in inheritance chain declares a member.

**Why?** When IList<T> : ICollection<T> both have Add(), determine which declared it first.

**Algorithm (FindDeclaringInterface):**
1. Build inheritance chain: recursive BFS from roots to given interface (top-down)
2. Walk chain from ancestors to immediate
3. For each: check `InterfaceDeclIndex.DeclaresMethod/Property(ifaceDefName, canonicalSig)`
4. Pick most ancestral (first in chain) if multiple candidates
5. Cache result by `(closedIfaceName, memberCanonicalSig)`

**BuildInterfaceChain:**
- Recursive BFS: process base interfaces first, then current
- Substitute type arguments at each level
- Reverse to get top-down order (roots first)

---

## Pass 7: DiamondResolver

Detects diamond inheritance conflicts. When multiple paths bring same method with different signatures, logs conflict.

**Diamond Pattern:**
```
    IBase
   /    \
  IA    IB (both override IBase.Method with different sigs)
   \    /
   Class
```

**Algorithm:**
1. Group methods by CLR name within scope (ClassSurface/ViewOnly separate)
2. For groups with multiple methods:
   - Group by canonical signature
   - If multiple signatures → diamond detected
3. Log conflicts (PhaseGate validates)
4. Strategy handling:
   - `OverloadAll` → keep all (already in list)
   - `PreferDerived` → log preference (don't modify)
   - `Error` → PhaseGate will fail

**Note:** Detection only, doesn't modify graph.

---

## Pass 8: BaseOverloadAdder

Adds base class overloads to derived. TS requires all overloads present on derived (unlike C# where they're inherited).

**Algorithm:**
1. Find base class (skip if external/System.Object)
2. Group methods by name (derived and base)
3. For each base method:
   - If derived doesn't override → skip (keeps base)
   - Build expected StableId for derived version
   - If missing from derived → synthesize
4. Deduplicate by StableId (base hierarchy may have same method at multiple levels)

**CreateBaseOverloadMethod:**
- Uses **derived class's StableId** (not base's)
- `Provenance = BaseOverload`
- `EmitScope = ClassSurface`
- Reserves name with Renamer

---

## Pass 9: OverloadReturnConflictResolver

Detects return-type conflicts. TS doesn't support overloads differing only in return type.

**Algorithm:**
1. Group methods by signature excluding return: `"MethodName(param1Type,param2Type)"`
2. For groups with multiple return types → conflict detected
3. Log conflict (PhaseGate validates)

**Note:** Detection only, doesn't modify graph.

---

## Pass 10: MemberDeduplicator

Safety net - removes any StableId duplicates introduced by multiple Shape passes.

**Algorithm:**
- For each type: deduplicate methods/properties/fields/events/constructors by StableId
- Keep first occurrence (deterministic)

---

## Pass 11: ViewPlanner

Creates As_IInterface properties for interfaces with ViewOnly members.

**Algorithm:**
1. Collect ALL ViewOnly members with `SourceInterface`
2. Group by interface StableId
3. For each interface:
   - Create `ExplicitView(InterfaceReference, ViewPropertyName, ViewMembers)`
   - Validate no duplicate StableIds within view
4. Attach views to type

**CreateViewName:**
- `IDisposable` → `As_IDisposable`
- `IEnumerable<string>` → `As_IEnumerable_1_of_string`
- `IDictionary<string, int>` → `As_IDictionary_2_of_string_and_int`

---

## Pass 12: ClassSurfaceDeduplicator

Deduplicates by emitted name (post-camelCase). When multiple properties emit to same name, keep most specific and demote others to ViewOnly.

**Algorithm:**
1. Group class-surface properties by emitted name (camelCase)
2. For duplicate groups:
   - Pick winner: `PickWinner(candidates)`
   - Demote losers: `EmitScope = ViewOnly`

**PickWinner (preference order):**
1. Non-explicit over explicit (`Provenance != ExplicitView`)
2. Generic over non-generic (GenericParameterReference vs concrete)
3. Narrower type over `object`
4. Stable ordering by `(DeclaringClrFullName, CanonicalSignature)`

**Example:** `IEnumerator<T>.Current` (generic T) wins over `IEnumerator.Current` (object)

---

## Pass 13: HiddenMemberPlanner

Handles C# 'new' hidden members. Reserves renamed versions (e.g., `Method_new`) through Renamer.

**Algorithm:**
- For each method with `IsNew`:
  - Build requested name: `method.ClrName + ctx.Policy.Classes.HiddenMemberSuffix` (default: "_new")
  - Reserve with Renamer: `ReserveMemberName(..., "HiddenNewConflict", ...)`

**Note:** Pure planning - doesn't modify graph, Renamer handles names.

---

## Pass 14: IndexerPlanner

Plans indexer representation (property vs methods).

**Policy:** `ctx.Policy.Indexers.EmitPropertyWhenSingle`
- Single indexer + policy true → keep as property
- Otherwise → convert ALL to methods

**ToIndexerMethods:**
1. Getter: `T get_{methodName}(TIndex index)`
2. Setter: `void set_{methodName}(TIndex index, T value)`
- Default: `get_Item`, `set_Item`
- Reserve names with Renamer
- `Provenance = IndexerNormalized`

---

## Pass 15: FinalIndexersPass

Final enforcement - ensures no indexer properties leak through.

**Invariant:**
- 0 indexers → nothing
- 1 indexer → keep as property ONLY if `policy.EmitPropertyWhenSingle`
- ≥2 indexers → convert ALL to methods

**Note:** Same conversion as Pass 14, but runs at end to catch any leaks.

---

## Pass 16: StaticSideAnalyzer

Analyzes static-side inheritance conflicts. TS doesn't allow static side of class to extend static side of base.

**Algorithm:**
1. Collect static members from derived and base
2. Find name conflicts: `derivedStaticNames.Intersect(baseStaticNames)`
3. Apply policy action:
   - `Error` → fail build
   - `AutoRename` → reserve `"{name}_static"` via Renamer
   - `Analyze` → warn only

**Note:** Uses Renamer for renames, doesn't modify graph directly.

---

## Pass 17: ConstraintCloser

Closes generic constraints for TypeScript.

**Steps:**
1. **Resolve:** Convert raw `System.Type` constraints → `TypeReference`s
   - Uses memoized `TypeReferenceFactory` to prevent infinite loops
2. **Validate:**
   - Both `struct` and `class` constraints → warning
   - Unrepresentable types (pointers, byrefs) → warning
3. **Merge strategy:**
   - `Intersection` → TS uses `T extends A & B & C` automatically
   - `Union` → Not supported in TS, emit warning
   - `PreferLeft` → Log strategy

---

## Key Transformations

### Interface Flattening
```
IEnumerable<T> extends IEnumerable
→ IEnumerable_1<T> { both GetEnumerator() variants }
```

### ViewOnly Synthesis
```
class Decimal : IConvertible
→ class Decimal { [ViewOnly] ToBoolean(...) }
```

### Explicit Views
```
class Decimal { [ViewOnly] ToBoolean, ToByte }
→ class Decimal { As_IConvertible: { toBoolean, toByte } }
```

### Base Overload Addition
```
class Derived { method(x: int) }
→ class Derived { method(x: int), method(s: string) }
```

### Indexer Conversion
```
class Array<T> { this[int]: T, this[Range]: T[] }
→ class Array_1<T> { get_Item(int): T, get_Item(Range): T[], set_Item(...) }
```

### Class Surface Dedup
```
class Enumerator<T> { current: object, current: T }
→ class Enumerator_1<T> { current: T, As_IEnumerator: { current: object } }
```

---

## Output

**Shape phase produces:**
- Flattened interfaces (no extends)
- ViewOnly members for non-conforming interfaces
- Explicit views planned
- Base overloads added
- Indexers converted (policy-dependent)
- Conflicts detected (diamonds, return-type, static-side)
- Constraints resolved and validated
- Clean graph ready for Renaming/Emit

**Next Phase:** Renaming
