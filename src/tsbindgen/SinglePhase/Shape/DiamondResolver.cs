using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Resolves diamond inheritance conflicts.
/// When multiple inheritance paths bring the same method with potentially different signatures,
/// this ensures all variants are available in TypeScript.
/// PURE - returns new SymbolGraph.
/// </summary>
public static class DiamondResolver
{
    public static SymbolGraph Resolve(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("DiamondResolver", "Resolving diamond inheritance...");

        var strategy = ctx.Policy.Interfaces.DiamondResolution;

        if (strategy == Core.Policy.DiamondResolutionStrategy.Error)
        {
            ctx.Log("DiamondResolver", "Strategy is Error - analyzing for conflicts");
            AnalyzeForDiamonds(ctx, graph);
            return graph;
        }

        var allTypes = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .ToList();

        int totalResolved = 0;
        var updatedGraph = graph;

        foreach (var type in allTypes)
        {
            var (newGraph, resolved) = ResolveForType(ctx, updatedGraph, type, strategy);
            updatedGraph = newGraph;
            totalResolved += resolved;
        }

        ctx.Log("DiamondResolver", $"Resolved {totalResolved} diamond conflicts");
        return updatedGraph;
    }

    private static (SymbolGraph UpdatedGraph, int ResolvedCount) ResolveForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type, Core.Policy.DiamondResolutionStrategy strategy)
    {
        // Find methods that come from multiple paths
        var methodGroups = type.Members.Methods
            .GroupBy(m => m.ClrName)
            .Where(g => g.Count() > 1)
            .ToList();

        if (methodGroups.Count == 0)
            return (graph, 0);

        int resolved = 0;
        var methodsToMarkViewOnly = new HashSet<MethodSymbol>();

        // Sort by method name for deterministic iteration
        foreach (var group in methodGroups.OrderBy(g => g.Key))
        {
            // Check if these are true diamond conflicts (same name, different signatures from different paths)
            var methods = group.ToList();

            // Group by signature
            var signatureGroups = methods.GroupBy(m =>
                ctx.CanonicalizeMethod(
                    m.ClrName,
                    m.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                    GetTypeFullName(m.ReturnType)))
                .ToList();

            // If all have the same signature, no conflict
            if (signatureGroups.Count <= 1)
                continue;

            // Diamond conflict detected
            ctx.Log("DiamondResolver", $"Diamond conflict in {type.ClrFullName}.{group.Key} - {signatureGroups.Count} signatures");

            if (strategy == Core.Policy.DiamondResolutionStrategy.OverloadAll)
            {
                // Keep all overloads - they're already in the members list
                // Just ensure they all have unique names via renamer if needed
                foreach (var method in methods)
                {
                    EnsureMethodRenamed(ctx, type, method);
                }
                resolved += methods.Count;
            }
            else if (strategy == Core.Policy.DiamondResolutionStrategy.PreferDerived)
            {
                // Keep only the most derived version (first in list, typically)
                // Mark others as ViewOnly
                foreach (var method in methods.Skip(1))
                {
                    methodsToMarkViewOnly.Add(method);
                }
                resolved++;
            }
        }

        // If no methods need updating, return original graph
        if (methodsToMarkViewOnly.Count == 0)
            return (graph, resolved);

        // Build new method list with updated EmitScope
        var updatedMethods = type.Members.Methods.Select(m =>
        {
            if (methodsToMarkViewOnly.Contains(m))
            {
                return m with { EmitScope = EmitScope.ViewOnly };
            }
            return m;
        }).ToImmutableArray();

        // Update the type immutably
        var updatedGraph = graph.WithUpdatedType(type.StableId.ToString(), t => t with
        {
            Members = t.Members with
            {
                Methods = updatedMethods
            }
        });

        return (updatedGraph, resolved);
    }

    private static void EnsureMethodRenamed(BuildContext ctx, TypeSymbol type, MethodSymbol method)
    {
        // M5 FIX: Base scope without #static/#instance suffix - ReserveMemberName will add it
        var typeScope = new TypeScope
        {
            TypeFullName = type.ClrFullName,
            IsStatic = method.IsStatic,
            ScopeKey = $"type:{type.ClrFullName}"
        };

        // Reserve through renamer with DiamondResolved reason
        ctx.Renamer.ReserveMemberName(
            method.StableId,
            method.ClrName,
            typeScope,
            "DiamondResolved",
            method.IsStatic);
    }

    private static void AnalyzeForDiamonds(BuildContext ctx, SymbolGraph graph)
    {
        var allTypes = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .ToList();

        foreach (var type in allTypes)
        {
            var methodGroups = type.Members.Methods
                .GroupBy(m => m.ClrName)
                .Where(g => g.Count() > 1)
                .ToList();

            // Sort by method name for deterministic iteration
            foreach (var group in methodGroups.OrderBy(g => g.Key))
            {
                var methods = group.ToList();

                var signatureGroups = methods.GroupBy(m =>
                    ctx.CanonicalizeMethod(
                        m.ClrName,
                        m.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                        GetTypeFullName(m.ReturnType)))
                    .ToList();

                if (signatureGroups.Count > 1)
                {
                    ctx.Diagnostics.Warning(
                        Core.Diagnostics.DiagnosticCodes.DiamondInheritanceConflict,
                        $"Diamond inheritance conflict in {type.ClrFullName}.{group.Key} - {signatureGroups.Count} signatures");
                }
            }
        }
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
