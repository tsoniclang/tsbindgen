using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Shape;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// Scope-related validation functions.
/// Validates EmitScope invariants, scope mismatches, and scope key formatting.
/// </summary>
internal static class Scopes
{
    /// <summary>
    /// M5: Validate EmitScope invariants.
    /// PG_INT_002: No member should appear in both ClassSurface and ViewOnly.
    /// PG_INT_003: ClassSurface members must not have SourceInterface set.
    /// </summary>
    internal static void ValidateEmitScopeInvariants(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M5: Validating EmitScope invariants...");

        int dualScopeErrors = 0;
        int sourceInterfaceErrors = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // PG_INT_002: Check for members appearing in both ClassSurface and ViewOnly
                var scopeMap = new Dictionary<MemberStableId, (bool ClassSurface, bool ViewOnly)>();

                void MarkMember(MemberStableId id, EmitScope scope)
                {
                    if (!scopeMap.TryGetValue(id, out var existing))
                        existing = (false, false);

                    scopeMap[id] = (
                        existing.ClassSurface || scope == EmitScope.ClassSurface,
                        existing.ViewOnly || scope == EmitScope.ViewOnly
                    );
                }

                foreach (var m in type.Members.Methods)
                    MarkMember(m.StableId, m.EmitScope);
                foreach (var p in type.Members.Properties)
                    MarkMember(p.StableId, p.EmitScope);
                foreach (var e in type.Members.Events)
                    MarkMember(e.StableId, e.EmitScope);

                foreach (var kv in scopeMap)
                {
                    if (kv.Value.ClassSurface && kv.Value.ViewOnly)
                    {
                        dualScopeErrors++;
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_INT_002,
                            "ERROR",
                            $"Member appears in both ClassSurface and ViewOnly\n" +
                            $"  type:   {type.ClrFullName}\n" +
                            $"  member: {FormatMemberStableId(kv.Key)}\n" +
                            $"  reason: Same StableId cannot have multiple EmitScopes");
                    }
                }

