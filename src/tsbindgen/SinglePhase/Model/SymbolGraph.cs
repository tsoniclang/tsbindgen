using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Model.Symbols;

namespace tsbindgen.SinglePhase.Model;

/// <summary>
/// The complete symbol graph for all loaded assemblies.
/// Created during Load phase, transformed during Shape phase.
/// Hierarchy: SymbolGraph → Namespaces → Types → Members
/// IMMUTABLE - use helper methods to create modified copies.
/// </summary>
public sealed record SymbolGraph
{
    /// <summary>
    /// All namespaces with their types.
    /// </summary>
    public required ImmutableArray<NamespaceSymbol> Namespaces { get; init; }

    /// <summary>
    /// Source assembly paths that contributed to this graph.
    /// </summary>
    public required ImmutableHashSet<string> SourceAssemblies { get; init; }

    /// <summary>
    /// Quick lookup: namespace name → namespace symbol.
    /// Built once during construction/transformation.
    /// </summary>
    public ImmutableDictionary<string, NamespaceSymbol> NamespaceIndex { get; init; } =
        ImmutableDictionary<string, NamespaceSymbol>.Empty;

    /// <summary>
    /// Quick lookup: type full name → type symbol.
    /// Built once during construction/transformation.
    /// </summary>
    public ImmutableDictionary<string, TypeSymbol> TypeIndex { get; init; } =
        ImmutableDictionary<string, TypeSymbol>.Empty;

    /// <summary>
    /// Build indices from namespaces (pure - returns new graph).
    /// Call this after creating a new graph to populate indices.
    /// </summary>
    public SymbolGraph WithIndices()
    {
        var nsIndexBuilder = ImmutableDictionary.CreateBuilder<string, NamespaceSymbol>();
        var typeIndexBuilder = ImmutableDictionary.CreateBuilder<string, TypeSymbol>();

        foreach (var ns in Namespaces)
        {
            nsIndexBuilder[ns.Name] = ns;

            foreach (var type in ns.Types)
            {
                typeIndexBuilder[type.ClrFullName] = type;
                IndexNestedTypes(type, typeIndexBuilder);
            }
        }

        return this with
        {
            NamespaceIndex = nsIndexBuilder.ToImmutable(),
            TypeIndex = typeIndexBuilder.ToImmutable()
        };
    }

    private static void IndexNestedTypes(TypeSymbol type,
        ImmutableDictionary<string, TypeSymbol>.Builder builder)
    {
        foreach (var nested in type.NestedTypes)
        {
            builder[nested.ClrFullName] = nested;
            IndexNestedTypes(nested, builder);
        }
    }

    /// <summary>
    /// Try to find a namespace by name.
    /// </summary>
    public bool TryGetNamespace(string name, out NamespaceSymbol? ns) =>
        NamespaceIndex.TryGetValue(name, out ns);

    /// <summary>
    /// Try to find a type by full CLR name.
    /// </summary>
    public bool TryGetType(string clrFullName, out TypeSymbol? type) =>
        TypeIndex.TryGetValue(clrFullName, out type);

    /// <summary>
    /// Update a single type in the graph (pure - returns new graph).
    /// Finds the type by CLR full name, applies the transform, and returns a new graph.
    /// Automatically rebuilds indices.
    /// </summary>
    public SymbolGraph WithUpdatedType(string keyOrStableId, Func<TypeSymbol, TypeSymbol> transform)
    {
        // Determine if key is a StableId (contains assembly:) or ClrFullName
        bool isStableId = keyOrStableId.Contains(':');

        // Find which namespace contains this type
        var targetNamespace = Namespaces.FirstOrDefault(ns =>
            ns.Types.Any(t => MatchesKey(t, keyOrStableId, isStableId)));

        if (targetNamespace == null)
            return this; // Type not found - return unchanged

        // Transform the namespace to update the type
        var updatedNamespace = targetNamespace with
        {
            Types = targetNamespace.Types.Select(t =>
                UpdateTypeRecursive(t, keyOrStableId, isStableId, transform)).ToImmutableArray()
        };

        // Replace namespace in graph
        var updatedNamespaces = Namespaces.Select(ns =>
            ns.Name == targetNamespace.Name ? updatedNamespace : ns).ToImmutableArray();

        // Return new graph with rebuilt indices
        return (this with { Namespaces = updatedNamespaces }).WithIndices();
    }

