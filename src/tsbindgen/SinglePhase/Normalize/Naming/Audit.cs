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
/// Audit functions - verify name reservation completeness.
/// Ensures all types and members have rename decisions in appropriate scopes.
/// </summary>
internal static class Audit
{
    /// <summary>
    /// Post-reservation audit: verify all types and members have rename decisions.
    /// Checks both class-surface and view-surface members in their appropriate scopes.
    /// Throws if any types/members are missing decisions.
    /// </summary>
    internal static void AuditReservationCompleteness(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("NameReservation", "Running post-reservation audit...");

        int typesChecked = 0;
        int membersChecked = 0;
        var errors = new List<string>();

        foreach (var ns in graph.Namespaces)
        {
            var nsScope = ScopeFactory.Namespace(ns.Name, NamespaceArea.Internal);

            foreach (var type in ns.Types)
            {
                if (Shared.IsCompilerGenerated(type.ClrName))
                    continue;

                // Verify type name is reserved
                if (!ctx.Renamer.TryGetDecision(type.StableId, nsScope, out _))
                {
                    errors.Add($"PG_FIN_003: Type missing rename decision\n" +
                              $"  Type: {type.ClrFullName}\n" +
                              $"  StableId: {type.StableId}\n" +
                              $"  Expected scope: {nsScope.ScopeKey}");
                }
                typesChecked++;

                // Verify class-surface members
                AuditClassSurfaceMembers(ctx, type, errors, ref membersChecked);

                // Verify view-surface members
                AuditViewSurfaceMembers(ctx, type, errors, ref membersChecked);
            }
        }

        ctx.Log("NameReservation", $"Audit complete: checked {typesChecked} types, {membersChecked} members");

        if (errors.Count > 0)
        {
            var errorReport = string.Join("\n\n", errors.Take(10)); // Show first 10 errors
            var summary = errors.Count > 10
                ? $"\n\n... and {errors.Count - 10} more errors"
                : "";

            throw new InvalidOperationException(
                $"Post-reservation audit failed with {errors.Count} error(s):\n\n{errorReport}{summary}");
        }

        ctx.Log("NameReservation", "✓ Post-reservation audit passed");
    }

    /// <summary>
    /// Audit all ClassSurface members have decisions in class scope.
    /// </summary>
    private static void AuditClassSurfaceMembers(BuildContext ctx, TypeSymbol type, List<string> errors, ref int membersChecked)
    {
        // Check methods
        foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface))
        {
            var scope = ScopeFactory.ClassSurface(type, method.IsStatic);
            if (!ctx.Renamer.TryGetDecision(method.StableId, scope, out _))
            {
                errors.Add($"PG_FIN_003: ClassSurface method missing rename decision\n" +
                          $"  Type: {type.ClrFullName}\n" +
                          $"  Member: {method.ClrName}\n" +
                          $"  StableId: {method.StableId}\n" +
                          $"  EmitScope: {method.EmitScope}\n" +
                          $"  IsStatic: {method.IsStatic}\n" +
                          $"  Expected scope: {scope.ScopeKey}");
            }
            membersChecked++;
        }

        // Check properties
        foreach (var property in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface))
        {
            var scope = ScopeFactory.ClassSurface(type, property.IsStatic);
            if (!ctx.Renamer.TryGetDecision(property.StableId, scope, out _))
            {
                errors.Add($"PG_FIN_003: ClassSurface property missing rename decision\n" +
                          $"  Type: {type.ClrFullName}\n" +
                          $"  Member: {property.ClrName}\n" +
                          $"  StableId: {property.StableId}\n" +
                          $"  EmitScope: {property.EmitScope}\n" +
                          $"  IsStatic: {property.IsStatic}\n" +
                          $"  Expected scope: {scope.ScopeKey}");
            }
            membersChecked++;
        }

        // Check fields
        foreach (var field in type.Members.Fields.Where(f => f.EmitScope == EmitScope.ClassSurface))
        {
            var scope = ScopeFactory.ClassSurface(type, field.IsStatic);
            if (!ctx.Renamer.TryGetDecision(field.StableId, scope, out _))
            {
                errors.Add($"PG_FIN_003: ClassSurface field missing rename decision\n" +
                          $"  Type: {type.ClrFullName}\n" +
                          $"  Member: {field.ClrName}\n" +
                          $"  StableId: {field.StableId}\n" +
                          $"  EmitScope: {field.EmitScope}\n" +
                          $"  IsStatic: {field.IsStatic}\n" +
                          $"  Expected scope: {scope.ScopeKey}");
            }
            membersChecked++;
        }

        // Check events
        foreach (var ev in type.Members.Events.Where(e => e.EmitScope == EmitScope.ClassSurface))
        {
            var scope = ScopeFactory.ClassSurface(type, ev.IsStatic);
            if (!ctx.Renamer.TryGetDecision(ev.StableId, scope, out _))
            {
                errors.Add($"PG_FIN_003: ClassSurface event missing rename decision\n" +
                          $"  Type: {type.ClrFullName}\n" +
                          $"  Member: {ev.ClrName}\n" +
                          $"  StableId: {ev.StableId}\n" +
                          $"  EmitScope: {ev.EmitScope}\n" +
                          $"  IsStatic: {ev.IsStatic}\n" +
                          $"  Expected scope: {scope.ScopeKey}");
            }
            membersChecked++;
        }
    }

    /// <summary>
    /// Audit all ViewOnly members have decisions in view scope.
    /// </summary>
    private static void AuditViewSurfaceMembers(BuildContext ctx, TypeSymbol type, List<string> errors, ref int membersChecked)
    {
        foreach (var view in type.ExplicitViews)
        {
            var interfaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);

            foreach (var viewMember in view.ViewMembers)
            {
                // Determine if this is actually a ViewOnly member
                bool isViewOnly = false;
                bool isStatic = false;

                switch (viewMember.Kind)
                {
                    case ViewPlanner.ViewMemberKind.Method:
                        var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                        isViewOnly = method?.EmitScope == EmitScope.ViewOnly;
                        isStatic = method?.IsStatic ?? false;
                        break;
                    case ViewPlanner.ViewMemberKind.Property:
                        var property = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                        isViewOnly = property?.EmitScope == EmitScope.ViewOnly;
                        isStatic = property?.IsStatic ?? false;
                        break;
                    case ViewPlanner.ViewMemberKind.Event:
                        var ev = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                        isViewOnly = ev?.EmitScope == EmitScope.ViewOnly;
                        isStatic = ev?.IsStatic ?? false;
                        break;
                }

                if (!isViewOnly)
                    continue; // Not a ViewOnly member - was checked in class surface audit

                var scope = ScopeFactory.ViewSurface(type, interfaceStableId, isStatic);
                if (!ctx.Renamer.TryGetDecision(viewMember.StableId, scope, out _))
                {
                    errors.Add($"PG_FIN_003: ViewOnly member missing rename decision\n" +
                              $"  Type: {type.ClrFullName}\n" +
                              $"  View: {view.ViewPropertyName}\n" +
                              $"  Interface: {interfaceStableId}\n" +
                              $"  Member: {viewMember.ClrName}\n" +
                              $"  StableId: {viewMember.StableId}\n" +
                              $"  MemberKind: {viewMember.Kind}\n" +
                              $"  IsStatic: {isStatic}\n" +
                              $"  Expected scope: {scope.ScopeKey}");
                }
                membersChecked++;
            }
        }
    }
}
