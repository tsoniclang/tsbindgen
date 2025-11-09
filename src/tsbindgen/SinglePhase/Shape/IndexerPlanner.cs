using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Plans indexer representation (property vs methods).
/// Single uniform indexers → keep as properties
/// Multiple/heterogeneous indexers → convert to methods with configured name
/// PURE - returns new SymbolGraph.
/// </summary>
public static class IndexerPlanner
{
    public static SymbolGraph Plan(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("IndexerPlanner", "Planning indexer representations...");

        var typesWithIndexers = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Members.Properties.Any(p => p.IsIndexer))
            .ToList();

        ctx.Log("IndexerPlanner", $"Found {typesWithIndexers.Count} types with indexers");

        int totalConverted = 0;
        var updatedGraph = graph;

        foreach (var type in typesWithIndexers)
        {
            bool wasConverted;
            updatedGraph = PlanIndexersForType(ctx, updatedGraph, type, out wasConverted);
            if (wasConverted)
                totalConverted++;
        }

        ctx.Log("IndexerPlanner", $"Converted {totalConverted} indexer groups to methods");
        return updatedGraph;
    }

    private static SymbolGraph PlanIndexersForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type, out bool wasConverted)
    {
        wasConverted = false;

        var indexers = type.Members.Properties
            .Where(p => p.IsIndexer)
            .ToImmutableArray();

        if (indexers.Length == 0)
            return graph;

        var policy = ctx.Policy.Indexers;

        // Policy enforcement:
        // - Single indexer AND policy allows → keep as property
        // - Otherwise (multiple indexers OR policy forbids) → convert ALL to methods and remove ALL indexer properties

        if (indexers.Length == 1 && policy.EmitPropertyWhenSingle)
        {
            // Keep single indexer as property; do nothing else
            ctx.Log("IndexerPlanner", $"Keeping single indexer as property in {type.ClrFullName}");
            return graph;
        }

        // Multiple indexers OR policy says don't keep property:
        // 1) Synthesize get/set methods for each indexer
        // 2) Remove *all* indexer properties from class surface (immutably)

        var methodName = policy.MethodName; // Default: "Item"

        var synthesizedMethods = indexers
            .SelectMany(indexer => ToIndexerMethods(ctx, type, indexer, methodName))
            .ToImmutableArray();

        // Pure transformation - add methods and remove all indexer properties
        var updatedGraph = graph.WithUpdatedType(type.ClrFullName, t =>
            t.WithAddedMethods(synthesizedMethods)
             .WithRemovedProperties(p => p.IsIndexer));

        ctx.Log("IndexerPlanner", $"Converted {indexers.Length} indexers to {synthesizedMethods.Length} methods in {type.ClrFullName}");

        // Verify (diagnostic only)
        if (updatedGraph.TryGetType(type.ClrFullName, out var verifyType))
        {
            var remaining = verifyType.Members.Properties.Where(p => p.IsIndexer).ToList();
            if (remaining.Count > 0)
            {
                ctx.Log("IndexerPlanner", $"WARNING: {remaining.Count} indexers still remain after removal!");
                foreach (var r in remaining)
                    ctx.Log("IndexerPlanner", $"  - {r.ClrName} [{r.StableId.MemberName}{r.StableId.CanonicalSignature}]");
            }
            else
            {
                ctx.Log("IndexerPlanner", $"All indexer properties removed from {type.ClrFullName}");
            }
        }

        wasConverted = true;
        return updatedGraph;
    }

    private static IEnumerable<MethodSymbol> ToIndexerMethods(BuildContext ctx, TypeSymbol type, PropertySymbol indexer, string methodName)
    {
        // Create getter and optionally setter methods for the indexer
        // Getter: T get_Item(TIndex index)
        // Setter: void set_Item(TIndex index, T value)

        // M5 FIX: Base scope without #static/#instance suffix - ReserveMemberName will add it
        var typeScope = new TypeScope
        {
            TypeFullName = type.ClrFullName,
            IsStatic = indexer.IsStatic,
            ScopeKey = $"type:{type.ClrFullName}"
        };

        // Always create getter
        if (indexer.HasGetter)
        {
            var getterName = $"get_{methodName}";
            var getterStableId = new MemberStableId
            {
                AssemblyName = type.StableId.AssemblyName,
                DeclaringClrFullName = type.ClrFullName,
                MemberName = getterName,
                CanonicalSignature = ctx.CanonicalizeMethod(
                    getterName,
                    indexer.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                    GetTypeFullName(indexer.PropertyType))
            };

            // Reserve with base scope - ReserveMemberName adds #static/#instance
            ctx.Renamer.ReserveMemberName(
                getterStableId,
                getterName,
                typeScope,
                "IndexerGetter",
                indexer.IsStatic);

            // Get final name with full scope (including #static/#instance)
            var getterScope = indexer.IsStatic ? RenamerScopes.ClassStatic(type) : RenamerScopes.ClassInstance(type);
            var getterTsEmitName = ctx.Renamer.GetFinalMemberName(getterStableId, getterScope, indexer.IsStatic);

            yield return new MethodSymbol
            {
                StableId = getterStableId,
                ClrName = getterName,
                TsEmitName = getterTsEmitName,
                ReturnType = indexer.PropertyType,
                Parameters = indexer.IndexParameters,
                GenericParameters = ImmutableArray<GenericParameterSymbol>.Empty,
                IsStatic = indexer.IsStatic,
                IsAbstract = false,
                IsVirtual = indexer.IsVirtual,
                IsOverride = indexer.IsOverride,
                IsSealed = false,
                IsNew = false,
                Visibility = indexer.Visibility,
                Provenance = MemberProvenance.IndexerNormalized,
                EmitScope = EmitScope.ClassSurface,
                Documentation = indexer.Documentation
            };
        }

        // Create setter if property has setter
        if (indexer.HasSetter)
        {
            var setterName = $"set_{methodName}";

            // Setter parameters: index params + value param
            var setterParams = indexer.IndexParameters
                .Append(new ParameterSymbol
                {
                    Name = "value",
                    Type = indexer.PropertyType,
                    IsRef = false,
                    IsOut = false,
                    IsParams = false,
                    HasDefaultValue = false,
                    DefaultValue = null
                })
                .ToImmutableArray();

            var setterStableId = new MemberStableId
            {
                AssemblyName = type.StableId.AssemblyName,
                DeclaringClrFullName = type.ClrFullName,
                MemberName = setterName,
                CanonicalSignature = ctx.CanonicalizeMethod(
                    setterName,
                    setterParams.Select(p => GetTypeFullName(p.Type)).ToList(),
                    "System.Void")
            };

            // Reserve with base scope - ReserveMemberName adds #static/#instance
            ctx.Renamer.ReserveMemberName(
                setterStableId,
                setterName,
                typeScope,
                "IndexerSetter",
                indexer.IsStatic);

            // Get final name with full scope (including #static/#instance)
            var setterScope = indexer.IsStatic ? RenamerScopes.ClassStatic(type) : RenamerScopes.ClassInstance(type);
            var setterTsEmitName = ctx.Renamer.GetFinalMemberName(setterStableId, setterScope, indexer.IsStatic);

            yield return new MethodSymbol
            {
                StableId = setterStableId,
                ClrName = setterName,
                TsEmitName = setterTsEmitName,
                ReturnType = new Model.Types.NamedTypeReference
                {
                    AssemblyName = "System.Private.CoreLib",
                    FullName = "System.Void",
                    Namespace = "System",
                    Name = "Void",
                    Arity = 0,
                    TypeArguments = ImmutableArray<Model.Types.TypeReference>.Empty,
                    IsValueType = true
                },
                Parameters = setterParams,
                GenericParameters = ImmutableArray<GenericParameterSymbol>.Empty,
                IsStatic = indexer.IsStatic,
                IsAbstract = false,
                IsVirtual = indexer.IsVirtual,
                IsOverride = indexer.IsOverride,
                IsSealed = false,
                IsNew = false,
                Visibility = indexer.Visibility,
                Provenance = MemberProvenance.IndexerNormalized,
                EmitScope = EmitScope.ClassSurface,
                Documentation = indexer.Documentation
            };
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
