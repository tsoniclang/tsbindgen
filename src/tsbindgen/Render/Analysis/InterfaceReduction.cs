using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Analysis;

/// <summary>
/// Phase 3: Removes redundant interfaces from implements/extends lists.
/// Performs transitive reduction to avoid TS2320 errors.
///
/// Example:
/// - Before: ICollection_1&lt;T&gt; implements IEnumerable_1&lt;T&gt;, IEnumerable
/// - After:  ICollection_1&lt;T&gt; implements IEnumerable_1&lt;T&gt;
///
/// IEnumerable is removed because IEnumerable_1&lt;T&gt; already extends IEnumerable.
/// </summary>
public static class InterfaceReduction
{
    public static NamespaceModel Apply(NamespaceModel model, IReadOnlyDictionary<string, NamespaceModel> allModels)
    {
        // Build a global type lookup across all namespaces
        var globalTypeLookup = new Dictionary<string, TypeModel>();

        foreach (var ns in allModels.Values)
        {
            foreach (var type in ns.Types)
            {
                var key = GetTypeKey(type.Binding.Type);
                globalTypeLookup[key] = type;
            }
        }

        // Process each type
        var updatedTypes = model.Types.Select(type => ReduceInterfaces(type, globalTypeLookup)).ToList();

        return model with { Types = updatedTypes };
    }

    private static TypeModel ReduceInterfaces(TypeModel type, Dictionary<string, TypeModel> typeLookup)
    {
        if (type.Implements.Count <= 1)
            return type; // Nothing to reduce

        var reduced = PerformTransitiveReduction(type.Implements, typeLookup);

        return type with { Implements = reduced };
    }

    /// <summary>
    /// Performs transitive reduction: removes interfaces that are already inherited through other interfaces.
    /// </summary>
    private static IReadOnlyList<TypeReference> PerformTransitiveReduction(
        IReadOnlyList<TypeReference> interfaces,
        Dictionary<string, TypeModel> typeLookup)
    {
        var toKeep = new List<TypeReference>();

        foreach (var interfaceRef in interfaces)
        {
            // Check if this interface is inherited by any OTHER interface in the list
            bool isRedundant = false;

            foreach (var otherInterfaceRef in interfaces)
            {
                if (GetTypeKey(interfaceRef) == GetTypeKey(otherInterfaceRef))
                    continue; // Skip self

                // Check if otherInterface inherits from interfaceRef
                if (InheritsFrom(otherInterfaceRef, interfaceRef, typeLookup, new HashSet<string>()))
                {
                    isRedundant = true;
                    break;
                }
            }

            if (!isRedundant)
            {
                toKeep.Add(interfaceRef);
            }
        }

        return toKeep;
    }

    /// <summary>
    /// Checks if 'derived' inherits from 'base' (directly or transitively).
    /// Uses global type lookup to resolve cross-namespace references.
    /// </summary>
    private static bool InheritsFrom(
        TypeReference derived,
        TypeReference baseType,
        Dictionary<string, TypeModel> typeLookup,
        HashSet<string> visited)
    {
        var derivedKey = GetTypeKey(derived);

        // Prevent infinite recursion
        if (visited.Contains(derivedKey))
            return false;

        visited.Add(derivedKey);

        // Try to find the derived type definition in global lookup
        if (!typeLookup.TryGetValue(derivedKey, out var derivedTypeDef))
            return false; // Can't find type, assume not inherited

        var baseKey = GetTypeKey(baseType);

        // Check direct inheritance
        foreach (var parent in derivedTypeDef.Implements)
        {
            if (GetTypeKey(parent) == baseKey)
                return true; // Direct match

            // Check transitive inheritance
            if (InheritsFrom(parent, baseType, typeLookup, visited))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a key for type lookup.
    /// Uses GetClrType() to get full type string including namespace and type name.
    /// </summary>
    private static string GetTypeKey(TypeReference typeRef)
    {
        // Build a key from namespace + type name (without generic arguments)
        // This allows us to match IEnumerable_1<T> with IEnumerable_1<string>, etc.
        var ns = typeRef.Namespace != null ? typeRef.Namespace + "." : "";
        return ns + typeRef.TypeName;
    }
}
