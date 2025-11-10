using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Shape;
using static tsbindgen.Core.TypeScriptReservedWords;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// Finalization validation functions.
/// Comprehensive finalization sweep - validates that every symbol has proper placement and naming.
/// This is the FINAL check before emission - nothing should leak past this gate.
/// Implements PG_FIN_001 through PG_FIN_009.
/// </summary>
internal static class Finalization
{
    /// <summary>
    /// Comprehensive finalization sweep - validates that every symbol has proper placement and naming.
    /// This is the FINAL check before emission - nothing should leak past this gate.
    /// Implements PG_FIN_001 through PG_FIN_009.
    /// </summary>
    internal static void Validate(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Running comprehensive finalization sweep (PG_FIN_001-009)...");

        foreach (var ns in graph.Namespaces)
        {
            // PG_FIN_004: Check all types have final names in namespace scope
            foreach (var type in ns.Types)
            {
                var nsScope = ScopeFactory.Namespace(ns.Name, NamespaceArea.Internal);
                if (!ctx.Renamer.HasFinalTypeName(type.StableId, nsScope))
                {
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.PG_FIN_004,
                        "ERROR",
                        $"Type {type.ClrFullName} missing final name in namespace scope {nsScope.ScopeKey}");
                }
            }

            foreach (var type in ns.Types)
            {
                // Track members by StableId to detect dual-role clashes
                var classSurfaceMembers = new HashSet<StableId>();
                var viewOnlyMembers = new HashSet<StableId>();

                // Collect all class surface members
                foreach (var prop in type.Members.Properties)
                {
                    if (prop.EmitScope == EmitScope.ClassSurface)
                        classSurfaceMembers.Add(prop.StableId);
                    else if (prop.EmitScope == EmitScope.ViewOnly)
                        viewOnlyMembers.Add(prop.StableId);
                }

                foreach (var method in type.Members.Methods)
                {
                    if (method.EmitScope == EmitScope.ClassSurface)
                        classSurfaceMembers.Add(method.StableId);
                    else if (method.EmitScope == EmitScope.ViewOnly)
                        viewOnlyMembers.Add(method.StableId);
                }

                foreach (var field in type.Members.Fields)
                {
                    if (field.EmitScope == EmitScope.ClassSurface)
                        classSurfaceMembers.Add(field.StableId);
                    else if (field.EmitScope == EmitScope.ViewOnly)
                        viewOnlyMembers.Add(field.StableId);
                }

                // PG_FIN_001: Check all members have explicit EmitScope (not Unspecified)
                // Every member MUST have deliberate placement decision - no defaults allowed
                foreach (var prop in type.Members.Properties)
                {
                    if (prop.EmitScope == EmitScope.Unspecified)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_001,
                            "ERROR",
                            $"Property {prop.ClrName} in {type.ClrFullName} has no EmitScope placement (still Unspecified)");
                    }
                }

