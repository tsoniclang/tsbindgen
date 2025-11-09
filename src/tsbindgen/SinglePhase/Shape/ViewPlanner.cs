using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using tsbindgen.SinglePhase.Renaming;
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
                // DEBUG: Log what's in plannedViews before attaching
                var canaryTypes = new[] { "System.Decimal", "System.Array", "System.CharEnumerator", "System.Enum", "System.TypeInfo" };
                if (canaryTypes.Contains(type.ClrFullName))
                {
                    ctx.Log("ViewPlanner", $"[DEBUG] Before WithUpdatedType: {type.StableId} has {plannedViews.Count} views");
                    foreach (var view in plannedViews)
                    {
                        ctx.Log("ViewPlanner", $"  view={view.ViewPropertyName} ViewMembers.Length={view.ViewMembers.Length}");
                    }
                }

                // Attach views to type immutably
                // Use StableId string for lookup to handle types with same CLR name from different assemblies
                updatedGraph = updatedGraph.WithUpdatedType(type.StableId.ToString(), t =>
                    t.WithExplicitViews(plannedViews.ToImmutableArray()));
                totalViews += plannedViews.Count;

                // DEBUG: Verify views were attached
                if (canaryTypes.Contains(type.ClrFullName))
                {
                    var verifyType = updatedGraph.Namespaces
                        .SelectMany(ns => ns.Types)
                        .FirstOrDefault(t => t.StableId.ToString() == type.StableId.ToString());
                    if (verifyType != null)
                    {
                        ctx.Log("ViewPlanner", $"[DEBUG] After WithUpdatedType: {verifyType.StableId} has {verifyType.ExplicitViews.Length} views");
                        foreach (var view in verifyType.ExplicitViews)
                        {
                            ctx.Log("ViewPlanner", $"  view={view.ViewPropertyName} ViewMembers.Length={view.ViewMembers.Length}");
                        }
                    }
                    else
                    {
                        ctx.Log("ViewPlanner", $"[DEBUG] ERROR: Could not find type {type.StableId} in updatedGraph!");
                    }
                }
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
            return new List<ExplicitView>();
        }

        // M5 FIX: Collect ALL ViewOnly members with SourceInterface (no graph filtering)
        // Every ViewOnly member MUST be represented in an ExplicitView
        var viewOnlyMembers = new List<(TypeReference ifaceRef, object member, ViewMemberKind kind, MemberStableId stableId, string clrName)>();

        foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ViewOnly && m.SourceInterface != null))
        {
            viewOnlyMembers.Add((method.SourceInterface!, method, ViewMemberKind.Method, (MemberStableId)method.StableId, method.ClrName));
        }

        foreach (var property in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ViewOnly && p.SourceInterface != null))
        {
            viewOnlyMembers.Add((property.SourceInterface!, property, ViewMemberKind.Property, (MemberStableId)property.StableId, property.ClrName));
        }

        foreach (var evt in type.Members.Events.Where(e => e.EmitScope == EmitScope.ViewOnly && e.SourceInterface != null))
        {
            viewOnlyMembers.Add((evt.SourceInterface!, evt, ViewMemberKind.Event, (MemberStableId)evt.StableId, evt.ClrName));
        }

        if (viewOnlyMembers.Count == 0)
        {
            return new List<ExplicitView>();
        }

        // Group by Interface StableId (assembly-qualified identifier)
        var groupsByInterfaceStableId = viewOnlyMembers
            .GroupBy(x => GetInterfaceStableId(x.ifaceRef))
            .OrderBy(g => g.Key)  // Deterministic ordering
            .ToList();

        // Start with existing views (if any) as dictionary by interface StableId
        var viewsByInterfaceStableId = type.ExplicitViews
            .ToDictionary(v => GetInterfaceStableId(v.InterfaceReference), v => v);

        var plannedViews = new List<ExplicitView>();

        foreach (var group in groupsByInterfaceStableId)
        {
            var ifaceStableId = group.Key;
            var ifaceRef = group.First().ifaceRef;

            // Collect new ViewMembers from this group (sorted by StableId for determinism)
            var newViewMembers = group
                .Select(x => new ViewMember(
                    Kind: x.kind,
                    StableId: x.stableId,
                    ClrName: x.clrName))
                .OrderBy(vm => vm.StableId.ToString())
                .ToList();

            ExplicitView view;

            if (viewsByInterfaceStableId.TryGetValue(ifaceStableId, out var existingView))
            {
                // MERGE: Union existing ViewMembers with new ones by MemberStableId
                var existingMemberIds = existingView.ViewMembers.Select(vm => vm.StableId).ToHashSet();
                var mergedMembers = existingView.ViewMembers
                    .Concat(newViewMembers.Where(vm => !existingMemberIds.Contains(vm.StableId)))
                    .OrderBy(vm => vm.StableId.ToString())
                    .ToImmutableArray();

                view = existingView with { ViewMembers = mergedMembers };

                ctx.Log("trace:viewplanner",
                    $"Merged view for {type.StableId} -> {ifaceStableId}: " +
                    $"existing={existingView.ViewMembers.Length} new={newViewMembers.Count} merged={mergedMembers.Length}");
            }
            else
            {
                // CREATE: New view
                var viewName = CreateViewName(ifaceRef);

                view = new ExplicitView(
                    InterfaceReference: ifaceRef,
                    ViewPropertyName: viewName,
                    ViewMembers: newViewMembers.ToImmutableArray());

                ctx.Log("trace:viewplanner",
                    $"Created view '{viewName}' for {type.StableId} -> {ifaceStableId} ({newViewMembers.Count} members)");
            }

            plannedViews.Add(view);
        }

        ctx.Log("trace:viewplanner", $"{type.StableId}: planned {plannedViews.Count} views (total ViewOnly members: {viewOnlyMembers.Count})");

        return plannedViews;
    }

    /// <summary>
    /// Get the StableId for an interface reference (assembly-qualified identifier).
    /// </summary>
    private static string GetInterfaceStableId(TypeReference ifaceRef)
    {
        return ifaceRef switch
        {
            NamedTypeReference named => $"{named.AssemblyName}:{named.FullName}",
            NestedTypeReference nested => $"{nested.DeclaringType}+{nested.NestedName}",
            _ => GetTypeFullName(ifaceRef)  // Fallback
        };
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
