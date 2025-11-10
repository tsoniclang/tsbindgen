using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Shape;

namespace tsbindgen.SinglePhase.Normalize.Naming;

/// <summary>
/// Name reservation functions - reserves names through the central Renamer.
/// </summary>
internal static class Reservation
{
    /// <summary>
    /// Reserve member names without mutating symbols (Phase 1).
    /// Returns (Reserved, Skipped) counts.
    /// Skips members that already have rename decisions from earlier passes.
    /// </summary>
    internal static (int Reserved, int Skipped) ReserveMemberNamesOnly(BuildContext ctx, TypeSymbol type)
    {
        // Base scope for member reservations (ReserveMemberName will add #instance/#static)
        var typeScope = ScopeFactory.ClassBase(type);

        int reserved = 0;
        int skipped = 0;

        foreach (var method in type.Members.Methods.OrderBy(m => m.ClrName))
        {
            // DEBUG: Log all Decimal To* methods
            bool isDecimalToMethod = type.ClrFullName == "System.Decimal" && method.ClrName.StartsWith("To");

            // M5: Skip ViewOnly members - they'll be reserved in view-scoped reservation
            if (method.EmitScope == EmitScope.ViewOnly)
            {
                if (isDecimalToMethod)
                    ctx.Log("name-resv-debug", $"Skip ViewOnly: {method.ClrName} (static={method.IsStatic})");
                skipped++;
                continue;
            }

            // Guard: Never reserve names for Unspecified members - this is a developer mistake
            if (method.EmitScope == EmitScope.Unspecified)
            {
                throw new InvalidOperationException(
                    $"Cannot reserve name for method with Unspecified EmitScope: {method.StableId} in {type.ClrFullName}. " +
                    "EmitScope must be explicitly set during Shape phase.");
            }

            // Skip Omitted members - they don't need name reservations
            if (method.EmitScope == EmitScope.Omitted)
            {
                skipped++;
                continue;
            }

            // Check if already renamed by earlier pass (e.g., HiddenMemberPlanner, IndexerPlanner)
            // M5 FIX: Pass class scope and isStatic to TryGetDecision
            var methodCheckScope = ScopeFactory.ClassSurface(type, method.IsStatic);
            if (ctx.Renamer.TryGetDecision(method.StableId, methodCheckScope, out var existingDecision))
            {
                if (isDecimalToMethod)
                    ctx.Log("name-resv-debug", $"Skip existing: {method.ClrName} (from {existingDecision.DecisionSource}, final={existingDecision.Final})");
                skipped++;
                continue;
            }

            var reason = method.Provenance switch
            {
                MemberProvenance.Original => "MethodDeclaration",
                MemberProvenance.FromInterface => "InterfaceMember",
                MemberProvenance.Synthesized => "SynthesizedMember",
                _ => "Unknown"
            };

            var requested = Shared.ComputeMethodBase(method);
            if (isDecimalToMethod)
                ctx.Log("name-resv-debug", $"Reserving: {method.ClrName} (static={method.IsStatic}, requested={requested})");
            ctx.Renamer.ReserveMemberName(method.StableId, requested, typeScope, reason, method.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var property in type.Members.Properties.OrderBy(p => p.ClrName))
        {
            // M5: Skip ViewOnly members - they'll be reserved in view-scoped reservation
            if (property.EmitScope == EmitScope.ViewOnly)
            {
                skipped++;
                continue;
            }

            // Guard: Never reserve names for Unspecified members - this is a developer mistake
            if (property.EmitScope == EmitScope.Unspecified)
            {
                throw new InvalidOperationException(
                    $"Cannot reserve name for property with Unspecified EmitScope: {property.StableId} in {type.ClrFullName}. " +
                    "EmitScope must be explicitly set during Shape phase.");
            }

            // Skip Omitted members - they don't need name reservations
            if (property.EmitScope == EmitScope.Omitted)
            {
                skipped++;
                continue;
            }

            // Check if already renamed (e.g., IndexerPlanner)
            // M5 FIX: Pass class scope and isStatic to TryGetDecision
            var propertyCheckScope = ScopeFactory.ClassSurface(type, property.IsStatic);
            if (ctx.Renamer.TryGetDecision(property.StableId, propertyCheckScope, out var existingDecision))
            {
                skipped++;
                continue;
            }

            var reason = property.IsIndexer ? "IndexerProperty" : "PropertyDeclaration";
            var requested = Shared.RequestedBaseForMember(property.ClrName);
            ctx.Renamer.ReserveMemberName(property.StableId, requested, typeScope, reason, property.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var field in type.Members.Fields.OrderBy(f => f.ClrName))
        {
            // Guard: Never reserve names for Unspecified members - this is a developer mistake
            if (field.EmitScope == EmitScope.Unspecified)
            {
                throw new InvalidOperationException(
                    $"Cannot reserve name for field with Unspecified EmitScope: {field.StableId} in {type.ClrFullName}. " +
                    "EmitScope must be explicitly set during Shape phase.");
            }

            // Skip Omitted members - they don't need name reservations
            if (field.EmitScope == EmitScope.Omitted)
            {
                skipped++;
                continue;
            }

            // Check if already renamed
            // M5 FIX: Pass class scope and isStatic to TryGetDecision
            var fieldCheckScope = ScopeFactory.ClassSurface(type, field.IsStatic);
            if (ctx.Renamer.TryGetDecision(field.StableId, fieldCheckScope, out var existingDecision))
            {
                skipped++;
                continue;
            }

            var reason = field.IsConst ? "ConstantField" : "FieldDeclaration";
            var requested = Shared.RequestedBaseForMember(field.ClrName);
            ctx.Renamer.ReserveMemberName(field.StableId, requested, typeScope, reason, field.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var ev in type.Members.Events.OrderBy(e => e.ClrName))
        {
            // Guard: Never reserve names for Unspecified members - this is a developer mistake
            if (ev.EmitScope == EmitScope.Unspecified)
            {
                throw new InvalidOperationException(
                    $"Cannot reserve name for event with Unspecified EmitScope: {ev.StableId} in {type.ClrFullName}. " +
                    "EmitScope must be explicitly set during Shape phase.");
            }

            // Skip Omitted members - they don't need name reservations
            if (ev.EmitScope == EmitScope.Omitted)
            {
                skipped++;
                continue;
            }

            // Check if already renamed
            // M5 FIX: Pass class scope and isStatic to TryGetDecision
            var eventCheckScope = ScopeFactory.ClassSurface(type, ev.IsStatic);
            if (ctx.Renamer.TryGetDecision(ev.StableId, eventCheckScope, out var existingDecision))
            {
                skipped++;
                continue;
            }

            var requested = Shared.RequestedBaseForMember(ev.ClrName);
            ctx.Renamer.ReserveMemberName(ev.StableId, requested, typeScope, reason: "EventDeclaration", ev.IsStatic, "NameReservation");
            reserved++;
        }

        foreach (var ctor in type.Members.Constructors)
        {
            // Check if already renamed
            // M5 FIX: Pass class scope and isStatic to TryGetDecision
            var ctorCheckScope = ScopeFactory.ClassSurface(type, ctor.IsStatic);
            if (ctx.Renamer.TryGetDecision(ctor.StableId, ctorCheckScope, out var existingDecision))
            {
                skipped++;
                continue;
            }

            ctx.Renamer.ReserveMemberName(ctor.StableId, "constructor", typeScope, "ConstructorDeclaration", ctor.IsStatic, "NameReservation");
            reserved++;
        }

        return (reserved, skipped);
    }

    /// <summary>
    /// M5: Reserve view member names in view-scoped namespace (separate from class surface).
    /// Each view gets its own scope: (TypeStableId, InterfaceStableId, isStatic).
    /// Uses PeekFinalMemberName to detect collisions with actual class-surface names.
    /// Returns (Reserved, Skipped) counts.
    /// </summary>
    internal static (int Reserved, int Skipped) ReserveViewMemberNamesOnly(
        BuildContext ctx,
        SymbolGraph graph,
        TypeSymbol type,
        HashSet<string> classAllNames)
    {
        int reserved = 0;
        int skipped = 0;

        // DEBUG: Log entry for canary types
        var canaryTypes = new[] { "System.Decimal", "System.Array", "System.CharEnumerator", "System.Enum", "System.TypeInfo" };
        if (canaryTypes.Contains(type.ClrFullName))
        {
            ctx.Log("NameReservation", $"[DEBUG] ReserveViewMemberNamesOnly CALLED: type={type.StableId} ExplicitViews.Length={type.ExplicitViews.Length}");
            foreach (var view in type.ExplicitViews)
            {
                ctx.Log("NameReservation", $"  view={view.ViewPropertyName} ViewMembers.Length={view.ViewMembers.Length}");
            }
        }

        // Check if type has any explicit views
        if (type.ExplicitViews.Length == 0)
            return (0, 0);

        // For each view, create a separate scope and reserve names (deterministic order)
        // Sort views by interface StableId for consistent ordering
        var sortedViews = type.ExplicitViews.OrderBy(v => ScopeFactory.GetInterfaceStableId(v.InterfaceReference));

        foreach (var view in sortedViews)
        {
            // Get interface StableId from TypeReference (no graph lookup)
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);

            // Create view-specific BASE scope (ReserveMemberName will add #instance/#static)
            var viewScope = ScopeFactory.ViewBase(type, interfaceStableId);

            // Create class surface BASE scope for collision detection (must match scope used in ReserveMemberNamesOnly)
            var classSurfaceScope = ScopeFactory.ClassBase(type);

            // Reserve names for each ViewOnly member (deterministic order)
            // ViewOnly members get separate view-scoped names even if they exist on class surface
            foreach (var viewMember in view.ViewMembers.OrderBy(vm => vm.Kind).ThenBy(vm => vm.StableId.ToString()))
            {
                // DEBUG: Log entry for canaries
                var canaryNames = new HashSet<string> { "ToByte", "ToSByte", "ToInt16", "Clear", "IndexOf", "Current", "TryFormat", "GetMethods", "GetFields" };
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view", $"[trace:resv:view] ENTER loop: member={viewMember.ClrName} stableId={viewMember.StableId}");
                }

                // M5 FIX: DO NOT skip! ViewOnly members need separate view-scoped decisions
                // even if they also exist on ClassSurface with the same StableId.
                // The collision detection below will apply $view suffix if needed.

                // This is a ViewOnly member - verify by checking EmitScope
                bool isViewOnly = false;
                switch (viewMember.Kind)
                {
                    case ViewPlanner.ViewMemberKind.Method:
                        var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                        isViewOnly = method?.EmitScope == EmitScope.ViewOnly;
                        break;
                    case ViewPlanner.ViewMemberKind.Property:
                        var prop = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                        isViewOnly = prop?.EmitScope == EmitScope.ViewOnly;
                        break;
                    case ViewPlanner.ViewMemberKind.Event:
                        var evt = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                        isViewOnly = evt?.EmitScope == EmitScope.ViewOnly;
                        break;
                }

                if (!isViewOnly)
                {
                    skipped++;
                    continue; // Not a ViewOnly member
                }

                // Find the actual member symbol to get isStatic
                var isStatic = Shared.FindMemberIsStatic(type, viewMember);

                // Compute base requested name using centralized function (same as class surface)
                var requested = Shared.RequestedBaseForMember(viewMember.ClrName);

                // Peek at what the view member would get in its scope
                var peek = ctx.Renamer.PeekFinalMemberName(viewScope, requested, isStatic);

                // DEBUG: Log peek result for canaries
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view", $"[DEBUG] TYPE={type.ClrFullName} member={viewMember.ClrName}");
                    ctx.Log("trace:resv:view", $"[DEBUG] requested={requested} peek={peek} isStatic={isStatic}");
                    ctx.Log("trace:resv:view", $"[DEBUG] classAllNames.Count={classAllNames.Count} Contains(peek)={classAllNames.Contains(peek)}");
                }

                // Collision if the view's final name equals ANY class-surface final name (static or instance)
                var collided = classAllNames.Contains(peek);

                // DEBUG: Log collision result
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view", $"[DEBUG] collided={collided}");
                }

                string finalRequested;
                string reason;
                string applySuffix;

                if (collided)
                {
                    // Collision with class surface - apply $view suffix
                    finalRequested = requested + "$view";

                    // DEBUG: Log suffix application
                    if (canaryNames.Contains(viewMember.ClrName))
                    {
                        ctx.Log("trace:resv:view", $"[DEBUG] APPLYING $view suffix: finalRequested={finalRequested}");
                    }

                    // If $view is also taken in the view scope, try $view2, $view3, etc.
                    var suffix = 1;
                    while (ctx.Renamer.IsNameTaken(viewScope, finalRequested, isStatic))
                    {
                        suffix++;
                        finalRequested = requested + "$view" + suffix;
                    }

                    reason = "ViewCollision";
                    applySuffix = finalRequested;  // e.g., "toSByte$view"
                }
                else
                {
                    finalRequested = requested;
                    reason = $"ViewMember:{view.ViewPropertyName}";
                    applySuffix = "none";
                }

                // B2) Trace: view reservation with detailed collision info
                if (canaryNames.Contains(viewMember.ClrName))
                {
                    ctx.Log("trace:resv:view",
                        $"[trace:resv:view] scope=view:{type.StableId}:{interfaceStableId}:{isStatic} member={Plan.Validation.Scopes.FormatMemberStableId(viewMember.StableId)}");
                    ctx.Log("trace:resv:view",
                        $"  requested={requested} peek={peek} classAllHit={collided} applySuffix={applySuffix} final={finalRequested}");
                }

                // Reserve in view scope
                ctx.Renamer.ReserveMemberName(
                    viewMember.StableId,
                    finalRequested,
                    viewScope,
                    reason,
                    isStatic,
                    "NameReservation");

                reserved++;
            }
        }

        return (reserved, skipped);
    }
}