    private static bool MatchesKey(TypeSymbol type, string key, bool isStableId)
    {
        if (isStableId)
            return type.StableId.ToString() == key;
        else
            return type.ClrFullName == key || ContainsNestedType(type, key);
    }

    private static TypeSymbol UpdateTypeRecursive(TypeSymbol type, string key, bool isStableId,
        Func<TypeSymbol, TypeSymbol> transform)
    {
        // If this is the target type, apply transform
        bool isMatch = isStableId
            ? type.StableId.ToString() == key
            : type.ClrFullName == key;

        if (isMatch)
            return transform(type);

        // Check nested types (only for ClrFullName matching, not StableId)
        if (!isStableId)
        {
            var hasNestedTarget = type.NestedTypes.Any(n =>
                n.ClrFullName == key || ContainsNestedType(n, key));

            if (!hasNestedTarget)
                return type; // No change needed

            // Recursively update nested types
            var updatedNested = type.NestedTypes.Select(n =>
                UpdateTypeRecursive(n, key, isStableId, transform)).ToImmutableArray();

            return type with { NestedTypes = updatedNested };
        }

        return type; // No change needed
    }

    private static bool ContainsNestedType(TypeSymbol type, string clrFullName)
    {
        foreach (var nested in type.NestedTypes)
        {
            if (nested.ClrFullName == clrFullName)
                return true;
            if (ContainsNestedType(nested, clrFullName))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Get statistics about the symbol graph.
    /// </summary>
    public SymbolGraphStatistics GetStatistics()
    {
        var totalTypes = 0;
        var totalMethods = 0;
        var totalProperties = 0;
        var totalFields = 0;
        var totalEvents = 0;

        foreach (var ns in Namespaces)
        {
            foreach (var type in ns.Types)
            {
                totalTypes++;
                totalMethods += type.Members.Methods.Length;
                totalProperties += type.Members.Properties.Length;
                totalFields += type.Members.Fields.Length;
                totalEvents += type.Members.Events.Length;

                CountNestedTypes(type, ref totalTypes, ref totalMethods,
                    ref totalProperties, ref totalFields, ref totalEvents);
            }
        }

        return new SymbolGraphStatistics
        {
            NamespaceCount = Namespaces.Length,
            TypeCount = totalTypes,
            MethodCount = totalMethods,
            PropertyCount = totalProperties,
            FieldCount = totalFields,
            EventCount = totalEvents
        };
    }

    private static void CountNestedTypes(TypeSymbol type,
        ref int types, ref int methods, ref int properties, ref int fields, ref int events)
    {
        foreach (var nested in type.NestedTypes)
        {
            types++;
            methods += nested.Members.Methods.Length;
            properties += nested.Members.Properties.Length;
            fields += nested.Members.Fields.Length;
            events += nested.Members.Events.Length;

            CountNestedTypes(nested, ref types, ref methods, ref properties, ref fields, ref events);
        }
    }
}

/// <summary>
/// Statistics about a symbol graph.
/// </summary>
public sealed record SymbolGraphStatistics
{
    public required int NamespaceCount { get; init; }
    public required int TypeCount { get; init; }
    public required int MethodCount { get; init; }
    public required int PropertyCount { get; init; }
    public required int FieldCount { get; init; }
    public required int EventCount { get; init; }

    public int TotalMembers => MethodCount + PropertyCount + FieldCount + EventCount;
}
