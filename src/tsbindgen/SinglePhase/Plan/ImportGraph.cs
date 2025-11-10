using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Builds cross-namespace dependency graph for import planning.
/// Analyzes type references to determine which namespaces need to import from which other namespaces.
/// Creates ImportGraphData containing dependency edges and namespace-local type sets.
/// </summary>
public static class ImportGraph
{
    public static ImportGraphData Build(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ImportGraph", "Building cross-namespace dependency graph...");

        var graphData = new ImportGraphData
        {
            NamespaceDependencies = new Dictionary<string, HashSet<string>>(),
            NamespaceTypeIndex = new Dictionary<string, HashSet<string>>(),
            CrossNamespaceReferences = new List<CrossNamespaceReference>()
        };

        // Build namespace type index first
        BuildNamespaceTypeIndex(ctx, graph, graphData);

        // Analyze dependencies for each namespace
        foreach (var ns in graph.Namespaces)
        {
            AnalyzeNamespaceDependencies(ctx, graph, ns, graphData);
        }

        ctx.Log("ImportGraph", $"Found {graphData.NamespaceDependencies.Count} namespaces with dependencies");
        ctx.Log("ImportGraph", $"Total cross-namespace references: {graphData.CrossNamespaceReferences.Count}");

        return graphData;
    }

    private static void BuildNamespaceTypeIndex(BuildContext ctx, SymbolGraph graph, ImportGraphData graphData)
    {
        // Build index: namespace name -> set of type full names in that namespace
        // ONLY INDEX PUBLIC TYPES - internal types won't be emitted so shouldn't be in import index
        foreach (var ns in graph.Namespaces)
        {
            var typeNames = new HashSet<string>();

            foreach (var type in ns.Types.Where(t => t.Accessibility == Accessibility.Public))
            {
                // Add to set-based index (for backwards compatibility)
                typeNames.Add(type.ClrFullName);

                // Add to fast lookup map: CLR full name → namespace
                // CRITICAL: ClrFullName is in backtick form (e.g., "IEnumerable`1")
                // This matches NamedTypeReference.FullName format
                graphData.ClrFullNameToNamespace[type.ClrFullName] = ns.Name;
            }

            graphData.NamespaceTypeIndex[ns.Name] = typeNames;
        }

        ctx.Log("ImportGraph", $"Indexed {graphData.NamespaceTypeIndex.Count} namespaces");
        ctx.Log("ImportGraph", $"Fast lookup map: {graphData.ClrFullNameToNamespace.Count} types");
    }

    private static void AnalyzeNamespaceDependencies(
        BuildContext ctx,
        SymbolGraph graph,
        NamespaceSymbol ns,
        ImportGraphData graphData)
    {
        var dependencies = new HashSet<string>();

        // ONLY ANALYZE PUBLIC TYPES - internal types won't be emitted
        foreach (var type in ns.Types.Where(t => t.Accessibility == Accessibility.Public))
        {
            // Analyze base class - collect ALL referenced types recursively
            if (type.BaseType != null)
            {
                var baseTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                CollectTypeReferences(type.BaseType, graph, graphData, baseTypeRefs);

                foreach (var (fullName, targetNs) in baseTypeRefs)
                {
                    if (targetNs != null && targetNs != ns.Name)
                    {
                        dependencies.Add(targetNs);
                        graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                            SourceNamespace: ns.Name,
                            SourceType: type.ClrFullName,
                            TargetNamespace: targetNs,
                            TargetType: fullName,
                            ReferenceKind: ReferenceKind.BaseClass));
                    }
                }
            }

