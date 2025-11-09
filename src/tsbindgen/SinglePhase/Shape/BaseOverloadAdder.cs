using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Adds base class overloads when derived class differs.
/// In TypeScript, all overloads must be present on the derived class.
/// PURE - returns new SymbolGraph.
/// </summary>
public static class BaseOverloadAdder
{
    public static SymbolGraph AddOverloads(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("BaseOverloadAdder", "Adding base class overloads...");

        var classes = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Class && t.BaseType != null)
            .ToList();

        int totalAdded = 0;
        var updatedGraph = graph;

        foreach (var derivedClass in classes)
        {
            var (newGraph, added) = AddOverloadsForClass(ctx, updatedGraph, derivedClass);
            updatedGraph = newGraph;
            totalAdded += added;
        }

        ctx.Log("BaseOverloadAdder", $"Added {totalAdded} base overloads");
        return updatedGraph;
    }

    private static (SymbolGraph UpdatedGraph, int AddedCount) AddOverloadsForClass(BuildContext ctx, SymbolGraph graph, TypeSymbol derivedClass)
    {
        // Find the base class
        var baseClass = FindBaseClass(graph, derivedClass);
        if (baseClass == null)
            return (graph, 0); // External base or System.Object

        // Find methods in derived that override or hide base methods
        var derivedMethodsByName = derivedClass.Members.Methods
            .Where(m => !m.IsStatic)
            .GroupBy(m => m.ClrName)
            .ToDictionary(g => g.Key, g => g.ToList());

        var baseMethodsByName = baseClass.Members.Methods
            .Where(m => !m.IsStatic)
            .GroupBy(m => m.ClrName)
            .ToDictionary(g => g.Key, g => g.ToList());

        var addedMethods = new List<MethodSymbol>();

        // For each base method name, check if derived has all the same overloads
        // Sort by method name for deterministic iteration
        foreach (var (methodName, baseMethods) in baseMethodsByName.OrderBy(kvp => kvp.Key))
        {
            if (!derivedMethodsByName.TryGetValue(methodName, out var derivedMethods))
            {
                // Derived doesn't override this method at all - keep base methods
                continue;
            }

            // Check each base method to see if derived has the same signature
            foreach (var baseMethod in baseMethods)
            {
                var baseSig = ctx.CanonicalizeMethod(
                    baseMethod.ClrName,
                    baseMethod.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                    GetTypeFullName(baseMethod.ReturnType));

                var derivedHasSig = derivedMethods.Any(dm =>
                {
                    var dSig = ctx.CanonicalizeMethod(
                        dm.ClrName,
                        dm.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                        GetTypeFullName(dm.ReturnType));
                    return dSig == baseSig;
                });

                if (!derivedHasSig)
                {
                    // Derived doesn't have this base overload - add it
                    var addedMethod = CreateBaseOverloadMethod(ctx, derivedClass, baseMethod);
                    addedMethods.Add(addedMethod);
                }
            }
        }

        if (addedMethods.Count == 0)
            return (graph, 0);

        ctx.Log("BaseOverloadAdder", $"Adding {addedMethods.Count} base overloads to {derivedClass.ClrFullName}");

        // Add to derived class (immutably)
        var updatedGraph = graph.WithUpdatedType(derivedClass.StableId.ToString(), t => t with
        {
            Members = t.Members with
            {
                Methods = t.Members.Methods.Concat(addedMethods).ToImmutableArray()
            }
        });

        return (updatedGraph, addedMethods.Count);
    }

    private static MethodSymbol CreateBaseOverloadMethod(BuildContext ctx, TypeSymbol derivedClass, MethodSymbol baseMethod)
    {
        // M5 FIX: Base scope without #static/#instance suffix - ReserveMemberName will add it
        var typeScope = new TypeScope
        {
            TypeFullName = derivedClass.ClrFullName,
            IsStatic = false,
            ScopeKey = $"type:{derivedClass.ClrFullName}"
        };

        var stableId = new MemberStableId
        {
            AssemblyName = derivedClass.StableId.AssemblyName,
            DeclaringClrFullName = derivedClass.ClrFullName,
            MemberName = baseMethod.ClrName,
            CanonicalSignature = ctx.CanonicalizeMethod(
                baseMethod.ClrName,
                baseMethod.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(baseMethod.ReturnType))
        };

        // Reserve name with BaseOverload reason
        ctx.Renamer.ReserveMemberName(
            stableId,
            baseMethod.ClrName,
            typeScope,
            "BaseOverload",
            isStatic: false);

        // Create the method with BaseOverload provenance
        return new MethodSymbol
        {
            StableId = stableId,
            ClrName = baseMethod.ClrName,
            ReturnType = baseMethod.ReturnType,
            Parameters = baseMethod.Parameters,
            GenericParameters = baseMethod.GenericParameters,
            IsStatic = false,
            IsAbstract = baseMethod.IsAbstract,
            IsVirtual = baseMethod.IsVirtual,
            IsOverride = false, // Not an override, it's the base signature
            IsSealed = false,
            IsNew = false,
            Visibility = baseMethod.Visibility,
            Provenance = MemberProvenance.BaseOverload,
            EmitScope = EmitScope.ClassSurface,
            Documentation = baseMethod.Documentation
        };
    }

    private static TypeSymbol? FindBaseClass(SymbolGraph graph, TypeSymbol derivedClass)
    {
        if (derivedClass.BaseType == null)
            return null;

        var baseFullName = GetTypeFullName(derivedClass.BaseType);

        // Skip System.Object and System.ValueType
        if (baseFullName == "System.Object" || baseFullName == "System.ValueType")
            return null;

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == baseFullName && t.Kind == TypeKind.Class);
    }

    private static string GetTypeFullName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            Model.Types.GenericParameterReference gp => gp.Name,
            Model.Types.ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            Model.Types.PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            Model.Types.ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}
