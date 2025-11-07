using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Analysis;

/// <summary>
/// Phase A2: Annotates indexer properties with explicit index parameters.
///
/// Ensures consistent indexer shape across interfaces and classes:
///   Item(index: System.Int32): T;
///   Item(index: System.Int32, value: T): System.Void;
///
/// Strategy (deterministic, not heuristic):
/// 1. Direct detection: If reflection captured index parameters
/// 2. Pattern matching: Known interface types (IList, IDictionary, etc.)
///    Priority: Dictionary interfaces > List interfaces
/// 3. Interface-driven inference: Classes implementing interfaces with indexers
/// </summary>
public static class IndexerShapeCatalog
{
    /// <summary>
    /// Phase A: Annotate interfaces only (strategies 1 & 2).
    /// Must be called across ALL namespaces before Phase B.
    /// </summary>
    public static NamespaceModel ApplyPhaseA(
        NamespaceModel model,
        IReadOnlyDictionary<string, NamespaceModel> allModels,
        AnalysisContext ctx)
    {
        var updatedTypes = model.Types.Select(type =>
        {
            if (type.Kind != TypeKind.Interface)
                return type;

            var updatedMembers = AnnotateIndexers(type, model, allModels, ctx, phaseA: true);
            if (updatedMembers == null)
                return type;

            return type with { Members = updatedMembers };
        }).ToList();

        return model with { Types = updatedTypes };
    }

    /// <summary>
    /// Phase B: Annotate classes and structs (all strategies, including interface inference).
    /// allModels MUST contain Phase A results from ALL namespaces.
    /// </summary>
    public static NamespaceModel ApplyPhaseB(
        NamespaceModel model,
        IReadOnlyDictionary<string, NamespaceModel> allModels,
        AnalysisContext ctx)
    {
        var updatedTypes = model.Types.Select(type =>
        {
            if (type.Kind == TypeKind.Interface)
                return type;  // Already done in Phase A

            var updatedMembers = AnnotateIndexers(type, model, allModels, ctx, phaseA: false);
            if (updatedMembers == null)
                return type;

            return type with { Members = updatedMembers };
        }).ToList();

        return model with { Types = updatedTypes };
    }

    /// <summary>
    /// Annotates Item properties in a single type.
    /// </summary>
    /// <param name="phaseA">If true, skip Strategy 3 (interface inference) to avoid circularity.</param>
    private static MemberCollectionModel? AnnotateIndexers(
        TypeModel type,
        NamespaceModel model,
        IReadOnlyDictionary<string, NamespaceModel> allModels,
        AnalysisContext ctx,
        bool phaseA)
    {
        var updatedProperties = new List<PropertyModel>();
        var modified = false;

        foreach (var prop in type.Members.Properties)
        {
            // Only process properties named "Item"
            if (prop.ClrName != "Item")
            {
                updatedProperties.Add(prop);
                continue;
            }

            // Strategy 1: Direct detection (if we already have parameters - shouldn't happen yet)
            if (prop.IndexerParameters.Count > 0)
            {
                updatedProperties.Add(prop with { IsIndexer = true });
                modified = true;
                continue;
            }

            // Strategy 2: Pattern matching on known interface types
            var indexParams = SynthesizeFromKnownPattern(type, model);
            if (indexParams != null)
            {
                updatedProperties.Add(prop with
                {
                    IsIndexer = true,
                    IndexerParameters = indexParams
                });
                modified = true;
                continue;
            }

            // Strategy 3: Interface-driven inference (for classes) - only in Phase B
            if (!phaseA && (type.Kind == TypeKind.Class || type.Kind == TypeKind.Struct))
            {
                indexParams = InferFromImplementedInterfaces(type, prop, model, allModels, ctx);
                if (indexParams != null)
                {
                    updatedProperties.Add(prop with
                    {
                        IsIndexer = true,
                        IndexerParameters = indexParams
                    });
                    modified = true;
                    continue;
                }
            }

            // Not an indexer, keep as-is
            updatedProperties.Add(prop);
        }

        if (!modified)
            return null;

        return type.Members with { Properties = updatedProperties };
    }

    /// <summary>
    /// Strategy 2: Synthesize index parameters via pattern matching on known interface types.
    /// Deterministic: dictionary interfaces have priority over list interfaces.
    /// </summary>
    private static IReadOnlyList<ParameterModel>? SynthesizeFromKnownPattern(
        TypeModel type,
        NamespaceModel model)
    {
        // Known indexer carrier types (use CLR backtick notation)
        var knownIndexerTypes = new HashSet<string>
        {
            "IList", "IList`1",
            "IReadOnlyList`1",
            "IDictionary`2",
            "IReadOnlyDictionary`2",
            "Array",
            "ReadOnlyCollection`1",
            "ImmutableArray`1",
            "ImmutableList`1",
            "List`1",
            "Dictionary`2",
            "Collection`1",
            "SortedList`2"
        };

        // Check if this type itself is a known indexer carrier
        if (knownIndexerTypes.Contains(type.ClrName))
        {
            // For dictionaries, use TKey as index parameter
            if (type.ClrName == "IDictionary`2" || type.ClrName == "IReadOnlyDictionary`2" || type.ClrName == "Dictionary`2")
            {
                if (type.GenericParameters.Count >= 1)
                {
                    // First generic param is TKey - create type reference
                    var tKeyRef = new TypeReference(
                        Namespace: null,
                        TypeName: type.GenericParameters[0].Name,
                        GenericArgs: Array.Empty<TypeReference>(),
                        ArrayRank: 0,
                        PointerDepth: 0,
                        DeclaringType: null,
                        Assembly: null);

                    return new[]
                    {
                        new ParameterModel(
                            Name: "key",
                            Type: tKeyRef,
                            Kind: ParameterKind.In,
                            IsOptional: false,
                            DefaultValue: null,
                            IsParams: false)
                    };
                }
            }

            // For all other indexers, use System.Int32
            return CreateInt32IndexParameter();
        }

        // Check if type implements a known indexer interface
        foreach (var impl in type.Implements)
        {
            var interfaceTypeName = ExtractTypeName(impl.TypeName);
            if (knownIndexerTypes.Contains(interfaceTypeName))
            {
                // For dictionary interfaces, use the first generic arg
                if ((interfaceTypeName == "IDictionary`2" || interfaceTypeName == "IReadOnlyDictionary`2") &&
                    impl.GenericArgs.Count >= 1)
                {
                    return new[]
                    {
                        new ParameterModel(
                            Name: "key",
                            Type: impl.GenericArgs[0],
                            Kind: ParameterKind.In,
                            IsOptional: false,
                            DefaultValue: null,
                            IsParams: false)
                    };
                }

                // For list-like, use Int32
                return CreateInt32IndexParameter();
            }
        }

        return null;
    }