            // Analyze interfaces - collect ALL referenced types recursively
            foreach (var ifaceRef in type.Interfaces)
            {
                var ifaceTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                CollectTypeReferences(ifaceRef, graph, graphData, ifaceTypeRefs);

                foreach (var (fullName, targetNs) in ifaceTypeRefs)
                {
                    if (targetNs != null && targetNs != ns.Name)
                    {
                        dependencies.Add(targetNs);
                        graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                            SourceNamespace: ns.Name,
                            SourceType: type.ClrFullName,
                            TargetNamespace: targetNs,
                            TargetType: fullName,
                            ReferenceKind: ReferenceKind.Interface));
                    }
                }
            }

            // Analyze generic parameters constraints - collect ALL referenced types recursively
            foreach (var gp in type.GenericParameters)
            {
                foreach (var constraint in gp.Constraints)
                {
                    var constraintTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                    CollectTypeReferences(constraint, graph, graphData, constraintTypeRefs);

                    foreach (var (fullName, targetNs) in constraintTypeRefs)
                    {
                        if (targetNs != null && targetNs != ns.Name)
                        {
                            dependencies.Add(targetNs);
                            graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                                SourceNamespace: ns.Name,
                                SourceType: type.ClrFullName,
                                TargetNamespace: targetNs,
                                TargetType: fullName,
                                ReferenceKind: ReferenceKind.GenericConstraint));
                        }
                    }
                }
            }

            // Analyze members
            AnalyzeMemberDependencies(ctx, graph, graphData, ns, type, dependencies);
        }

        if (dependencies.Count > 0)
        {
            graphData.NamespaceDependencies[ns.Name] = dependencies;
            ctx.Log("ImportGraph", $"{ns.Name} depends on {dependencies.Count} other namespaces");
        }
    }

    private static void AnalyzeMemberDependencies(
        BuildContext ctx,
        SymbolGraph graph,
        ImportGraphData graphData,
        NamespaceSymbol ns,
        TypeSymbol type,
        HashSet<string> dependencies)
    {
        // Analyze methods
        foreach (var method in type.Members.Methods)
        {
            // Return type - collect ALL referenced types recursively
            var returnTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(method.ReturnType, graph, graphData, returnTypeRefs);

            foreach (var (fullName, targetNs) in returnTypeRefs)
            {
                if (targetNs != null && targetNs != ns.Name)
                {
                    dependencies.Add(targetNs);
                    graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                        SourceNamespace: ns.Name,
                        SourceType: type.ClrFullName,
                        TargetNamespace: targetNs,
                        TargetType: fullName,
                        ReferenceKind: ReferenceKind.MethodReturn));
                }
            }

            // Parameters - collect ALL referenced types recursively
            foreach (var param in method.Parameters)
            {
                var paramTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                CollectTypeReferences(param.Type, graph, graphData, paramTypeRefs);

                foreach (var (fullName, targetNs) in paramTypeRefs)
                {
                    if (targetNs != null && targetNs != ns.Name)
                    {
                        dependencies.Add(targetNs);
                        graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                            SourceNamespace: ns.Name,
                            SourceType: type.ClrFullName,
                            TargetNamespace: targetNs,
                            TargetType: fullName,
                            ReferenceKind: ReferenceKind.MethodParameter));
                    }
                }
            }

            // Generic parameters constraints - collect recursively
            foreach (var gp in method.GenericParameters)
            {
                foreach (var constraint in gp.Constraints)
                {
                    var constraintTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                    CollectTypeReferences(constraint, graph, graphData, constraintTypeRefs);

                    foreach (var (fullName, targetNs) in constraintTypeRefs)
                    {
                        if (targetNs != null && targetNs != ns.Name)
                        {
                            dependencies.Add(targetNs);
                            graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                                SourceNamespace: ns.Name,
                                SourceType: type.ClrFullName,
                                TargetNamespace: targetNs,
                                TargetType: fullName,
                                ReferenceKind: ReferenceKind.GenericConstraint));
                        }
                    }
                }
            }
        }

        // Analyze properties - collect ALL referenced types recursively
        foreach (var property in type.Members.Properties)
        {
            var propTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(property.PropertyType, graph, graphData, propTypeRefs);

            foreach (var (fullName, targetNs) in propTypeRefs)
            {
                if (targetNs != null && targetNs != ns.Name)
                {
                    dependencies.Add(targetNs);
                    graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                        SourceNamespace: ns.Name,
                        SourceType: type.ClrFullName,
                        TargetNamespace: targetNs,
                        TargetType: fullName,
                        ReferenceKind: ReferenceKind.PropertyType));
                }
            }

            // Index parameters - collect recursively
            foreach (var indexParam in property.IndexParameters)
            {
                var indexTypeRefs = new HashSet<(string FullName, string? Namespace)>();
                CollectTypeReferences(indexParam.Type, graph, graphData, indexTypeRefs);

                foreach (var (fullName, targetNs) in indexTypeRefs)
                {
                    if (targetNs != null && targetNs != ns.Name)
                    {
                        dependencies.Add(targetNs);
                    }
                }
            }
        }

        // Analyze fields - collect ALL referenced types recursively
        foreach (var field in type.Members.Fields)
        {
            var fieldTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(field.FieldType, graph, graphData, fieldTypeRefs);

            foreach (var (fullName, targetNs) in fieldTypeRefs)
            {
                if (targetNs != null && targetNs != ns.Name)
                {
                    dependencies.Add(targetNs);
                    graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                        SourceNamespace: ns.Name,
                        SourceType: type.ClrFullName,
                        TargetNamespace: targetNs,
                        TargetType: fullName,
                        ReferenceKind: ReferenceKind.FieldType));
                }
            }
        }

        // Analyze events - collect ALL referenced types recursively
        foreach (var evt in type.Members.Events)
        {
            var eventTypeRefs = new HashSet<(string FullName, string? Namespace)>();
            CollectTypeReferences(evt.EventHandlerType, graph, graphData, eventTypeRefs);

            foreach (var (fullName, targetNs) in eventTypeRefs)
            {
                if (targetNs != null && targetNs != ns.Name)
                {
                    dependencies.Add(targetNs);
                    graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                        SourceNamespace: ns.Name,
                        SourceType: type.ClrFullName,
                        TargetNamespace: targetNs,
                        TargetType: fullName,
                        ReferenceKind: ReferenceKind.EventType));
                }
            }
        }
    }

    private static string? FindNamespaceForType(
        SymbolGraph graph,
        ImportGraphData graphData,
        TypeReference typeRef)
    {
        // Get normalized CLR lookup key (backtick arity, generic definition)
        var clrKey = GetClrLookupKey(typeRef);
        if (clrKey == null)
            return null; // Generic parameter, placeholder, or unknown

        // Fast O(1) lookup using CLR full name
        // CRITICAL: This now works for generic types because clrKey uses backtick form
        // Example: IEnumerable_1<T> → "System.Collections.Generic.IEnumerable`1"
        if (graphData.ClrFullNameToNamespace.TryGetValue(clrKey, out var ns))
            return ns;

        // Type might be external (not in our graph)
        return null;
    }

    /// <summary>
    /// Get normalized CLR lookup key for a TypeReference.
    /// Returns the generic definition name in CLR backtick format.
    /// This matches how TypeSymbol.ClrFullName is stored in the index.
    ///
    /// Examples:
    ///   IEnumerable_1&lt;T&gt;  → "System.Collections.Generic.IEnumerable`1"
    ///   Func_2&lt;T1,T2&gt;    → "System.Func`2"
    ///   Exception       → "System.Exception"
    /// </summary>
    private static string? GetClrLookupKey(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName, // Already in CLR backtick form
            NestedTypeReference nested => nested.FullReference.FullName, // NamedTypeReference behind the scenes
            ArrayTypeReference arr => GetClrLookupKey(arr.ElementType), // Recurse to element type
            PointerTypeReference ptr => GetClrLookupKey(ptr.PointeeType), // Recurse to pointee type
            ByRefTypeReference byref => GetClrLookupKey(byref.ReferencedType), // Recurse to referenced type
            GenericParameterReference => null, // Type parameters are local, never imported
            PlaceholderTypeReference => null, // Placeholders are unknown, no import
            _ => null // Unknown type reference
        };
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => GetTypeFullName(arr.ElementType),
            PointerTypeReference ptr => GetTypeFullName(ptr.PointeeType),
            ByRefTypeReference byref => GetTypeFullName(byref.ReferencedType),
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    /// <summary>
    /// Recursively collect all named type references from a TypeReference tree.
    /// This includes generic type arguments, array element types, etc.
    /// Returns set of (FullName, Namespace) pairs for all referenced named types.
    /// </summary>
    private static void CollectTypeReferences(
        TypeReference? typeRef,
        SymbolGraph graph,
        ImportGraphData graphData,
        HashSet<(string FullName, string? Namespace)> collected)
    {
        if (typeRef == null) return;

        switch (typeRef)
        {
            case NamedTypeReference named:
                var ns = FindNamespaceForType(graph, graphData, named);
                collected.Add((named.FullName, ns));

                // Recurse into type arguments
                foreach (var arg in named.TypeArguments)
                {
                    CollectTypeReferences(arg, graph, graphData, collected);
                }
                break;

            case NestedTypeReference nested:
                var nestedNs = FindNamespaceForType(graph, graphData, nested);
                collected.Add((nested.FullReference.FullName, nestedNs));

                // Recurse into type arguments of nested type
                foreach (var arg in nested.FullReference.TypeArguments)
                {
                    CollectTypeReferences(arg, graph, graphData, collected);
                }
                break;

            case ArrayTypeReference arr:
                CollectTypeReferences(arr.ElementType, graph, graphData, collected);
                break;

            case PointerTypeReference ptr:
                CollectTypeReferences(ptr.PointeeType, graph, graphData, collected);
                break;

            case ByRefTypeReference byref:
                CollectTypeReferences(byref.ReferencedType, graph, graphData, collected);
                break;

            case GenericParameterReference:
                // Generic parameters don't need imports - they're declared locally
                break;

            default:
                // Unknown type reference - skip
                break;
        }
    }
}