                // PG_INT_003: Check for ClassSurface members with SourceInterface
                foreach (var m in type.Members.Methods.Where(x => x.EmitScope == EmitScope.ClassSurface && x.SourceInterface != null))
                {
                    sourceInterfaceErrors++;
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.PG_INT_003,
                        "ERROR",
                        $"Class-surface member has SourceInterface set\n" +
                        $"  type:   {type.ClrFullName}\n" +
                        $"  method: {m.ClrName}\n" +
                        $"  interface: {m.SourceInterface}\n" +
                        $"  reason: SourceInterface is only valid for ViewOnly members");
                }

                foreach (var p in type.Members.Properties.Where(x => x.EmitScope == EmitScope.ClassSurface && x.SourceInterface != null))
                {
                    sourceInterfaceErrors++;
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.PG_INT_003,
                        "ERROR",
                        $"Class-surface property has SourceInterface set\n" +
                        $"  type:     {type.ClrFullName}\n" +
                        $"  property: {p.ClrName}\n" +
                        $"  interface: {p.SourceInterface}\n" +
                        $"  reason: SourceInterface is only valid for ViewOnly members");
                }
            }
        }

        ctx.Log("[PG]", $"M5: EmitScope invariants - {dualScopeErrors} dual-scope errors, {sourceInterfaceErrors} SourceInterface errors");
    }

    /// <summary>
    /// Step 6: Validate scope mismatches.
    /// PG_SCOPE_003: Detects empty/malformed scope keys.
    /// PG_SCOPE_004: Detects class/view scope kind doesn't match EmitScope.
    /// </summary>
    internal static void ValidateScopeMismatches(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M5: Validating scope mismatches (Step 6)...");

        int malformedScopeErrors = 0;
        int scopeEmitMismatchErrors = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Check all ClassSurface members - verify they can be looked up with class scope
                foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface))
                {
                    var scope = ScopeFactory.ClassSurface(type, method.IsStatic);

                    // PG_SCOPE_003: Check for malformed scope key
                    if (string.IsNullOrWhiteSpace(scope.ScopeKey) ||
                        !scope.ScopeKey.StartsWith("type:", StringComparison.Ordinal))
                    {
                        malformedScopeErrors++;
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_SCOPE_003,
                            "ERROR",
                            $"Malformed scope key for ClassSurface member\n" +
                            $"  type:     {type.ClrFullName}\n" +
                            $"  member:   {method.ClrName}\n" +
                            $"  scope:    {scope.ScopeKey}\n" +
                            $"  expected: type:...");
                    }

                    // Verify decision exists (should have been caught by post-reservation audit)
                    if (!ctx.Renamer.TryGetDecision(method.StableId, scope, out _))
                    {
                        // This should have been caught by post-reservation audit in NameReservation
                        // If we get here, it means the audit has a gap
                        scopeEmitMismatchErrors++;
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_SCOPE_004,
                            "ERROR",
                            $"ClassSurface method missing decision in class scope\n" +
                            $"  type:       {type.ClrFullName}\n" +
                            $"  method:     {method.ClrName}\n" +
                            $"  EmitScope:  {method.EmitScope}\n" +
                            $"  scope:      {scope.ScopeKey}\n" +
                            $"  reason:     Class-surface members must have decisions in class scope");
                    }
                }

                // Check similar for properties, fields, events
                foreach (var property in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface))
                {
                    var scope = ScopeFactory.ClassSurface(type, property.IsStatic);
                    if (string.IsNullOrWhiteSpace(scope.ScopeKey) ||
                        !scope.ScopeKey.StartsWith("type:", StringComparison.Ordinal))
                    {
                        malformedScopeErrors++;
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_SCOPE_003,
                            "ERROR",
                            $"Malformed scope key for ClassSurface property\n" +
                            $"  type:     {type.ClrFullName}\n" +
                            $"  property: {property.ClrName}\n" +
                            $"  scope:    {scope.ScopeKey}");
                    }
                }

                // Check ViewOnly members - verify they can be looked up with view scope
                foreach (var view in type.ExplicitViews)
                {
                    var interfaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);

                    foreach (var viewMember in view.ViewMembers)
                    {
                        // Find the actual member to check EmitScope and isStatic
                        bool isViewOnly = false;
                        bool isStatic = false;

                        switch (viewMember.Kind)
                        {
                            case ViewPlanner.ViewMemberKind.Method:
                                var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                                if (method != null)
                                {
                                    isViewOnly = method.EmitScope == EmitScope.ViewOnly;
                                    isStatic = method.IsStatic;
                                }
                                break;
                            case ViewPlanner.ViewMemberKind.Property:
                                var property = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                                if (property != null)
                                {
                                    isViewOnly = property.EmitScope == EmitScope.ViewOnly;
                                    isStatic = property.IsStatic;
                                }
                                break;
                            case ViewPlanner.ViewMemberKind.Event:
                                var ev = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                                if (ev != null)
                                {
                                    isViewOnly = ev.EmitScope == EmitScope.ViewOnly;
                                    isStatic = ev.IsStatic;
                                }
                                break;
                        }

                        // Only check ViewOnly members
                        if (!isViewOnly)
                            continue;

                        var scope = ScopeFactory.ViewSurface(type, interfaceStableId, isStatic);

                        // PG_SCOPE_003: Check for malformed scope key
                        if (string.IsNullOrWhiteSpace(scope.ScopeKey) ||
                            !scope.ScopeKey.StartsWith("view:", StringComparison.Ordinal))
                        {
                            malformedScopeErrors++;
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.PG_SCOPE_003,
                                "ERROR",
                                $"Malformed scope key for ViewOnly member\n" +
                                $"  type:      {type.ClrFullName}\n" +
                                $"  member:    {viewMember.ClrName}\n" +
                                $"  view:      {view.ViewPropertyName}\n" +
                                $"  scope:     {scope.ScopeKey}\n" +
                                $"  expected:  view:...");
                        }

                        // PG_SCOPE_004: Verify view member has decision in view scope (not class scope)
                        if (!ctx.Renamer.TryGetDecision(viewMember.StableId, scope, out _))
                        {
                            scopeEmitMismatchErrors++;
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.PG_SCOPE_004,
                                "ERROR",
                                $"ViewOnly member missing decision in view scope\n" +
                                $"  type:       {type.ClrFullName}\n" +
                                $"  member:     {viewMember.ClrName}\n" +
                                $"  view:       {view.ViewPropertyName}\n" +
                                $"  EmitScope:  ViewOnly\n" +
                                $"  scope:      {scope.ScopeKey}\n" +
                                $"  reason:     View-only members must have decisions in view scope");
                        }
                    }
                }
            }
        }

        ctx.Log("[PG]", $"M5: Scope mismatches - {malformedScopeErrors} malformed scope errors, {scopeEmitMismatchErrors} scope/EmitScope mismatch errors");
    }

    // ================================================================================
    // Helper Functions
    // ================================================================================

    /// <summary>
    /// Format a MemberStableId for diagnostics.
    /// </summary>
    internal static string FormatMemberStableId(MemberStableId id)
    {
        // Avoid duplicating member name if already in CanonicalSignature
        var sig = id.CanonicalSignature.StartsWith(id.MemberName + "(", StringComparison.Ordinal)
            ? id.CanonicalSignature
            : $"{id.MemberName}{id.CanonicalSignature}";
        return $"{id.AssemblyName}:{id.DeclaringClrFullName}::{sig}";
    }
}