                foreach (var method in type.Members.Methods)
                {
                    if (method.EmitScope == EmitScope.Unspecified)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_001,
                            "ERROR",
                            $"Method {method.ClrName} in {type.ClrFullName} has no EmitScope placement (still Unspecified)");
                    }
                }

                foreach (var field in type.Members.Fields)
                {
                    if (field.EmitScope == EmitScope.Unspecified)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_001,
                            "ERROR",
                            $"Field {field.ClrName} in {type.ClrFullName} has no EmitScope placement (still Unspecified)");
                    }
                }

                foreach (var evt in type.Members.Events)
                {
                    if (evt.EmitScope == EmitScope.Unspecified)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_001,
                            "ERROR",
                            $"Event {evt.ClrName} in {type.ClrFullName} has no EmitScope placement (still Unspecified)");
                    }
                }

                // PG_FIN_007: Check for dual-role clashes (same StableId in both ClassSurface and ViewOnly)
                var dualRole = classSurfaceMembers.Intersect(viewOnlyMembers).ToList();
                foreach (var stableId in dualRole)
                {
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.PG_FIN_007,
                        "ERROR",
                        $"Member {stableId} in {type.ClrFullName} appears in both ClassSurface and ViewOnly scopes");
                }

                // PG_FIN_003: Check all ClassSurface members have final names in class scope
                foreach (var prop in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface))
                {
                    var scope = ScopeFactory.ClassSurface(type, prop.IsStatic);
                    if (!ctx.Renamer.HasFinalMemberName(prop.StableId, scope))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_003,
                            "ERROR",
                            $"Property {prop.ClrName} (ClassSurface) in {type.ClrFullName} missing final name in scope {scope.ScopeKey}");
                    }
                }

                foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface))
                {
                    var scope = ScopeFactory.ClassSurface(type, method.IsStatic);
                    if (!ctx.Renamer.HasFinalMemberName(method.StableId, scope))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_003,
                            "ERROR",
                            $"Method {method.ClrName} (ClassSurface) in {type.ClrFullName} missing final name in scope {scope.ScopeKey}");
                    }
                }

                foreach (var field in type.Members.Fields.Where(f => f.EmitScope == EmitScope.ClassSurface))
                {
                    var scope = ScopeFactory.ClassSurface(type, field.IsStatic);
                    if (!ctx.Renamer.HasFinalMemberName(field.StableId, scope))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_003,
                            "ERROR",
                            $"Field {field.ClrName} (ClassSurface) in {type.ClrFullName} missing final name in scope {scope.ScopeKey}");
                    }
                }

                // PG_FIN_002, PG_FIN_003: Check all ViewOnly members have final names in view scope
                // Build map of interface StableId to ExplicitView for validation
                var viewsByInterface = new Dictionary<string, ViewPlanner.ExplicitView>();
                foreach (var view in type.ExplicitViews)
                {
                    var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);
                    viewsByInterface[ifaceStableId] = view;
                }

                foreach (var prop in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ViewOnly))
                {
                    // PG_FIN_002: ViewOnly member must have SourceInterface and belong to exactly one view
                    if (prop.SourceInterface == null)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_002,
                            "ERROR",
                            $"ViewOnly property {prop.ClrName} in {type.ClrFullName} has no SourceInterface");
                        continue;
                    }

                    var propInterfaceStableId = ScopeFactory.GetInterfaceStableId(prop.SourceInterface);
                    if (!viewsByInterface.ContainsKey(propInterfaceStableId))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_002,
                            "ERROR",
                            $"ViewOnly property {prop.ClrName} in {type.ClrFullName} references interface {propInterfaceStableId} which has no ExplicitView");
                        continue;
                    }

                    // Check final name in view scope
                    var viewScope = ScopeFactory.ViewSurface(type, propInterfaceStableId, prop.IsStatic);
                    if (!ctx.Renamer.HasFinalViewMemberName(prop.StableId, viewScope))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_003,
                            "ERROR",
                            $"ViewOnly property {prop.ClrName} in {type.ClrFullName} missing final name in view scope {viewScope.ScopeKey}");
                    }
                }

                foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ViewOnly))
                {
                    // PG_FIN_002: ViewOnly member must have SourceInterface and belong to exactly one view
                    if (method.SourceInterface == null)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_002,
                            "ERROR",
                            $"ViewOnly method {method.ClrName} in {type.ClrFullName} has no SourceInterface");
                        continue;
                    }

                    var methodInterfaceStableId = ScopeFactory.GetInterfaceStableId(method.SourceInterface);
                    if (!viewsByInterface.ContainsKey(methodInterfaceStableId))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_002,
                            "ERROR",
                            $"ViewOnly method {method.ClrName} in {type.ClrFullName} references interface {methodInterfaceStableId} which has no ExplicitView");
                        continue;
                    }

                    // Check final name in view scope
                    var viewScope = ScopeFactory.ViewSurface(type, methodInterfaceStableId, method.IsStatic);
                    if (!ctx.Renamer.HasFinalViewMemberName(method.StableId, viewScope))
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_003,
                            "ERROR",
                            $"ViewOnly method {method.ClrName} in {type.ClrFullName} missing final name in view scope {viewScope.ScopeKey}");
                    }
                }

                // PG_FIN_005: Check for empty views (zero members)
                foreach (var view in type.ExplicitViews)
                {
                    if (view.ViewMembers.Length == 0)
                    {
                        var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_005,
                            "ERROR",
                            $"ExplicitView for {ifaceStableId} in {type.ClrFullName} has zero members");
                    }
                }

                // PG_FIN_006: Check for duplicate membership (member appears in >1 view)
                var memberToViews = new Dictionary<StableId, List<string>>();
                foreach (var view in type.ExplicitViews)
                {
                    var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);

                    foreach (var viewMember in view.ViewMembers)
                    {
                        if (!memberToViews.ContainsKey(viewMember.StableId))
                            memberToViews[viewMember.StableId] = new List<string>();
                        memberToViews[viewMember.StableId].Add(ifaceStableId);
                    }
                }

                foreach (var (stableId, views) in memberToViews)
                {
                    if (views.Count > 1)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_006,
                            "ERROR",
                            $"Member {stableId} in {type.ClrFullName} appears in {views.Count} views: {string.Join(", ", views)}");
                    }
                }

                // PG_FIN_009: Check for unsanitized identifiers in emitting members
                foreach (var prop in type.Members.Properties.Where(p => p.EmitScope != EmitScope.Omitted))
                {
                    var sanitized = Sanitize(prop.TsEmitName);
                    if (sanitized.WasSanitized && sanitized.Sanitized != prop.TsEmitName)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"Property {prop.ClrName} in {type.ClrFullName} has unsanitized TsEmitName '{prop.TsEmitName}' (reserved word)");
                    }

                    // Check indexer parameters (if this is an indexer property)
                    if (prop.IsIndexer)
                    {
                        foreach (var param in prop.IndexParameters)
                        {
                            var paramSanitized = Sanitize(param.Name);
                            if (paramSanitized.WasSanitized && paramSanitized.Sanitized != param.Name)
                            {
                                validationCtx.RecordDiagnostic(
                                    DiagnosticCodes.PG_FIN_009,
                                    "ERROR",
                                    $"Property {prop.ClrName} parameter '{param.Name}' in {type.ClrFullName} is unsanitized (reserved word)");
                            }
                        }
                    }
                }

                foreach (var method in type.Members.Methods.Where(m => m.EmitScope != EmitScope.Omitted))
                {
                    var sanitized = Sanitize(method.TsEmitName);
                    if (sanitized.WasSanitized && sanitized.Sanitized != method.TsEmitName)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"Method {method.ClrName} in {type.ClrFullName} has unsanitized TsEmitName '{method.TsEmitName}' (reserved word)");
                    }

                    // Check method parameters
                    foreach (var param in method.Parameters)
                    {
                        var paramSanitized = Sanitize(param.Name);
                        if (paramSanitized.WasSanitized && paramSanitized.Sanitized != param.Name)
                        {
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.PG_FIN_009,
                                "ERROR",
                                $"Method {method.ClrName} parameter '{param.Name}' in {type.ClrFullName} is unsanitized (reserved word)");
                        }
                    }

                    // Check generic type parameters
                    foreach (var tp in method.GenericParameters)
                    {
                        var tpSanitized = Sanitize(tp.Name);
                        if (tpSanitized.WasSanitized && tpSanitized.Sanitized != tp.Name)
                        {
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.PG_FIN_009,
                                "ERROR",
                                $"Method {method.ClrName} type parameter '{tp.Name}' in {type.ClrFullName} is unsanitized (reserved word)");
                        }
                    }
                }

                foreach (var field in type.Members.Fields.Where(f => f.EmitScope != EmitScope.Omitted))
                {
                    var sanitized = Sanitize(field.TsEmitName);
                    if (sanitized.WasSanitized && sanitized.Sanitized != field.TsEmitName)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"Field {field.ClrName} in {type.ClrFullName} has unsanitized TsEmitName '{field.TsEmitName}' (reserved word)");
                    }
                }

                // Check type-level generic parameters
                foreach (var tp in type.GenericParameters)
                {
                    var tpSanitized = Sanitize(tp.Name);
                    if (tpSanitized.WasSanitized && tpSanitized.Sanitized != tp.Name)
                    {
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"Type {type.ClrFullName} type parameter '{tp.Name}' is unsanitized (reserved word)");
                    }
                }

                // Check view property names
                foreach (var view in type.ExplicitViews)
                {
                    var viewPropSanitized = Sanitize(view.ViewPropertyName);
                    if (viewPropSanitized.WasSanitized && viewPropSanitized.Sanitized != view.ViewPropertyName)
                    {
                        var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);
                        validationCtx.RecordDiagnostic(
                            DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"View property '{view.ViewPropertyName}' for {ifaceStableId} in {type.ClrFullName} is unsanitized (reserved word)");
                    }
                }
            }
        }

        ctx.Log("PhaseGate", "Finalization sweep complete");
    }
}
