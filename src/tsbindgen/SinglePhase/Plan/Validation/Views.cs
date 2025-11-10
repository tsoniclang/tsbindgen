using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Shape;
using static tsbindgen.Core.TypeScriptReservedWords;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// View validation functions.
/// </summary>
internal static class Views
{
    internal static void Validate(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating explicit interface views...");

        int totalViewOnlyMembers = 0;
        int orphanedViewOnlyMembers = 0;
        int totalViews = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Collect ViewOnly members that are FROM interfaces (need explicit views)
                // Only check members with SourceInterface set (ViewPlanner needs this to group into views)
                // Exclude ViewOnly members from external interfaces (not in our graph)
                var viewOnlyMethods = type.Members.Methods
                    .Where(m => m.EmitScope == EmitScope.ViewOnly)
                    .Where(m => m.SourceInterface != null)
                    .Where(m => Shared.IsInterfaceInGraph(graph, m.SourceInterface))
                    .ToList();

                var viewOnlyProperties = type.Members.Properties
                    .Where(p => p.EmitScope == EmitScope.ViewOnly)
                    .Where(p => p.SourceInterface != null)
                    .Where(p => Shared.IsInterfaceInGraph(graph, p.SourceInterface))
                    .ToList();

                // Guard: indexer properties must NOT be ViewOnly (they should be converted or kept as properties)
                foreach (var property in viewOnlyProperties)
                {
                    if (property.IsIndexer)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.IndexerConflict,
                            "ERROR",
                            $"Indexer property {property.ClrName} in {type.ClrFullName} must not be ViewOnly (should be converted to methods or kept as property)");
                    }
                }

                totalViewOnlyMembers += viewOnlyMethods.Count + viewOnlyProperties.Count;

                if (viewOnlyMethods.Count == 0 && viewOnlyProperties.Count == 0)
                    continue;

                // Get planned views from type (attached by ViewPlanner)
                var plannedViews = type.ExplicitViews;

                if (plannedViews.Length == 0)
                {
                    // Interfaces and static classes can have ViewOnly members without explicit views
                    // - Interfaces: ViewOnly members are the interface definition itself
                    // - Static classes: ViewOnly members are extension methods that appear on interfaces
                    if (type.Kind == TypeKind.Interface || type.IsStatic)
                    {
                        continue; // This is expected and allowed
                    }

                    // For regular classes/structs, ViewOnly members without views is an error
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.ViewCoverageMismatch,
                        "ERROR",
                        $"Type {type.ClrFullName} has {viewOnlyMethods.Count + viewOnlyProperties.Count} ViewOnly members but no explicit views planned");
                    orphanedViewOnlyMembers += viewOnlyMethods.Count + viewOnlyProperties.Count;
                    continue;
                }

                totalViews += plannedViews.Length;

                // Check that each ViewOnly member appears in a view AND SourceInterface matches view Interface
                foreach (var method in viewOnlyMethods)
                {
                    var matchingView = plannedViews.FirstOrDefault(v =>
                        v.ViewMembers.Any(vm =>
                            vm.Kind == ViewPlanner.ViewMemberKind.Method &&
                            vm.StableId.Equals(method.StableId)));

                    if (matchingView == null)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.ViewCoverageMismatch,
                            "ERROR",
                            $"ViewOnly method {method.ClrName} in {type.ClrFullName} does not appear in any explicit view");
                        orphanedViewOnlyMembers++;
                        continue;
                    }

                    // Verify SourceInterface matches view Interface
                    if (method.SourceInterface != null)
                    {
                        var viewIfaceName = GetTypeFullName(matchingView.InterfaceReference);
                        var sourceIfaceName = GetTypeFullName(method.SourceInterface);
                        if (viewIfaceName != sourceIfaceName)
                        {
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.ViewCoverageMismatch,
                                "ERROR",
                                $"ViewOnly method {method.ClrName} in {type.ClrFullName} has SourceInterface {sourceIfaceName} but appears in view for {viewIfaceName}");
                            orphanedViewOnlyMembers++;
                        }
                    }
                }

                foreach (var property in viewOnlyProperties)
                {
                    var matchingView = plannedViews.FirstOrDefault(v =>
                        v.ViewMembers.Any(vm =>
                            vm.Kind == ViewPlanner.ViewMemberKind.Property &&
                            vm.StableId.Equals(property.StableId)));

                    if (matchingView == null)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.ViewCoverageMismatch,
                            "ERROR",
                            $"ViewOnly property {property.ClrName} in {type.ClrFullName} does not appear in any explicit view");
                        orphanedViewOnlyMembers++;
                        continue;
                    }

                    // Verify SourceInterface matches view Interface
                    if (property.SourceInterface != null)
                    {
                        var viewIfaceName = GetTypeFullName(matchingView.InterfaceReference);
                        var sourceIfaceName = GetTypeFullName(property.SourceInterface);
                        if (viewIfaceName != sourceIfaceName)
                        {
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.ViewCoverageMismatch,
                                "ERROR",
                                $"ViewOnly property {property.ClrName} in {type.ClrFullName} has SourceInterface {sourceIfaceName} but appears in view for {viewIfaceName}");
                            orphanedViewOnlyMembers++;
                        }
                    }
                }

                // Check that view interface types are resolvable
                foreach (var view in plannedViews)
                {
                    var ifaceExists = FindInterface(graph, view.InterfaceReference) != null;

                    if (!ifaceExists)
                    {
                        // Interface not in graph - it should be external and importable
                        // This is a warning, not an error (external interfaces are expected)
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.InterfaceNotFound,
                            "WARNING",
                            $"View {view.ViewPropertyName} in {type.ClrFullName} references external interface (should be imported)");
                    }
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {totalViewOnlyMembers} ViewOnly members across {totalViews} views");

        if (orphanedViewOnlyMembers > 0)
        {
            ctx.Log("PhaseGate", $"Found {orphanedViewOnlyMembers} orphaned ViewOnly members");
        }
    }

    internal static void ValidateIntegrity(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M3: Validating view integrity (3 hard rules)...");

        int totalViews = 0;
        int emptyViews = 0;
        int duplicateViews = 0;
        int invalidViewNames = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.ExplicitViews.Length == 0)
                    continue;

                totalViews += type.ExplicitViews.Length;

                // Track interfaces we've seen for this type (to detect duplicates)
                var seenInterfaces = new Dictionary<string, string>(); // StableId -> ViewPropertyName

                foreach (var view in type.ExplicitViews)
                {
                    // Rule 1: PG_VIEW_001 - Non-empty (must contain ≥1 ViewMember)
                    if (view.ViewMembers.Length == 0)
                    {
                        emptyViews++;
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_VIEW_001,
                            "ERROR",
                            $"Empty view (no members)\n" +
                            $"  type:     {type.ClrFullName}\n" +
                            $"  view:     {view.ViewPropertyName}\n" +
                            $"  iface:    {GetTypeReferenceName(view.InterfaceReference)}");
                    }

                    // Rule 2: PG_VIEW_002 - Unique target (no two views for same interface)
                    // Use interface StableId for comparison
                    var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);
                    if (seenInterfaces.TryGetValue(ifaceStableId, out var existingViewName))
                    {
                        duplicateViews++;
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_VIEW_002,
                            "ERROR",
                            $"Duplicate view for interface on type\n" +
                            $"  type:      {type.ClrFullName}\n" +
                            $"  interface: {GetTypeReferenceName(view.InterfaceReference)}\n" +
                            $"  views:     {existingViewName}, {view.ViewPropertyName}");
                    }
                    else
                    {
                        seenInterfaces[ifaceStableId] = view.ViewPropertyName;
                    }

                    // Rule 3: PG_VIEW_003 - Valid/sanitized view property name
                    // View property name must be a valid TS identifier
                    // If it's a reserved word, it must end with "_"
                    if (IsReservedWord(view.ViewPropertyName) &&
                        !view.ViewPropertyName.EndsWith("_"))
                    {
                        invalidViewNames++;
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_VIEW_003,
                            "ERROR",
                            $"Invalid/unsanitized view property name\n" +
                            $"  type:     {type.ClrFullName}\n" +
                            $"  view:     {view.ViewPropertyName}\n" +
                            $"  expected: {view.ViewPropertyName}_\n" +
                            $"  reason:   TypeScript reserved word");
                    }

                    // Check for invalid characters in view property name
                    if (!Shared.IsValidTypeScriptIdentifier(view.ViewPropertyName))
                    {
                        invalidViewNames++;
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_VIEW_003,
                            "ERROR",
                            $"Invalid view property name (contains invalid characters)\n" +
                            $"  type:     {type.ClrFullName}\n" +
                            $"  view:     {view.ViewPropertyName}\n" +
                            $"  reason:   Invalid TypeScript identifier");
                    }
                }
            }
        }

        ctx.Log("[PG]", $"M3: Validated {totalViews} views - {emptyViews} empty, {duplicateViews} duplicates, {invalidViewNames} invalid names");
    }

    internal static void ValidateMemberScoping(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M5: Validating view member name scoping...");

        int viewMemberCollisions = 0;
        int classSurfaceCollisions = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.ExplicitViews.Length == 0)
                    continue;

                // Collect class surface member names for PG_NAME_004 checks
                var classSurfaceNames = new HashSet<string>();
                foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface))
                {
                    var scope = ScopeFactory.ClassSurface(type, method.IsStatic);
                    var name = ctx.Renamer.GetFinalMemberName(method.StableId, scope);
                    classSurfaceNames.Add(name);
                }
                foreach (var prop in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface))
                {
                    var scope = ScopeFactory.ClassSurface(type, prop.IsStatic);
                    var name = ctx.Renamer.GetFinalMemberName(prop.StableId, scope);
                    classSurfaceNames.Add(name);
                }
                foreach (var field in type.Members.Fields.Where(f => f.EmitScope == EmitScope.ClassSurface))
                {
                    var scope = ScopeFactory.ClassSurface(type, field.IsStatic);
                    var name = ctx.Renamer.GetFinalMemberName(field.StableId, scope);
                    classSurfaceNames.Add(name);
                }
                foreach (var evt in type.Members.Events.Where(e => e.EmitScope == EmitScope.ClassSurface))
                {
                    var scope = ScopeFactory.ClassSurface(type, evt.IsStatic);
                    var name = ctx.Renamer.GetFinalMemberName(evt.StableId, scope);
                    classSurfaceNames.Add(name);
                }

                // Check each view
                foreach (var view in type.ExplicitViews)
                {
                    // Get interface StableId from TypeReference (no graph lookup, same as NameReservation)
                    var interfaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);
                    var interfaceTypeName = GetTypeReferenceName(view.InterfaceReference);

                    // PG_NAME_003: Check for collisions within this view
                    var viewMemberNames = new Dictionary<string, string>(); // emittedName -> first member description

                    foreach (var viewMember in view.ViewMembers)
                    {
                        string emittedName;
                        bool isStatic = FindMemberIsStatic(type, viewMember);

                        // Get emitted name based on member kind - use ViewSurface for each member
                        switch (viewMember.Kind)
                        {
                            case ViewPlanner.ViewMemberKind.Method:
                                var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                                if (method == null) continue;
                                var methodScope = ScopeFactory.ViewSurface(type, interfaceStableId, method.IsStatic);
                                emittedName = ctx.Renamer.GetFinalMemberName(method.StableId, methodScope);
                                break;
                            case ViewPlanner.ViewMemberKind.Property:
                                var prop = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                                if (prop == null) continue;
                                var propScope = ScopeFactory.ViewSurface(type, interfaceStableId, prop.IsStatic);
                                emittedName = ctx.Renamer.GetFinalMemberName(prop.StableId, propScope);
                                break;
                            case ViewPlanner.ViewMemberKind.Event:
                                var evt = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                                if (evt == null) continue;
                                var evtScope = ScopeFactory.ViewSurface(type, interfaceStableId, evt.IsStatic);
                                emittedName = ctx.Renamer.GetFinalMemberName(evt.StableId, evtScope);
                                break;
                            default:
                                continue;
                        }

                        // PG_NAME_003: Check for collision within view
                        if (viewMemberNames.TryGetValue(emittedName, out var firstMember))
                        {
                            viewMemberCollisions++;
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.PG_NAME_003,
                                "ERROR",
                                $"View member collision within view scope\n" +
                                $"  type:         {type.ClrFullName}\n" +
                                $"  view:         {view.ViewPropertyName}\n" +
                                $"  interface:    {interfaceTypeName}\n" +
                                $"  emitted name: {emittedName}\n" +
                                $"  first member: {firstMember}\n" +
                                $"  collision:    {viewMember.ClrName}");
                        }
                        else
                        {
                            viewMemberNames[emittedName] = viewMember.ClrName;
                        }

                        // PG_NAME_004: Check if view member name equals class surface name
                        // Only flag for ViewOnly members - members on both class surface and view naturally have the same name
                        bool isViewOnly = false;
                        switch (viewMember.Kind)
                        {
                            case ViewPlanner.ViewMemberKind.Method:
                                var methodForCheck = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                                isViewOnly = methodForCheck?.EmitScope == EmitScope.ViewOnly;
                                break;
                            case ViewPlanner.ViewMemberKind.Property:
                                var propForCheck = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                                isViewOnly = propForCheck?.EmitScope == EmitScope.ViewOnly;
                                break;
                            case ViewPlanner.ViewMemberKind.Event:
                                var evtForCheck = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                                isViewOnly = evtForCheck?.EmitScope == EmitScope.ViewOnly;
                                break;
                        }

                        if (isViewOnly && classSurfaceNames.Contains(emittedName))
                        {
                            ctx.Log("PhaseGate",
                                $"PG_NAME_004: ViewOnly member {type.ClrFullName}::{viewMember.ClrName} " +
                                $"emitted as '{emittedName}' shadows class surface in view {view.ViewPropertyName}");

                            classSurfaceCollisions++;
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.PG_NAME_004,
                                "ERROR",
                                $"View member name equals class surface name\n" +
                                $"  type:         {type.ClrFullName}\n" +
                                $"  view:         {view.ViewPropertyName}\n" +
                                $"  interface:    {interfaceTypeName}\n" +
                                $"  emitted name: {emittedName}\n" +
                                $"  view member:  {viewMember.ClrName}\n" +
                                $"  reason:       View member names must not shadow class surface members");
                        }
                    }
                }
            }
        }

        ctx.Log("[PG]", $"M5: View member name scoping - {viewMemberCollisions} view collisions, {classSurfaceCollisions} class surface collisions");
    }

    // Helper functions

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
            ArrayTypeReference arr => GetTypeFullName(arr.ElementType),
            PointerTypeReference ptr => GetTypeFullName(ptr.PointeeType),
            ByRefTypeReference byref => GetTypeFullName(byref.ReferencedType),
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static string GetTypeReferenceName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static bool FindMemberIsStatic(TypeSymbol type, ViewPlanner.ViewMember viewMember)
    {
        return viewMember.Kind switch
        {
            ViewPlanner.ViewMemberKind.Method =>
                type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId))?.IsStatic ?? false,
            ViewPlanner.ViewMemberKind.Property =>
                type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId))?.IsStatic ?? false,
            ViewPlanner.ViewMemberKind.Event =>
                type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId))?.IsStatic ?? false,
            _ => false
        };
    }
}