/// <summary>
/// Import graph data structure containing namespace dependencies and cross-references.
/// </summary>
public sealed class ImportGraphData
{
    /// <summary>
    /// Maps namespace name to set of namespaces it depends on.
    /// </summary>
    public Dictionary<string, HashSet<string>> NamespaceDependencies { get; init; } = new();

    /// <summary>
    /// Maps namespace name to set of type full names defined in that namespace.
    /// </summary>
    public Dictionary<string, HashSet<string>> NamespaceTypeIndex { get; init; } = new();

    /// <summary>
    /// Fast lookup map: CLR full name (with backtick arity) → owning namespace.
    /// Example: "System.Collections.Generic.IEnumerable`1" → "System.Collections.Generic"
    /// Built once during BuildNamespaceTypeIndex for O(1) lookups.
    /// </summary>
    public Dictionary<string, string> ClrFullNameToNamespace { get; init; } = new();

    /// <summary>
    /// List of all cross-namespace type references.
    /// </summary>
    public List<CrossNamespaceReference> CrossNamespaceReferences { get; init; } = new();
}

/// <summary>
/// Represents a single cross-namespace type reference.
/// </summary>
public sealed record CrossNamespaceReference(
    string SourceNamespace,
    string SourceType,
    string TargetNamespace,
    string TargetType,
    ReferenceKind ReferenceKind);

/// <summary>
/// Kind of cross-namespace reference.
/// </summary>
public enum ReferenceKind
{
    BaseClass,
    Interface,
    GenericConstraint,
    MethodReturn,
    MethodParameter,
    PropertyType,
    FieldType,
    EventType
}
