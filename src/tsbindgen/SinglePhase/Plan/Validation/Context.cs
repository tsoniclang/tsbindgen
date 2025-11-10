using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// Validation context container and reporting functions.
/// </summary>
internal sealed class ValidationContext
{
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public List<string> Diagnostics { get; set; } = new();

    // Track diagnostic counts by code (e.g., TBG120, TBG211, etc.)
    public Dictionary<string, int> DiagnosticCountsByCode { get; set; } = new();

    // Step 1: Track sanitized names (reserved words that were properly escaped)
    public int SanitizedNameCount { get; set; }

    // Step 3: Aggregate interface conformance issues by type (for one-line summaries)
    public Dictionary<string, List<string>> InterfaceConformanceIssuesByType { get; set; } = new();

    /// <summary>
    /// Record a diagnostic with its code for tracking.
    /// </summary>
    public void RecordDiagnostic(string code, string severity, string message)
    {
        // Track count by code
        if (!DiagnosticCountsByCode.ContainsKey(code))
        {
            DiagnosticCountsByCode[code] = 0;
        }
        DiagnosticCountsByCode[code]++;

        // Update severity counters
        switch (severity.ToUpperInvariant())
        {
            case "ERROR":
                ErrorCount++;
                break;
            case "WARNING":
                WarningCount++;
                break;
            case "INFO":
                InfoCount++;
                break;
        }

        // Add to diagnostics list
        Diagnostics.Add($"{severity.ToUpperInvariant()}: [{code}] {message}");
    }
}

/// <summary>
/// Context operations for validation.
/// </summary>
internal static class Context
{
    internal static ValidationContext Create()
    {
        return new ValidationContext
        {
            ErrorCount = 0,
            WarningCount = 0,
            InfoCount = 0,
            Diagnostics = new List<string>(),
            SanitizedNameCount = 0,
            InterfaceConformanceIssuesByType = new Dictionary<string, List<string>>()
        };
    }

    internal static void WriteSummaryJson(BuildContext ctx, ValidationContext validationCtx)
    {
        var summaryPath = System.IO.Path.Combine(".tests", "phasegate-summary.json");

        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(summaryPath)!);

            var summary = new
            {
                timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                totals = new
                {
                    errors = validationCtx.ErrorCount,
                    warnings = validationCtx.WarningCount,
                    info = validationCtx.InfoCount,
                    sanitized_names = validationCtx.SanitizedNameCount
                },
                diagnostic_counts_by_code = validationCtx.DiagnosticCountsByCode
                    .OrderBy(kvp => kvp.Key)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
            };

