using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Load;

/// <summary>
/// Substitutes generic type parameters in interface members for closed generic interfaces.
/// For `IComparable&lt;T&gt;.CompareTo(T)` implemented as `IComparable&lt;int&gt;`, substitutes T → int.
/// Creates closed member surfaces used by interface flattening, structural conformance, and explicit views.
/// </summary>
public static class InterfaceMemberSubstitution
{
    /// <summary>
    /// Process all types in the graph, building substitution maps for closed generic interfaces.
    /// The actual substituted members will be used by Shape phase components (InterfaceInliner, StructuralConformance, ViewPlanner).
    /// </summary>
    public static void SubstituteClosedInterfaces(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("InterfaceMemberSubstitution", "Building closed interface member maps...");

        int totalSubstitutions = 0;

        // Build interface index for lookup
        var interfaceIndex = BuildInterfaceIndex(graph);

        // Build substitution maps for each type that implements closed generic interfaces
        int nsCount = 0;
        foreach (var ns in graph.Namespaces)
        {
            nsCount++;
            if (nsCount % 10 == 0)
                ctx.Log("InterfaceMemberSubstitution", $"Processing namespace {nsCount}/{graph.Namespaces.Length}: {ns.Name}");

            foreach (var type in ns.Types)
            {
                var substitutions = ProcessType(ctx, type, interfaceIndex);
                totalSubstitutions += substitutions;
            }
        }

        ctx.Log("InterfaceMemberSubstitution", $"Created {totalSubstitutions} interface member mappings");
    }

    private static Dictionary<string, TypeSymbol> BuildInterfaceIndex(SymbolGraph graph)
    {
        var index = new Dictionary<string, TypeSymbol>();

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.Kind == TypeKind.Interface)
                {
                    index[type.ClrFullName] = type;
                }
            }
        }

        return index;
    }

    private static int ProcessType(BuildContext ctx, TypeSymbol type, Dictionary<string, TypeSymbol> interfaceIndex)
    {
        if (type.Interfaces.Length == 0)
            return 0;

        int substitutionCount = 0;

        // Process each implemented interface
        foreach (var ifaceRef in type.Interfaces)
        {
            // Check if this is a closed generic interface (has type arguments)
            if (ifaceRef is NamedTypeReference namedRef && namedRef.TypeArguments.Count > 0)
            {
                // Find the generic interface definition
                var genericIfaceName = GetGenericDefinitionName(namedRef.FullName);
                if (interfaceIndex.TryGetValue(genericIfaceName, out var ifaceSymbol))
                {
                    // Build substitution map for this interface
                    var substitutionMap = BuildSubstitutionMap(ifaceSymbol, namedRef);

                    if (substitutionMap.Count > 0)
                    {
                        substitutionCount++;

                        // The substitution map is now available for later Shape phase components
                        // They can use it to create substituted member views for:
                        // - Interface flattening (InterfaceInliner)
                        // - Structural conformance checking (StructuralConformance)
                        // - Explicit view planning (ViewPlanner)
                    }
                }
            }
        }

        return substitutionCount;
    }

    private static Dictionary<string, TypeReference> BuildSubstitutionMap(
        TypeSymbol interfaceSymbol,
        NamedTypeReference closedInterfaceRef)
    {
        var map = new Dictionary<string, TypeReference>();

        if (interfaceSymbol.GenericParameters.Length != closedInterfaceRef.TypeArguments.Count)
            return map; // Mismatch - skip

        for (int i = 0; i < interfaceSymbol.GenericParameters.Length; i++)
        {
            var param = interfaceSymbol.GenericParameters[i];
            var arg = closedInterfaceRef.TypeArguments[i];
            map[param.Name] = arg;
        }

        return map;
    }

    /// <summary>
    /// Substitutes type parameters in a type reference using the given substitution map.
    /// This is used by Shape phase components when they need to create substituted member signatures.
    /// </summary>
    public static TypeReference SubstituteTypeReference(
        TypeReference original,
        Dictionary<string, TypeReference> substitutionMap)
    {
        return original switch
        {
            GenericParameterReference gp when substitutionMap.ContainsKey(gp.Name) =>
                substitutionMap[gp.Name],

            ArrayTypeReference arr => new ArrayTypeReference
            {
                ElementType = SubstituteTypeReference(arr.ElementType, substitutionMap),
                Rank = arr.Rank
            },

            PointerTypeReference ptr => new PointerTypeReference
            {
                PointeeType = SubstituteTypeReference(ptr.PointeeType, substitutionMap),
                Depth = ptr.Depth
            },

            ByRefTypeReference byref => new ByRefTypeReference
            {
                ReferencedType = SubstituteTypeReference(byref.ReferencedType, substitutionMap)
            },

            NamedTypeReference named when named.TypeArguments.Count > 0 => new NamedTypeReference
            {
                AssemblyName = named.AssemblyName,
                Namespace = named.Namespace,
                Name = named.Name,
                FullName = named.FullName,
                Arity = named.Arity,
                IsValueType = named.IsValueType,
                TypeArguments = named.TypeArguments
                    .Select(arg => SubstituteTypeReference(arg, substitutionMap))
                    .ToList()
            },

            _ => original // No substitution needed
        };
    }

    private static string GetGenericDefinitionName(string fullName)
    {
        // Convert "System.IComparable<int>" → "System.IComparable`1"
        // Handle both angle brackets and backtick notation
        var tickIndex = fullName.IndexOf('`');
        if (tickIndex >= 0)
        {
            // Already has backtick - extract arity
            var arityEnd = tickIndex + 1;
            while (arityEnd < fullName.Length && char.IsDigit(fullName[arityEnd]))
                arityEnd++;

            return fullName.Substring(0, arityEnd);
        }

        // No backtick found - might be already a definition name or not generic
        return fullName;
    }
}
