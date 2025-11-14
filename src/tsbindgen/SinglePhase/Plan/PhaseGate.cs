using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;
using tsbindgen.SinglePhase.Shape;
using tsbindgen.SinglePhase.Plan.Validation;
using ValidationCore = tsbindgen.SinglePhase.Plan.Validation.Core;
using VCtx = tsbindgen.SinglePhase.Plan.Validation.Context;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Validates the symbol graph before emission.
/// Performs comprehensive validation checks and policy enforcement.
/// Acts as quality gate between Shape/Plan phases and Emit phase.
/// </summary>
public static class PhaseGate
{
    public static void Validate(BuildContext ctx, SymbolGraph graph, ImportPlan imports, InterfaceConstraintFindings constraintFindings)
    {
        ctx.Log("PhaseGate", "Validating symbol graph before emission...");

        var validationContext = new ValidationContext
        {
            ErrorCount = 0,
            WarningCount = 0,
            Diagnostics = new List<string>(),
            SanitizedNameCount = 0,
            InterfaceConformanceIssuesByType = new Dictionary<string, List<string>>()
        };

        // Run all validation checks (delegated to Validation modules)
        ValidationCore.ValidateTypeNames(ctx, graph, validationContext);
        ValidationCore.ValidateMemberNames(ctx, graph, validationContext);
        ValidationCore.ValidateGenericParameters(ctx, graph, validationContext);
        ValidationCore.ValidateInterfaceConformance(ctx, graph, validationContext);
        ValidationCore.ValidateInheritance(ctx, graph, validationContext);
        ValidationCore.ValidateEmitScopes(ctx, graph, validationContext);
        ValidationCore.ValidateImports(ctx, graph, imports, validationContext);
        ValidationCore.ValidatePolicyCompliance(ctx, graph, validationContext);

        // Step 9: PhaseGate Hardening - Additional validation checks
        Views.Validate(ctx, graph, validationContext);
        Names.ValidateFinalNames(ctx, graph, validationContext);
        Names.ValidateAliases(ctx, graph, imports, validationContext);

        // PhaseGate Hardening - M1: Identifier sanitization verification
        Names.ValidateIdentifiers(ctx, graph, validationContext);

        // PhaseGate Hardening - M2: Overload collision detection
        Names.ValidateOverloadCollisions(ctx, graph, validationContext);

        // PhaseGate Hardening - M3: View integrity validation (3 hard rules)
        Views.ValidateIntegrity(ctx, graph, validationContext);

        // PhaseGate Hardening - M4: Constraint findings from InterfaceConstraintAuditor
        Constraints.EmitDiagnostics(ctx, constraintFindings, validationContext);

        // PhaseGate Hardening - M5: View member name scoping (PG_NAME_003, PG_NAME_004)
        Views.ValidateMemberScoping(ctx, graph, validationContext);

        // PhaseGate Hardening - M5: EmitScope invariants (PG_INT_002, PG_INT_003)
        Scopes.ValidateEmitScopeInvariants(ctx, graph, validationContext);

        // PhaseGate Hardening - M5: Scope mismatches (PG_SCOPE_003, PG_SCOPE_004) - Step 6
        Scopes.ValidateScopeMismatches(ctx, graph, validationContext);

        // PhaseGate Hardening - M5: Class surface uniqueness (PG_NAME_005)
        Names.ValidateClassSurfaceUniqueness(ctx, graph, validationContext);

        // PhaseGate Hardening - M6: Comprehensive finalization sweep (PG_FIN_001 through PG_FIN_009)
        // This is the FINAL validation before emission - catches any symbol without proper finalization
        Finalization.Validate(ctx, graph, validationContext);

        // PhaseGate Hardening - M6a: CLR surface name policy (PG_NAME_SURF_001)
        // Validates that class members match interface members using CLR-name contract
        Names.ValidateClrSurfaceNamePolicy(ctx, graph, validationContext);

        // NOTE: PG_NAME_SURF_002 (numeric suffix validation) is disabled in CLR-name contract model
        // With CLR-name contract, we emit CLR names directly (ToInt32, ToUInt16, etc.)
        // Numeric suffixes in CLR names are legitimate, not collision-resolution artifacts
        // Names.ValidateNoNumericSuffixesOnSurface(ctx, graph, validationContext);

        // PhaseGate Hardening - M7: Printer name consistency (PG_PRINT_001)
        // Validates TypeRefPrinter→TypeNameResolver→Renamer chain integrity
        Types.ValidatePrinterNameConsistency(ctx, graph, validationContext);

        // PhaseGate Hardening - M7a: TypeMap validation (PG_TYPEMAP_001)
        // Validates no unsupported special forms (pointers, byrefs, function pointers)
        // MUST RUN EARLY - before other type reference validation
        Types.ValidateTypeMapCompliance(ctx, graph, validationContext);

        // PhaseGate Hardening - M7_CLROf: Primitive generic lifting (PG_GENERIC_PRIM_LIFT_001)
        // Validates all primitive type arguments are covered by CLROf lifting rules
        // Ensures TypeRefPrinter primitive detection stays in sync with PrimitiveLift configuration
        Types.ValidatePrimitiveGenericLifting(ctx, graph, validationContext);

        // PhaseGate Hardening - M7b: External type resolution (PG_LOAD_001)
        // Validates all external type references are either in TypeIndex or built-in
        // MUST RUN AFTER TypeMap check, BEFORE API surface validation
        Types.ValidateExternalTypeResolution(ctx, graph, validationContext);

        // PhaseGate Hardening - M7c: Type reference resolution (PG_REF_001)
        // Validates all type references can be resolved via import/local/built-in
        // Catches TS2304 "Cannot find name" at planning time
        Types.ValidateTypeReferenceResolution(ctx, graph, imports, validationContext);

        // PhaseGate Hardening - M7d: Generic arity consistency (PG_ARITY_001)
        // Validates generic type arity matches across aliases and type references
        // Catches TS2315 "Type is not generic" at planning time
        Types.ValidateGenericArityConsistency(ctx, graph, imports, validationContext);

        // PhaseGate Hardening - M8: Public API surface validation (PG_API_001, PG_API_002)
        // Validates public APIs don't reference non-emitted/internal types
        // THIS MUST RUN BEFORE PG_IMPORT_001 - it's more fundamental
        ImportExport.ValidatePublicApiSurface(ctx, graph, imports, validationContext);

        // PhaseGate Hardening - M9: Import completeness (PG_IMPORT_001)
        // Validates every foreign type used in signatures has a corresponding import
        ImportExport.ValidateImportCompleteness(ctx, graph, imports, validationContext);

        // PhaseGate Hardening - M10: Export completeness (PG_EXPORT_001)
        // Validates imported types are actually exported by source namespaces
        ImportExport.ValidateExportCompleteness(ctx, graph, imports, validationContext);

        // PhaseGate Hardening - M17: Heritage value imports (PG_IMPORT_002)
        // Validates base classes and interfaces in heritage clauses use value imports (not type-only)
        ImportExport.ValidateHeritageValueImports(ctx, graph, imports, validationContext);

        // PhaseGate Hardening - M18: Qualified export paths (PG_EXPORT_002)
        // Validates qualified names like 'System_Internal.System.Exception$instance' have valid export paths
        ImportExport.ValidateQualifiedExportPaths(ctx, graph, imports, validationContext);

        // Report results
        ctx.Log("PhaseGate", $"Validation complete - {validationContext.ErrorCount} errors, {validationContext.WarningCount} warnings, {validationContext.InfoCount} info");
        ctx.Log("PhaseGate", $"Sanitized {validationContext.SanitizedNameCount} reserved word identifiers");

        // Print diagnostic summary table
        if (validationContext.DiagnosticCountsByCode.Count > 0)
        {
            ctx.Log("PhaseGate", "");
            ctx.Log("PhaseGate", "Diagnostic Summary by Code:");
            ctx.Log("PhaseGate", "─────────────────────────────────────────");

            foreach (var (code, count) in validationContext.DiagnosticCountsByCode.OrderByDescending(kvp => kvp.Value))
            {
                var description = VCtx.GetDiagnosticDescription(code);
                ctx.Log("PhaseGate", $"  {code}: {count,5} - {description}");
            }

            ctx.Log("PhaseGate", "─────────────────────────────────────────");
        }

        if (validationContext.ErrorCount > 0)
        {
            // Build sample diagnostics message for error output - show ERRORS first
            var errors = validationContext.Diagnostics.Where(d => d.StartsWith("ERROR:")).ToList();
            var warnings = validationContext.Diagnostics.Where(d => d.StartsWith("WARNING:")).ToList();

            var sample = errors.Take(20).ToList();
            var sampleText = string.Join("\n", sample.Select(d => $"  {d}"));

            if (errors.Count > 20)
            {
                sampleText += $"\n  ... and {errors.Count - 20} more errors";
            }

            if (warnings.Count > 0)
            {
                sampleText += $"\n\n  ({warnings.Count} warnings suppressed from this display)";
            }

            ctx.Diagnostics.Error(Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                $"PhaseGate validation failed with {validationContext.ErrorCount} errors\n\nSample errors (first 20):\n{sampleText}");
        }

        // Record all diagnostics for later analysis
        foreach (var diagnostic in validationContext.Diagnostics)
        {
            ctx.Log("PhaseGate", diagnostic);
        }

        // Step 3: Write detailed diagnostics file with full conformance issues
        VCtx.WriteDiagnosticsFile(ctx, validationContext);

        // Write summary JSON for CI/snapshot comparison
        VCtx.WriteSummaryJson(ctx, validationContext);
    }
}
