using System.Collections.Generic;
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
    /// <summary>
    /// Global storage for planned views, keyed by type full name.
    /// Emitters will use this to generate view properties and companion interfaces.
    /// </summary>
    private static Dictionary<string, List<ExplicitView>> _plannedViewsByType = new();

    public static void Plan(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ViewPlanner: Planning explicit interface views...");

        // Clear previous planning
        _plannedViewsByType.Clear();

        var classesAndStructs = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Where(t => t.Kind == TypeKind.Class || t.Kind == TypeKind.Struct)
            .ToList();

        int totalViews = 0;

        foreach (var type in classesAndStructs)
        {
            var views = PlanViewsForType(ctx, graph, type);
            totalViews += views;
        }

        ctx.Log($"ViewPlanner: Planned {totalViews} explicit interface views");
    }

    private static int PlanViewsForType(BuildContext ctx, SymbolGraph graph, TypeSymbol type)
    {
        // Get explicit views from StructuralConformance
        var explicitViews = StructuralConformance.GetExplicitViews(type.ClrFullName);

        if (explicitViews.Count == 0)
            return 0;

        var plannedViews = new List<ExplicitView>();

        foreach (var ifaceRef in explicitViews)
        {
            var iface = FindInterface(graph, ifaceRef);
            if (iface == null)
                continue;

            // Create view name: As_IList for IList, As_IEnumerable_1 for IEnumerable<T>
            var viewName = CreateViewName(ifaceRef);

            // Filter ViewOnly members to those specific to this interface
            var viewMembers = FilterViewMembers(type, iface);

            var view = new ExplicitView(
                InterfaceReference: ifaceRef,
                ViewPropertyName: viewName,
                ViewMembers: viewMembers);

            plannedViews.Add(view);

            ctx.Log($"ViewPlanner: Created view '{viewName}' for {type.ClrFullName} -> {iface.ClrFullName}");
        }

        // Note: View names are now self-disambiguating via type arguments (of_type_and_type pattern)
        // No need for numeric suffix disambiguation

        // Store planned views
        _plannedViewsByType[type.ClrFullName] = plannedViews;

        return plannedViews.Count;
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
                Symbol: method,
                ClrName: method.ClrName));
        }

        foreach (var property in viewOnlyProperties)
        {
            viewMembers.Add(new ViewMember(
                Kind: ViewMemberKind.Property,
                Symbol: property,
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

    /// <summary>
    /// Get planned views for a type.
    /// Called by emitters to generate view properties.
    /// </summary>
    public static List<ExplicitView> GetPlannedViews(string typeFullName)
    {
        if (_plannedViewsByType.TryGetValue(typeFullName, out var views))
            return views;

        return new List<ExplicitView>();
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
    public record ExplicitView(
        TypeReference InterfaceReference,
        string ViewPropertyName,
        List<ViewMember> ViewMembers);

    public record ViewMember(
        ViewMemberKind Kind,
        object Symbol, // MethodSymbol or PropertySymbol
        string ClrName);

    public enum ViewMemberKind
    {
        Method,
        Property,
        Event
    }
}
