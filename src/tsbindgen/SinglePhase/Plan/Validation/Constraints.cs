using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Types;
using tsbindgen.SinglePhase.Shape;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// Constraint validation functions.
/// Validates generic parameter constraints and emits diagnostics for constraint losses.
/// </summary>
internal static class Constraints
{
    /// <summary>
    /// M4: Emit constructor constraint diagnostics from InterfaceConstraintAuditor findings.
    /// This replaces per-member checking to avoid duplicate diagnostics for view members.
    /// Classifies constraint losses as benign (WARNING) vs non-benign (ERROR).
    /// </summary>
    internal static void EmitDiagnostics(BuildContext ctx, InterfaceConstraintFindings findings, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M4: Emitting constraint diagnostics from interface findings...");

        int errorCount = 0;
        int warningCount = 0;

        // Check policy flag for constructor constraint loss
        var allowConstructorLoss = ctx.Policy.Constraints.AllowConstructorConstraintLoss;

        foreach (var finding in findings.Findings)
        {
            if (finding.LossKind == ConstraintLossKind.ConstructorConstraintLoss)
            {
                if (!allowConstructorLoss)
                {
                    // Strict mode: ERROR
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.PG_CT_001,
                        "ERROR",
                        $"PG_CT_001: Non-benign constraint loss on {finding.GenericParameterName} in {finding.TypeFullName}\n" +
                        $"  Type:      {finding.TypeFullName}\n" +
                        $"  Interface: {finding.InterfaceFullName}\n" +
                        $"  Reason:    TypeScript cannot represent parameterless constructor constraints; callers relying on `new {finding.GenericParameterName}()` would be unsound.");
                    errorCount++;
                }
                else
                {
                    // Override mode: WARNING
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.PG_CT_002,
                        "WARNING",
                        $"[OVERRIDE] PG_CT_002: Constructor constraint loss on {finding.GenericParameterName} in {finding.TypeFullName}\n" +
                        $"  Type:      {finding.TypeFullName}\n" +
                        $"  Interface: {finding.InterfaceFullName}\n" +
                        $"  Reason:    TypeScript cannot represent parameterless constructor constraints; callers relying on `new {finding.GenericParameterName}()` would be unsound.\n" +
                        $"  Note:      Allowed via Policy.Constraints.AllowConstructorConstraintLoss = true");
                    warningCount++;
                }
            }
        }

        ctx.Log("[PG]", $"M4: Emitted {errorCount} constraint errors, {warningCount} constraint warnings from {findings.Findings.Length} findings");
    }

    // ================================================================================
    // Helper Functions
    // ================================================================================

    /// <summary>
    /// Check if a constraint is an enum type.
    /// </summary>
    private static bool IsEnumConstraint(TypeReference typeRef, SymbolGraph graph)
    {
        var typeName = GetTypeReferenceName(typeRef);
        var type = graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == typeName);

        return type?.Kind == TypeKind.Enum;
    }

    /// <summary>
    /// Format the full CLR constraint set in readable form.
    /// Example: "struct, System.Enum, new()"
    /// </summary>
    private static string FormatConstraintSet(GenericParameterConstraints specialConstraints, IReadOnlyList<TypeReference> typeConstraints)
    {
        var parts = new List<string>();

        // Special constraints
        if (specialConstraints.HasFlag(GenericParameterConstraints.ReferenceType))
            parts.Add("class");
        if (specialConstraints.HasFlag(GenericParameterConstraints.ValueType))
            parts.Add("struct");
        if (specialConstraints.HasFlag(GenericParameterConstraints.NotNullable))
            parts.Add("notnull");

        // Type constraints
        foreach (var constraint in typeConstraints)
        {
            parts.Add(GetTypeReferenceName(constraint));
        }

        // Constructor constraint (always last)
        if (specialConstraints.HasFlag(GenericParameterConstraints.DefaultConstructor))
            parts.Add("new()");

        return parts.Count > 0 ? string.Join(", ", parts) : "none";
    }

    /// <summary>
    /// Format the TypeScript-level constraint set (after lossy mapping).
    /// Shows what TypeScript can actually represent.
    /// </summary>
    private static string FormatTsConstraintSet(GenericParameterConstraints specialConstraints, IReadOnlyList<TypeReference> typeConstraints)
    {
        var parts = new List<string>();

        // Special constraints are lost in TS (just informational)
        if (specialConstraints.HasFlag(GenericParameterConstraints.ValueType))
            parts.Add("value-type-like");
        else if (specialConstraints.HasFlag(GenericParameterConstraints.ReferenceType))
            parts.Add("reference-type-like");

        // Type constraints that survive
        foreach (var constraint in typeConstraints)
        {
            // Enums become number, others pass through
            parts.Add(GetTypeReferenceName(constraint));
        }

        return parts.Count > 0 ? string.Join(" & ", parts) : "(no constraint)";
    }

    /// <summary>
    /// Get the full name from a TypeReference for diagnostics.
    /// Simplified version that returns just the type name.
    /// </summary>
    private static string GetTypeReferenceName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}