            var json = System.Text.Json.JsonSerializer.Serialize(summary, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });

            System.IO.File.WriteAllText(summaryPath, json);

            ctx.Log("PhaseGate", $"Summary JSON written to {summaryPath}");
        }
        catch (Exception ex)
        {
            ctx.Log("PhaseGate", $"WARNING - Failed to write summary JSON: {ex.Message}");
        }
    }

    internal static void WriteDiagnosticsFile(BuildContext ctx, ValidationContext validationCtx)
    {
        var diagnosticsPath = System.IO.Path.Combine(".tests", "phasegate-diagnostics.txt");

        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(diagnosticsPath)!);

            using var writer = new System.IO.StreamWriter(diagnosticsPath);

            writer.WriteLine("=".PadRight(80, '='));
            writer.WriteLine("PhaseGate Detailed Diagnostics");
            writer.WriteLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            writer.WriteLine("=".PadRight(80, '='));
            writer.WriteLine();

            writer.WriteLine($"Summary:");
            writer.WriteLine($"  Errors: {validationCtx.ErrorCount}");
            writer.WriteLine($"  Warnings: {validationCtx.WarningCount}");
            writer.WriteLine($"  Info: {validationCtx.InfoCount}");
            writer.WriteLine($"  Sanitized identifiers: {validationCtx.SanitizedNameCount}");
            writer.WriteLine();

            // Write interface conformance details
            if (validationCtx.InterfaceConformanceIssuesByType.Count > 0)
            {
                writer.WriteLine("-".PadRight(80, '-'));
                writer.WriteLine($"Interface Conformance Issues ({validationCtx.InterfaceConformanceIssuesByType.Count} types)");
                writer.WriteLine("-".PadRight(80, '-'));
                writer.WriteLine();

                foreach (var (typeName, issues) in validationCtx.InterfaceConformanceIssuesByType.OrderBy(kv => kv.Key))
                {
                    writer.WriteLine($"{typeName}:");
                    foreach (var issue in issues)
                    {
                        writer.WriteLine(issue);
                    }
                    writer.WriteLine();
                }
            }

            // Write all diagnostics
            writer.WriteLine("-".PadRight(80, '-'));
            writer.WriteLine("All Diagnostics");
            writer.WriteLine("-".PadRight(80, '-'));
            writer.WriteLine();

            foreach (var diagnostic in validationCtx.Diagnostics)
            {
                writer.WriteLine(diagnostic);
            }

            ctx.Log("PhaseGate", $"Detailed diagnostics written to {diagnosticsPath}");
        }
        catch (Exception ex)
        {
            ctx.Log("PhaseGate", $"WARNING - Failed to write diagnostics file: {ex.Message}");
        }
    }

    internal static string GetDiagnosticDescription(string code)
    {
        return code switch
        {
            DiagnosticCodes.ValidationFailed => "Validation failed",
            DiagnosticCodes.DuplicateMember => "Duplicate members",
            DiagnosticCodes.AmbiguousOverload => "Ambiguous overloads",
            DiagnosticCodes.ReservedWordUnsanitized => "Reserved words not sanitized",
            DiagnosticCodes.CovarianceSummary => "Property covariance (TS limitation)",
            DiagnosticCodes.StructuralConformanceFailure => "Interface conformance failures",
            DiagnosticCodes.CircularInheritance => "Circular inheritance/dependencies",
            DiagnosticCodes.ViewCoverageMismatch => "ViewOnly member coverage issues",
            DiagnosticCodes.IndexerConflict => "Indexer conflicts",
            DiagnosticCodes.InterfaceNotFound => "External interface references",
            DiagnosticCodes.NameConflictUnresolved => "Name conflicts",
            DiagnosticCodes.UnrepresentableConstraint => "Unrepresentable constraints",
            // PhaseGate Hardening diagnostics
            DiagnosticCodes.PG_ID_001 => "Reserved identifier not sanitized",
            DiagnosticCodes.PG_OV_001 => "Duplicate erased signature",
            DiagnosticCodes.PG_VIEW_001 => "Empty view (no members)",
            DiagnosticCodes.PG_VIEW_002 => "Duplicate view for same interface",
            DiagnosticCodes.PG_VIEW_003 => "Invalid/unsanitized view property name",
            DiagnosticCodes.PG_CT_001 => "Non-benign constraint loss",
            DiagnosticCodes.PG_CT_002 => "Constructor constraint loss (override)",
            DiagnosticCodes.PG_IFC_001 => "Interface method not assignable (erased)",
            DiagnosticCodes.PG_NAME_003 => "View member collision within view scope",
            DiagnosticCodes.PG_NAME_004 => "View member name shadows class surface",
            DiagnosticCodes.PG_NAME_005 => "Duplicate property name on class surface",
            DiagnosticCodes.PG_INT_002 => "Member in both ClassSurface and ViewOnly",
            DiagnosticCodes.PG_INT_003 => "ClassSurface member has SourceInterface",
            DiagnosticCodes.PG_FIN_001 => "Member has no final placement or illegal combo",
            DiagnosticCodes.PG_FIN_002 => "ViewOnly member not in exactly one ExplicitView",
            DiagnosticCodes.PG_FIN_003 => "Member missing final name in scope after reservation",
            DiagnosticCodes.PG_FIN_004 => "Type missing final name in namespace scope",
            DiagnosticCodes.PG_FIN_005 => "Empty/invalid view",
            DiagnosticCodes.PG_FIN_006 => "Duplicate view membership",
            DiagnosticCodes.PG_FIN_007 => "Class/View dual-role clash",
            DiagnosticCodes.PG_FIN_008 => "Interface requires view but type has none",
            DiagnosticCodes.PG_FIN_009 => "Unsanitized identifier post-sanitizer",
            DiagnosticCodes.PG_SCOPE_003 => "Empty/malformed scope key",
            DiagnosticCodes.PG_SCOPE_004 => "Scope kind doesn't match EmitScope",
            DiagnosticCodes.PG_PRINT_001 => "Type name mismatch (Printer vs Renamer)",
            DiagnosticCodes.PG_IMPORT_001 => "Type used but not imported",
            DiagnosticCodes.PG_EXPORT_001 => "Import references unexported type",
            DiagnosticCodes.PG_API_001 => "Public API exposes internal/non-emitted type",
            DiagnosticCodes.PG_API_002 => "Generic constraint references non-emitted type",
            DiagnosticCodes.PG_TYPEMAP_001 => "Unsupported special form (pointer/byref/fnptr)",
            DiagnosticCodes.PG_LOAD_001 => "Unresolved external type reference",
            DiagnosticCodes.PG_LOAD_002 => "Mixed PublicKeyToken for same assembly",
            DiagnosticCodes.PG_LOAD_003 => "Version drift (same assembly, different versions)",
            DiagnosticCodes.PG_LOAD_004 => "Retargetable/ContentType assembly reference",
            _ => "Unknown diagnostic"
        };
    }
}
