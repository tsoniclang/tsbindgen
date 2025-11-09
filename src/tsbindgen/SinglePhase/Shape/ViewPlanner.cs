using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;
using tsbindgen.SinglePhase.Normalize;

namespace tsbindgen.SinglePhase.Shape;

/// <summary>
/// Plans explicit interface views (As_IInterface properties).
/// Creates As_IInterface properties for interfaces that couldn't be structurally implemented.
/// These properties expose interface-specific members that were marked ViewOnly.
/// </summary>
public static class ViewPlanner
{
    public static SymbolGraph Plan(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ViewPlanner", "Planning explicit interface views...");

        var classesAndStructs = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Class || t.Kind == TypeKind.Struct)
            .ToList();

        ctx.Log("ViewPlanner", $"Found {classesAndStructs.Count} classes/structs to process");
        var zlibHandle = classesAndStructs.FirstOrDefault(t => t.ClrFullName.Contains("ZLibStreamHandle"));
        if (zlibHandle != null)
        {
            ctx.Log("ViewPlanner", $"ZLibStreamHandle FOUND in classes/structs: {zlibHandle.ClrFullName}");
        }
        else
        {
            ctx.Log("ViewPlanner", $"ZLibStreamHandle NOT FOUND in classes/structs");
        }

        int totalViews = 0;
        var updatedGraph = graph;

        foreach (var type in classesAndStructs)
        {
            var plannedViews = PlanViewsForType(ctx, updatedGraph, type);
            if (plannedViews.Count > 0)
            {
                // Attach views to type immutably
                // Use StableId string for lookup to handle types with same CLR name from different assemblies
                updatedGraph = updatedGraph.WithUpdatedType(type.StableId.ToString(), t =>
                    t.WithExplicitViews(plannedViews.ToImmutableArray()));
                totalViews += plannedViews.Count;
            }
        }

