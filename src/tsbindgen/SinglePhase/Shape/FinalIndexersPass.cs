using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Final, definitive pass to ensure indexer policy is enforced.
/// Runs at the end of Shape phase to ensure no indexer properties leak through.
///
/// Invariant:
/// - 0 indexers → nothing to do
/// - 1 indexer → keep as property ONLY if policy.EmitPropertyWhenSingle == true
/// - ≥2 indexers → convert ALL to get/set methods, remove ALL indexer properties
///
/// PURE - returns new SymbolGraph.
/// </summary>
public static class FinalIndexersPass
{
    public static SymbolGraph Run(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("FinalIndexersPass", "Enforcing final indexer policy...");

        var policy = ctx.Policy.Indexers;
        var updatedGraph = graph;
        int typesConverted = 0;
        int indexersRemoved = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                var indexers = type.Members.Properties
                    .Where(p => p.IsIndexer)
                    .ToImmutableArray();

                if (indexers.Length == 0)
                    continue; // No indexers

                // Invariant check
                if (indexers.Length == 1 && policy.EmitPropertyWhenSingle)
                {
                    // Keep single indexer as property
                    ctx.Log("FinalIndexersPass", $"Keeping single indexer property in {type.ClrFullName}");
                    continue;
                }

                // Convert all indexers to methods + remove all indexer properties
                ctx.Log("FinalIndexersPass", $"Converting {indexers.Length} indexers to methods in {type.ClrFullName}");

                var methods = indexers
                    .SelectMany(idx => ToIndexerMethods(ctx, type, idx, policy.MethodName))
                    .ToImmutableArray();

                updatedGraph = updatedGraph.WithUpdatedType(type.ClrFullName, t =>
                    t.WithMembers(t.Members with
                    {
                        Methods = t.Members.Methods.AddRange(methods),
                        Properties = t.Members.Properties.RemoveAll(p => p.IsIndexer)
                    }));

                typesConverted++;
                indexersRemoved += indexers.Length;

                // Verify removal
                if (updatedGraph.TryGetType(type.ClrFullName, out var verifyType))
                {
                    var remaining = verifyType.Members.Properties.Where(p => p.IsIndexer).ToList();
                    if (remaining.Count > 0)
                    {
                        ctx.Log("FinalIndexersPass", $"WARNING: {remaining.Count} indexers still remain in {type.ClrFullName} after removal!");
                    }
                    else
                    {
                        ctx.Log("FinalIndexersPass", $"All indexer properties removed from {type.ClrFullName}");
                    }
                }
            }
        }

        ctx.Log("FinalIndexersPass", $"Converted {typesConverted} types, removed {indexersRemoved} indexer properties");
        return updatedGraph;
    }

    private static IEnumerable<MethodSymbol> ToIndexerMethods(BuildContext ctx, TypeSymbol type, PropertySymbol indexer, string methodName)
    {
        // Create getter and optionally setter methods for the indexer
        // Note: TsEmitName will be assigned by centralized NameReservation pass

        // Create getter: T get_Item(TIndex index)
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

            yield return new MethodSymbol
            {
                StableId = getterStableId,
                ClrName = getterName,
                TsEmitName = "", // Will be set by NameReservation pass
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

        // Create setter: void set_Item(TIndex index, T value)
        if (indexer.HasSetter)
        {
            var setterName = $"set_{methodName}";

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

            yield return new MethodSymbol
            {
                StableId = setterStableId,
                ClrName = setterName,
                TsEmitName = "", // Will be set by NameReservation pass
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
