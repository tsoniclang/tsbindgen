using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Deduplicates class surface by emitted name (post-camelCase).
/// When multiple properties emit to the same name, keeps the most specific one
/// and demotes others to ViewOnly.
/// PURE - returns new SymbolGraph.
/// </summary>
public static class ClassSurfaceDeduplicator
{
    public static SymbolGraph Deduplicate(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ClassSurfaceDeduplicator", "Deduplicating class surface by emitted name...");

        int totalDemoted = 0;
        var updatedNamespaces = ImmutableArray.CreateBuilder<NamespaceSymbol>();

        foreach (var ns in graph.Namespaces)
        {
            var updatedTypes = ImmutableArray.CreateBuilder<TypeSymbol>();

            foreach (var type in ns.Types)
            {
                var (updatedType, demoted) = DeduplicateType(ctx, type);
                updatedTypes.Add(updatedType);
                totalDemoted += demoted;
            }

            updatedNamespaces.Add(ns with { Types = updatedTypes.ToImmutable() });
        }

        ctx.Log("ClassSurfaceDeduplicator", $"Demoted {totalDemoted} duplicate members to ViewOnly");
        return (graph with { Namespaces = updatedNamespaces.ToImmutable() }).WithIndices();
    }

    private static (TypeSymbol UpdatedType, int Demoted) DeduplicateType(BuildContext ctx, TypeSymbol type)
    {
        // Only process classes and structs
        if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
            return (type, 0);

        int demoted = 0;

        // Deduplicate properties by emitted name
        var (updatedProperties, propertyDemoted) = DeduplicateProperties(ctx, type);
        demoted += propertyDemoted;

        // Could also deduplicate methods, but in practice property duplicates are the main issue
        // Methods with same name but different signatures are overloads (handled elsewhere)

        if (demoted == 0)
            return (type, 0);

        return (type with
        {
            Members = type.Members with
            {
                Properties = updatedProperties
            }
        }, demoted);
    }

    private static (ImmutableArray<PropertySymbol> Updated, int Demoted) DeduplicateProperties(
        BuildContext ctx,
        TypeSymbol type)
    {
        // Group class-surface properties by emitted name (camelCase)
        var groups = type.Members.Properties
            .Where(p => p.EmitScope == EmitScope.ClassSurface)
            .GroupBy(p => ApplyCamelCase(p.ClrName))
            .Where(g => g.Count() > 1) // Only groups with duplicates
            .ToList();

        if (groups.Count == 0)
            return (type.Members.Properties, 0);

        var demotions = new HashSet<SinglePhase.Renaming.MemberStableId>();
        int totalDemoted = 0;

        foreach (var group in groups)
        {
            var emittedName = group.Key;
            var candidates = group.ToList();

            // Pick winner using deterministic rules
            var winner = PickWinner(candidates);

            ctx.Log("class-dedupe",
                $"winner: {type.StableId} name={emittedName} kept={Plan.Validation.Scopes.FormatMemberStableId(winner.StableId)}");

            // Demote all others to ViewOnly
            foreach (var loser in candidates.Where(c => c.StableId != winner.StableId))
            {
                demotions.Add(loser.StableId);
                totalDemoted++;

                var ifaceName = loser.SourceInterface?.ToString() ?? "Unknown";
                ctx.Log("class-dedupe",
                    $"demote: {type.StableId} name={emittedName} -> ViewOnly iface={ifaceName} {Plan.Validation.Scopes.FormatMemberStableId(loser.StableId)}");
            }
        }

        // Apply demotions
        var updated = type.Members.Properties.Select(p =>
        {
            if (demotions.Contains(p.StableId))
            {
                return p with { EmitScope = EmitScope.ViewOnly };
            }
            return p;
        }).ToImmutableArray();

        return (updated, totalDemoted);
    }

    /// <summary>
    /// Pick the winner from duplicate properties using deterministic rules.
    /// Preference order:
    /// 1. Non-explicit over explicit (public member beats EII)
    /// 2. Generic over non-generic (IEnumerator&lt;T&gt;.Current beats IEnumerator.Current)
    /// 3. Narrower return type over object
    /// 4. Stable ordering by (DeclaringClrFullName, CanonicalSignature)
    /// </summary>
    private static PropertySymbol PickWinner(List<PropertySymbol> candidates)
    {
        return candidates
            .OrderBy(p => p.Provenance == MemberProvenance.ExplicitView ? 1 : 0) // Non-explicit first
            .ThenBy(p => IsGenericType(p.PropertyType) ? 0 : 1) // Generic first
            .ThenBy(p => IsObjectType(p.PropertyType) ? 1 : 0) // Non-object first
            .ThenBy(p => p.StableId.DeclaringClrFullName) // Stable ordering
            .ThenBy(p => p.StableId.CanonicalSignature)
            .First();
    }

    /// <summary>
    /// Check if a type is a generic parameter (T) vs concrete type.
    /// </summary>
    private static bool IsGenericType(Model.Types.TypeReference typeRef)
    {
        return typeRef is Model.Types.GenericParameterReference;
    }

    /// <summary>
    /// Check if a type is System.Object.
    /// </summary>
    private static bool IsObjectType(Model.Types.TypeReference typeRef)
    {
        if (typeRef is Model.Types.NamedTypeReference named)
        {
            return named.FullName == "System.Object";
        }
        return false;
    }

    /// <summary>
    /// Apply camelCase transformation to a name (simplified).
    /// </summary>
    private static string ApplyCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // Simple lowercase first character
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
