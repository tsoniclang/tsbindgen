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
        foreach (var ns in graph.Namespaces)
        {
            var typeNames = new HashSet<string>();

            foreach (var type in ns.Types)
            {
                typeNames.Add(type.ClrFullName);
            }

            graphData.NamespaceTypeIndex[ns.Name] = typeNames;
        }

        ctx.Log("ImportGraph", $"Indexed {graphData.NamespaceTypeIndex.Count} namespaces");
    }

    private static void AnalyzeNamespaceDependencies(
        BuildContext ctx,
        SymbolGraph graph,
        NamespaceSymbol ns,
        ImportGraphData graphData)
    {
        var dependencies = new HashSet<string>();

        foreach (var type in ns.Types)
        {
            // Analyze base class
            if (type.BaseType != null)
            {
                var baseNs = FindNamespaceForType(graph, graphData, type.BaseType);
                if (baseNs != null && baseNs != ns.Name)
                {
                    dependencies.Add(baseNs);
                    graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                        SourceNamespace: ns.Name,
                        SourceType: type.ClrFullName,
                        TargetNamespace: baseNs,
                        TargetType: GetTypeFullName(type.BaseType),
                        ReferenceKind: ReferenceKind.BaseClass));
                }
            }

            // Analyze interfaces
            foreach (var ifaceRef in type.Interfaces)
            {
                var ifaceNs = FindNamespaceForType(graph, graphData, ifaceRef);
                if (ifaceNs != null && ifaceNs != ns.Name)
                {
                    dependencies.Add(ifaceNs);
                    graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                        SourceNamespace: ns.Name,
                        SourceType: type.ClrFullName,
                        TargetNamespace: ifaceNs,
                        TargetType: GetTypeFullName(ifaceRef),
                        ReferenceKind: ReferenceKind.Interface));
                }
            }

            // Analyze generic parameters constraints
            foreach (var gp in type.GenericParameters)
            {
                foreach (var constraint in gp.Constraints)
                {
                    var constraintNs = FindNamespaceForType(graph, graphData, constraint);
                    if (constraintNs != null && constraintNs != ns.Name)
                    {
                        dependencies.Add(constraintNs);
                        graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                            SourceNamespace: ns.Name,
                            SourceType: type.ClrFullName,
                            TargetNamespace: constraintNs,
                            TargetType: GetTypeFullName(constraint),
                            ReferenceKind: ReferenceKind.GenericConstraint));
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
            // Return type
            var returnNs = FindNamespaceForType(graph, graphData, method.ReturnType);
            if (returnNs != null && returnNs != ns.Name)
            {
                dependencies.Add(returnNs);
                graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                    SourceNamespace: ns.Name,
                    SourceType: type.ClrFullName,
                    TargetNamespace: returnNs,
                    TargetType: GetTypeFullName(method.ReturnType),
                    ReferenceKind: ReferenceKind.MethodReturn));
            }

            // Parameters
            foreach (var param in method.Parameters)
            {
                var paramNs = FindNamespaceForType(graph, graphData, param.Type);
                if (paramNs != null && paramNs != ns.Name)
                {
                    dependencies.Add(paramNs);
                    graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                        SourceNamespace: ns.Name,
                        SourceType: type.ClrFullName,
                        TargetNamespace: paramNs,
                        TargetType: GetTypeFullName(param.Type),
                        ReferenceKind: ReferenceKind.MethodParameter));
                }
            }

            // Generic parameters constraints
            foreach (var gp in method.GenericParameters)
            {
                foreach (var constraint in gp.Constraints)
                {
                    var constraintNs = FindNamespaceForType(graph, graphData, constraint);
                    if (constraintNs != null && constraintNs != ns.Name)
                    {
                        dependencies.Add(constraintNs);
                    }
                }
            }
        }

        // Analyze properties
        foreach (var property in type.Members.Properties)
        {
            var propNs = FindNamespaceForType(graph, graphData, property.PropertyType);
            if (propNs != null && propNs != ns.Name)
            {
                dependencies.Add(propNs);
                graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                    SourceNamespace: ns.Name,
                    SourceType: type.ClrFullName,
                    TargetNamespace: propNs,
                    TargetType: GetTypeFullName(property.PropertyType),
                    ReferenceKind: ReferenceKind.PropertyType));
            }

            // Index parameters
            foreach (var indexParam in property.IndexParameters)
            {
                var indexNs = FindNamespaceForType(graph, graphData, indexParam.Type);
                if (indexNs != null && indexNs != ns.Name)
                {
                    dependencies.Add(indexNs);
                }
            }
        }

        // Analyze fields
        foreach (var field in type.Members.Fields)
        {
            var fieldNs = FindNamespaceForType(graph, graphData, field.FieldType);
            if (fieldNs != null && fieldNs != ns.Name)
            {
                dependencies.Add(fieldNs);
                graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                    SourceNamespace: ns.Name,
                    SourceType: type.ClrFullName,
                    TargetNamespace: fieldNs,
                    TargetType: GetTypeFullName(field.FieldType),
                    ReferenceKind: ReferenceKind.FieldType));
            }
        }

        // Analyze events
        foreach (var evt in type.Members.Events)
        {
            var eventNs = FindNamespaceForType(graph, graphData, evt.EventHandlerType);
            if (eventNs != null && eventNs != ns.Name)
            {
                dependencies.Add(eventNs);
                graphData.CrossNamespaceReferences.Add(new CrossNamespaceReference(
                    SourceNamespace: ns.Name,
                    SourceType: type.ClrFullName,
                    TargetNamespace: eventNs,
                    TargetType: GetTypeFullName(evt.EventHandlerType),
                    ReferenceKind: ReferenceKind.EventType));
            }
        }
    }

    private static string? FindNamespaceForType(
        SymbolGraph graph,
        ImportGraphData graphData,
        TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        // Check each namespace's type index
        foreach (var (nsName, typeNames) in graphData.NamespaceTypeIndex)
        {
            if (typeNames.Contains(fullName))
                return nsName;
        }

        // Type might be external (not in our graph)
        return null;
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
