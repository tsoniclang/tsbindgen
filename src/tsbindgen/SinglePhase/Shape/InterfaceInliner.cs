using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Core.Canon;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Inlines interface hierarchies - removes extends chains.
/// Flattens all inherited members into each interface so TypeScript doesn't need extends.
/// </summary>
public static class InterfaceInliner
{
    public static void Inline(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("InterfaceInliner", "Inlining interface hierarchies...");

        var interfacesToInline = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Interface)
            .ToList();

        ctx.Log("InterfaceInliner", $"Found {interfacesToInline.Count} interfaces to inline");

        foreach (var iface in interfacesToInline)
        {
            InlineInterface(ctx, iface, graph);
        }

        ctx.Log("InterfaceInliner", "Complete");
    }

    private static void InlineInterface(BuildContext ctx, TypeSymbol iface, SymbolGraph graph)
    {
        // Collect all members from this interface and all base interfaces
        var allMembers = new List<MethodSymbol>(iface.Members.Methods);
        var allProperties = new List<PropertySymbol>(iface.Members.Properties);
        var allEvents = new List<EventSymbol>(iface.Members.Events);

        // Walk up the interface hierarchy and collect all inherited members
        var visited = new HashSet<string>(); // Track visited interfaces by full name
        var toVisit = new Queue<TypeReference>(iface.Interfaces);

        while (toVisit.Count > 0)
        {
            var baseIfaceRef = toVisit.Dequeue();

            // Get the full name for tracking
            var fullName = GetTypeFullName(baseIfaceRef);
            if (visited.Contains(fullName))
                continue;

            visited.Add(fullName);

            // Find the base interface symbol in the graph
            var baseIface = FindInterfaceByReference(graph, baseIfaceRef);
            if (baseIface == null)
            {
                // External interface - we can't inline it, but log it
                ctx.Log("InterfaceInliner", $"Skipping external interface {fullName}");
                continue;
            }

            // Add all members from base interface
            allMembers.AddRange(baseIface.Members.Methods);
            allProperties.AddRange(baseIface.Members.Properties);
            allEvents.AddRange(baseIface.Members.Events);

            // Queue base interface's bases for visiting
            foreach (var grandparent in baseIface.Interfaces)
            {
                toVisit.Enqueue(grandparent);
            }
        }

        // Deduplicate members by canonical signature
        var uniqueMethods = DeduplicateMethods(ctx, allMembers);
        var uniqueProperties = DeduplicateProperties(ctx, allProperties);
        var uniqueEvents = DeduplicateEvents(ctx, allEvents);

        // Update the interface with inlined members (keep original constructors/fields)
        var newMembers = new TypeMembers
        {
            Methods = uniqueMethods.ToImmutableArray(),
            Properties = uniqueProperties.ToImmutableArray(),
            Fields = iface.Members.Fields, // Interfaces rarely have fields
            Events = uniqueEvents.ToImmutableArray(),
            Constructors = iface.Members.Constructors // Interfaces don't have constructors
        };

        // Replace members (using reflection since TypeSymbol properties have init-only setters)
        var membersProperty = typeof(TypeSymbol).GetProperty(nameof(TypeSymbol.Members));
        membersProperty!.SetValue(iface, newMembers);

        // Clear the Interfaces list (no more extends in TypeScript)
        var interfacesProperty = typeof(TypeSymbol).GetProperty(nameof(TypeSymbol.Interfaces));
        interfacesProperty!.SetValue(iface, ImmutableArray<TypeReference>.Empty);

        ctx.Log("InterfaceInliner", $"Inlined {iface.ClrFullName} - {uniqueMethods.Count} methods, {uniqueProperties.Count} properties");
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static TypeSymbol? FindInterfaceByReference(SymbolGraph graph, TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    private static IReadOnlyList<MethodSymbol> DeduplicateMethods(BuildContext ctx, List<MethodSymbol> methods)
    {
        var seen = new Dictionary<string, MethodSymbol>();

        foreach (var method in methods)
        {
            var sig = ctx.CanonicalizeMethod(
                method.ClrName,
                method.Parameters.Select(p => GetTypeFullName(p.Type)).ToList(),
                GetTypeFullName(method.ReturnType));

            if (!seen.ContainsKey(sig))
            {
                seen[sig] = method;
            }
            // If duplicate, keep the first one (deterministic)
        }

        return seen.Values.ToList();
    }

    private static IReadOnlyList<PropertySymbol> DeduplicateProperties(BuildContext ctx, List<PropertySymbol> properties)
    {
        var seen = new Dictionary<string, PropertySymbol>();

        foreach (var prop in properties)
        {
            // For indexers, include parameters in signature
            var indexParams = prop.IndexParameters.Select(p => GetTypeFullName(p.Type)).ToList();

            var sig = ctx.CanonicalizeProperty(
                prop.ClrName,
                indexParams,
                GetTypeFullName(prop.PropertyType));

            if (!seen.ContainsKey(sig))
            {
                seen[sig] = prop;
            }
        }

        return seen.Values.ToList();
    }

    private static IReadOnlyList<EventSymbol> DeduplicateEvents(BuildContext ctx, List<EventSymbol> events)
    {
        var seen = new Dictionary<string, EventSymbol>();

        foreach (var evt in events)
        {
            var sig = SignatureCanonicalizer.CanonicalizeEvent(
                evt.ClrName,
                GetTypeFullName(evt.EventHandlerType));

            if (!seen.ContainsKey(sig))
            {
                seen[sig] = evt;
            }
        }

        return seen.Values.ToList();
    }
}