    /// <summary>
    /// Strategy 3: Infer indexer shape from implemented interfaces.
    /// Used for classes that implement interfaces with indexers.
    /// </summary>
    private static IReadOnlyList<ParameterModel>? InferFromImplementedInterfaces(
        TypeModel type,
        PropertyModel prop,
        NamespaceModel model,
        IReadOnlyDictionary<string, NamespaceModel> allModels,
        AnalysisContext ctx)
    {
        // Check each implemented interface for an indexer
        foreach (var interfaceRef in type.Implements)
        {
            // Look up the interface type
            var interfaceNs = interfaceRef.Namespace ?? model.ClrName;
            if (!allModels.TryGetValue(interfaceNs, out var interfaceNsModel))
                continue;

            var interfaceType = interfaceNsModel.Types
                .FirstOrDefault(t => t.Binding.Type.TypeName == interfaceRef.TypeName);

            if (interfaceType == null)
                continue;

            // Check if interface has an indexer (Item property)
            var interfaceItemProp = interfaceType.Members.Properties
                .FirstOrDefault(p => p.ClrName == "Item" && p.IsIndexer);

            if (interfaceItemProp != null && interfaceItemProp.IndexerParameters.Count > 0)
            {
                // Substitute generic parameters if needed
                return SubstituteGenericParameters(
                    interfaceItemProp.IndexerParameters,
                    interfaceType.GenericParameters,
                    interfaceRef.GenericArgs);
            }
        }

        return null;
    }

    /// <summary>
    /// Substitutes generic type parameters in indexer parameters.
    /// Example: IList_1&lt;string&gt;.Item's T param becomes string.
    /// </summary>
    private static IReadOnlyList<ParameterModel> SubstituteGenericParameters(
        IReadOnlyList<ParameterModel> parameters,
        IReadOnlyList<GenericParameterModel> interfaceTypeParams,
        IReadOnlyList<TypeReference> genericArgs)
    {
        if (genericArgs.Count == 0)
            return parameters;

        // Build substitution map
        var substitutionMap = new Dictionary<string, TypeReference>();
        for (int i = 0; i < Math.Min(interfaceTypeParams.Count, genericArgs.Count); i++)
        {
            substitutionMap[interfaceTypeParams[i].Name] = genericArgs[i];
        }

        // Apply substitution
        return parameters.Select(p =>
        {
            var substitutedType = SubstituteTypeReference(p.Type, substitutionMap);
            if (substitutedType == p.Type)
                return p;

            return p with { Type = substitutedType };
        }).ToList();
    }

    /// <summary>
    /// Recursively substitutes type parameters in a TypeReference.
    /// </summary>
    private static TypeReference SubstituteTypeReference(
        TypeReference typeRef,
        Dictionary<string, TypeReference> substitutionMap)
    {
        // Check if this is a type parameter
        if (typeRef.Namespace == null && substitutionMap.TryGetValue(typeRef.TypeName, out var substitution))
        {
            return substitution;
        }

        // Recursively substitute generic arguments
        if (typeRef.GenericArgs.Count > 0)
        {
            var substitutedArgs = typeRef.GenericArgs
                .Select(arg => SubstituteTypeReference(arg, substitutionMap))
                .ToList();

            // Check if any changed
            if (!substitutedArgs.SequenceEqual(typeRef.GenericArgs))
            {
                return typeRef with { GenericArgs = substitutedArgs };
            }
        }

        return typeRef;
    }

    /// <summary>
    /// Creates a standard System.Int32 index parameter.
    /// </summary>
    private static IReadOnlyList<ParameterModel> CreateInt32IndexParameter()
    {
        var int32Type = new TypeReference(
            Namespace: "System",
            TypeName: "Int32",
            GenericArgs: Array.Empty<TypeReference>(),
            ArrayRank: 0,
            PointerDepth: 0,
            DeclaringType: null,
            Assembly: "System.Private.CoreLib");

        return new[]
        {
            new ParameterModel(
                Name: "index",
                Type: int32Type,
                Kind: ParameterKind.In,
                IsOptional: false,
                DefaultValue: null,
                IsParams: false)
        };
    }

    /// <summary>
    /// Extracts base type name from full type name.
    /// Examples: "IList`1" → "IList`1", "System.Collections.Generic.IList`1" → "IList`1"
    /// </summary>
    private static string ExtractTypeName(string fullTypeName)
    {
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot == -1 ? fullTypeName : fullTypeName.Substring(lastDot + 1);
    }
}