        ctx.Log("ViewPlanner", $"Planned {totalViews} explicit interface views");
        return updatedGraph;
    }

    private static List<ExplicitView> PlanViewsForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type)
    {
        // Skip interfaces and static types - they ARE the view, no explicit views needed
        if (type.Kind == TypeKind.Interface || type.IsStatic)
        {
            ctx.Log("ViewPlanner", $"Skipping {type.ClrFullName} (interface or static type)");
            return new List<ExplicitView>();
        }

        // Debug ZLibStreamHandle
        if (type.ClrFullName.Contains("ZLibStreamHandle"))
        {
            ctx.Log("ViewPlanner", $"Planning views for {type.ClrFullName}");
        }

        // Build candidate interface set from:
        // 1. All interfaces declared on the type (type.Interfaces)
        // 2. PLUS all SourceInterfaces from ViewOnly members
        var candidateInterfaceNames = new HashSet<string>();

        // Add from type.Interfaces
        foreach (var ifaceRef in type.Interfaces)
        {
            candidateInterfaceNames.Add(GetTypeFullName(ifaceRef));
        }

        // Collect all ViewOnly members that have SourceInterface
        var viewOnlyWithInterface = new List<(TypeReference ifaceRef, object member, ViewMemberKind kind, string clrName)>();

        foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ViewOnly && m.SourceInterface != null))
        {
            candidateInterfaceNames.Add(GetTypeFullName(method.SourceInterface!));
            viewOnlyWithInterface.Add((method.SourceInterface!, method, ViewMemberKind.Method, method.ClrName));
        }

        foreach (var property in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ViewOnly && p.SourceInterface != null))
        {
            candidateInterfaceNames.Add(GetTypeFullName(property.SourceInterface!));
            viewOnlyWithInterface.Add((property.SourceInterface!, property, ViewMemberKind.Property, property.ClrName));
        }

        foreach (var evt in type.Members.Events.Where(e => e.EmitScope == EmitScope.ViewOnly && e.SourceInterface != null))
        {
            candidateInterfaceNames.Add(GetTypeFullName(evt.SourceInterface!));
            viewOnlyWithInterface.Add((evt.SourceInterface!, evt, ViewMemberKind.Event, evt.ClrName));
        }

        if (viewOnlyWithInterface.Count == 0)
        {
            ctx.Log("ViewPlanner", $"{type.ClrFullName} has no ViewOnly members with SourceInterface");
            return new List<ExplicitView>();
        }

        ctx.Log("ViewPlanner", $"{type.ClrFullName} has {viewOnlyWithInterface.Count} ViewOnly members with SourceInterface");
        ctx.Log("ViewPlanner", $"Candidate interfaces: {string.Join(", ", candidateInterfaceNames)}");

        // Filter candidates to only those that exist in the graph
        // We don't create views for external interfaces (like System.IDisposable from another assembly)
        var candidatesInGraph = candidateInterfaceNames
            .Where(name => GlobalInterfaceIndex.ContainsInterface(name))
            .ToHashSet();

        ctx.Log("ViewPlanner", $"Interfaces in graph: {string.Join(", ", candidatesInGraph)}");
        ctx.Log("ViewPlanner", $"Excluded external interfaces: {string.Join(", ", candidateInterfaceNames.Except(candidatesInGraph))}");

        // Group ViewOnly members by SourceInterface, but only for interfaces in the graph
        var groupsByInterface = viewOnlyWithInterface
            .Where(x => candidatesInGraph.Contains(GetTypeFullName(x.ifaceRef)))
            .GroupBy(x => GetTypeFullName(x.ifaceRef))
            .ToList();

        ctx.Log("ViewPlanner", $"Grouped into {groupsByInterface.Count} interface groups:");
        foreach (var group in groupsByInterface)
        {
            ctx.Log("ViewPlanner", $"  - {group.Key}: {group.Count()} members");
        }

        // Sanity check: verify each member's SourceInterface matches the group key
        foreach (var group in groupsByInterface)
        {
            var groupKey = group.Key;
            foreach (var member in group)
            {
                if (member.ifaceRef != null)
                {
                    var memberIfaceName = GetTypeFullName(member.ifaceRef);
                    if (memberIfaceName != groupKey)
                    {
                        ctx.Log("ViewPlanner", $"WARNING - Member {member.clrName} grouped under {groupKey} but has SourceInterface {memberIfaceName}");
                    }
                }
            }
        }

        var plannedViews = new List<ExplicitView>();

        foreach (var group in groupsByInterface)
        {
            var ifaceFullName = group.Key;
            var ifaceRef = group.First().ifaceRef;

            // Create view name: As_IList for IList, As_IEnumerable_1 for IEnumerable<T>
            var viewName = CreateViewName(ifaceRef);

            // TODO: Ask Renamer for unique name in type scope (not implemented yet)
            // For now, use the created name directly

            // Collect all members for this interface
            var viewMembers = group.Select(x => new ViewMember(
                Kind: x.kind,
                StableId: GetStableIdFromMember(x.member, x.kind),
                ClrName: x.clrName)).ToImmutableArray();

            var view = new ExplicitView(
                InterfaceReference: ifaceRef,
                ViewPropertyName: viewName,
                ViewMembers: viewMembers);

            plannedViews.Add(view);

            ctx.Log("ViewPlanner", $"Created view '{viewName}' for {type.ClrFullName} -> {ifaceFullName} ({viewMembers.Length} members)");
        }

        return plannedViews;
    }

    private static string CreateViewName(TypeReference ifaceRef)
    {
        // Create: As_IInterface for non-generic interfaces
        // Create: As_IEnumerable_1_of_string for IEnumerable<string>
        // Create: As_IDictionary_2_of_string_and_int for IDictionary<string, int>

        var baseName = ifaceRef switch
        {
            NamedTypeReference named => named.Name,
            NestedTypeReference nested => nested.NestedName,
            _ => "Interface"
        };

        // Sanitize: replace backtick with underscore (IEnumerable`1 → IEnumerable_1)
        baseName = baseName.Replace('`', '_');

        // Build view name with type arguments for disambiguation
        var viewName = $"As_{baseName}";

        // Add type arguments if generic
        if (ifaceRef is NamedTypeReference { TypeArguments.Count: > 0 } namedType)
        {
            var typeArgNames = namedType.TypeArguments
                .Select(arg => GetTypeArgumentName(arg))
                .ToList();

            viewName += "_of_" + string.Join("_and_", typeArgNames);
        }

        return viewName;
    }

    private static string GetTypeArgumentName(TypeReference typeRef)
    {
        // Convert type reference to sanitized name for view naming
        return typeRef switch
        {
            NamedTypeReference named => SanitizeTypeName(named.Name),
            NestedTypeReference nested => SanitizeTypeName(nested.NestedName),
            GenericParameterReference gp => gp.Name, // Use parameter name directly (T, U, etc.)
            ArrayTypeReference arr => GetTypeArgumentName(arr.ElementType) + "_array",
            PointerTypeReference ptr => GetTypeArgumentName(ptr.PointeeType) + "_ptr",
            ByRefTypeReference byref => GetTypeArgumentName(byref.ReferencedType) + "_ref",
            _ => "unknown"
        };
    }

    private static string SanitizeTypeName(string name)
    {
        // Remove generic arity backticks and sanitize for identifier use
        return name.Replace('`', '_').Replace('.', '_');
    }

    private static List<ViewMember> FilterViewMembers(TypeSymbol type, TypeSymbol iface)
    {
        var viewMembers = new List<ViewMember>();

        // Build interface signature sets using normalized signatures for precise matching
        var interfaceMethodSignatures = iface.Members.Methods
            .Select(m => SignatureNormalization.NormalizeMethod(m))
            .ToHashSet();

        var interfacePropertySignatures = iface.Members.Properties
            .Select(p => SignatureNormalization.NormalizeProperty(p))
            .ToHashSet();

        // Find all ViewOnly members that match this interface's surface
        var viewOnlyMethods = type.Members.Methods
            .Where(m => m.EmitScope == EmitScope.ViewOnly && IsFromInterface(m, iface))
            .Where(m => interfaceMethodSignatures.Contains(SignatureNormalization.NormalizeMethod(m)))
            .ToList();

        var viewOnlyProperties = type.Members.Properties
            .Where(p => p.EmitScope == EmitScope.ViewOnly && IsFromInterface(p, iface))
            .Where(p => interfacePropertySignatures.Contains(SignatureNormalization.NormalizeProperty(p)))
            .ToList();

        foreach (var method in viewOnlyMethods)
        {
            viewMembers.Add(new ViewMember(
                Kind: ViewMemberKind.Method,
                StableId: method.StableId,
                ClrName: method.ClrName));
        }

        foreach (var property in viewOnlyProperties)
        {
            viewMembers.Add(new ViewMember(
                Kind: ViewMemberKind.Property,
                StableId: property.StableId,
                ClrName: property.ClrName));
        }

        return viewMembers;
    }

    private static bool IsFromInterface(MethodSymbol method, TypeSymbol iface)
    {
        // Check if method's SourceInterface matches
        if (method.SourceInterface != null)
        {
            var sourceName = GetTypeFullName(method.SourceInterface);
            return sourceName == iface.ClrFullName;
        }

        // Or check provenance
        return method.Provenance == MemberProvenance.FromInterface ||
               method.Provenance == MemberProvenance.Synthesized;
    }

    private static bool IsFromInterface(PropertySymbol property, TypeSymbol iface)
    {
        if (property.SourceInterface != null)
        {
            var sourceName = GetTypeFullName(property.SourceInterface);
            return sourceName == iface.ClrFullName;
        }

        return property.Provenance == MemberProvenance.FromInterface ||
               property.Provenance == MemberProvenance.Synthesized;
    }

    private static TypeSymbol? FindInterface(SymbolGraph graph, TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    private static string GetTypeFullName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            GenericParameterReference gp => gp.Name,
            ArrayTypeReference arr => $"{GetTypeFullName(arr.ElementType)}[]",
            PointerTypeReference ptr => $"{GetTypeFullName(ptr.PointeeType)}*",
            ByRefTypeReference byref => $"{GetTypeFullName(byref.ReferencedType)}&",
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    /// <summary>
    /// Information about an explicit interface view.
    /// </summary>
    private static MemberStableId GetStableIdFromMember(object member, ViewMemberKind kind)
    {
        return kind switch
        {
            ViewMemberKind.Method => ((MethodSymbol)member).StableId,
            ViewMemberKind.Property => ((PropertySymbol)member).StableId,
            ViewMemberKind.Event => ((EventSymbol)member).StableId,
            _ => throw new InvalidOperationException($"Unknown ViewMemberKind: {kind}")
        };
    }

    public sealed record ExplicitView(
        TypeReference InterfaceReference,
        string ViewPropertyName,
        ImmutableArray<ViewMember> ViewMembers);

    public record ViewMember(
        ViewMemberKind Kind,
        MemberStableId StableId,
        string ClrName);

    public enum ViewMemberKind
    {
        Method,
        Property,
        Event
    }
}
