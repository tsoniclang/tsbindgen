using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Resolves interface members to their declaring interface.
/// Used to determine which interface in an inheritance chain actually declares a member.
/// </summary>
public static class InterfaceResolver
{
    // Cache for FindDeclaringInterface results: (closedIfaceFullName, memberCanonicalSig) -> declaring interface
    private static Dictionary<(string, string), TypeReference?> _declaringInterfaceCache = new();

    /// <summary>
    /// Find the interface that actually declares a member.
    /// Walks up the interface inheritance chain from the given closed interface to find
    /// which ancestor interface declares the member.
    /// </summary>
    /// <param name="closedIface">The closed interface reference (e.g., ICollection&lt;TFoo&gt;)</param>
    /// <param name="memberCanonicalSig">The canonical signature of the member after substitution</param>
    /// <param name="isMethod">True if method, false if property</param>
    /// <param name="ctx">Build context for logging</param>
    /// <returns>The closed interface reference that declares the member, or null if not found</returns>
    public static TypeReference? FindDeclaringInterface(
        TypeReference closedIface,
        string memberCanonicalSig,
        bool isMethod,
        BuildContext ctx)
    {
        var closedIfaceName = GetTypeFullName(closedIface);
        var cacheKey = (closedIfaceName, memberCanonicalSig);

        ctx.Log("InterfaceResolver", $"Finding declaring interface for {memberCanonicalSig} starting from {closedIfaceName}");

        // Check cache
        if (_declaringInterfaceCache.TryGetValue(cacheKey, out var cached))
        {
            ctx.Log("InterfaceResolver", $"Cache hit - returning {(cached != null ? GetTypeFullName(cached) : "null")}");
            return cached;
        }

        // Get the generic definition name (with arity backtick)
        var genericDefName = GetGenericDefinitionName(closedIfaceName);

        // Build the inheritance chain from roots to closedIface
        var chain = BuildInterfaceChain(closedIface, ctx);

        // Walk from most ancestral (top) to immediate (bottom), checking declares-only index
        TypeReference? declaringInterface = null;
        var candidates = new List<(TypeReference iface, string defName)>();

        foreach (var ifaceInChain in chain)
        {
            var ifaceDefName = GetGenericDefinitionName(GetTypeFullName(ifaceInChain));

            // Check if this interface declares the member
            bool declares = isMethod
                ? InterfaceDeclIndex.DeclaresMethod(ifaceDefName, memberCanonicalSig)
                : InterfaceDeclIndex.DeclaresProperty(ifaceDefName, memberCanonicalSig);

            if (declares)
            {
                candidates.Add((ifaceInChain, ifaceDefName));
            }
        }

        if (candidates.Count == 0)
        {
            // Member not found in any interface - this shouldn't happen
            ctx.Log("InterfaceResolver", $"WARNING - Member {memberCanonicalSig} not declared in any interface in chain from {closedIfaceName}");
            declaringInterface = null;
        }
        else if (candidates.Count == 1)
        {
            // Single declaring interface - the common case
            declaringInterface = candidates[0].iface;
        }
        else
        {
            // Multiple interfaces declare the same signature (diamond anomaly)
            // Pick the most ancestral; if tied, use lexicographic order for determinism
            ctx.Log("InterfaceResolver", $"WARNING - Multiple interfaces declare {memberCanonicalSig}: {string.Join(", ", candidates.Select(c => c.defName))}");

            // For now, pick the first (most ancestral) one
            // TODO: If we need more sophisticated tiebreaking, implement here
            declaringInterface = candidates[0].iface;

            ctx.Log("InterfaceResolver", $"Using {GetTypeFullName(declaringInterface)} as declaring interface (most ancestral)");
        }

        // Cache result
        _declaringInterfaceCache[cacheKey] = declaringInterface;

        return declaringInterface;
    }

    /// <summary>
    /// Build the inheritance chain from roots to the given interface.
    /// Returns interfaces in top-down order (most ancestral first).
    /// </summary>
    private static List<TypeReference> BuildInterfaceChain(TypeReference iface, BuildContext ctx)
    {
        var chain = new List<TypeReference>();
        var visited = new HashSet<string>();

        BuildInterfaceChainRecursive(iface, chain, visited, ctx);

        // Reverse to get top-down order (roots first)
        chain.Reverse();

        return chain;
    }

    private static void BuildInterfaceChainRecursive(
        TypeReference iface,
        List<TypeReference> chain,
        HashSet<string> visited,
        BuildContext ctx)
    {
        var ifaceName = GetTypeFullName(iface);

        if (visited.Contains(ifaceName))
        {
            return; // Already processed
        }

        visited.Add(ifaceName);

        // Get the interface info to access base interfaces
        var genericDefName = GetGenericDefinitionName(ifaceName);
        var info = GlobalInterfaceIndex.GetInterface(genericDefName);

        if (info != null)
        {
            // Recursively process base interfaces first
            foreach (var baseIfaceRef in info.Symbol.Interfaces)
            {
                // Substitute type arguments from the closed interface into the base reference
                var closedBaseRef = SubstituteTypeArguments(baseIfaceRef, iface);
                BuildInterfaceChainRecursive(closedBaseRef, chain, visited, ctx);
            }
        }

        // Add this interface to the chain (after its bases)
        chain.Add(iface);
    }

    /// <summary>
    /// Substitute type arguments from a closed interface into a base interface reference.
    /// For now, this is a simplified version - if the base reference is already closed, return it.
    /// If it has generic parameters, we'd need to map them through the declaring interface's type parameters.
    /// </summary>
    private static TypeReference SubstituteTypeArguments(TypeReference baseRef, TypeReference closedIface)
    {
        // Simplified: For now, just return the base reference as-is
        // TODO: Implement proper generic argument propagation if needed
        // This would require mapping generic parameters through the type argument list
        return baseRef;
    }

    /// <summary>
    /// Get the full name of a type reference.
    /// </summary>
    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    /// <summary>
    /// Get the generic definition name (with backtick arity) from a full name.
    /// For example, "System.Collections.Generic.IEnumerable`1[[System.Int32]]" -> "System.Collections.Generic.IEnumerable`1"
    /// </summary>
    private static string GetGenericDefinitionName(string fullName)
    {
        // If the name contains [[, it's a closed generic type - strip the type arguments
        var bracketIndex = fullName.IndexOf("[[");
        if (bracketIndex >= 0)
        {
            return fullName.Substring(0, bracketIndex);
        }

        return fullName;
    }

    /// <summary>
    /// Clear the cache (for testing or when rebuilding the index).
    /// </summary>
    public static void ClearCache()
    {
        _declaringInterfaceCache.Clear();
    }
}
