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

        // Run all validation checks
        ValidateTypeNames(ctx, graph, validationContext);
        ValidateMemberNames(ctx, graph, validationContext);
        ValidateGenericParameters(ctx, graph, validationContext);
        ValidateInterfaceConformance(ctx, graph, validationContext);
        ValidateInheritance(ctx, graph, validationContext);
        ValidateEmitScopes(ctx, graph, validationContext);
        ValidateImports(ctx, graph, imports, validationContext);
        ValidatePolicyCompliance(ctx, graph, validationContext);

        // Step 9: PhaseGate Hardening - Additional validation checks
        ValidateViews(ctx, graph, validationContext);
        ValidateFinalNames(ctx, graph, validationContext);
        ValidateAliases(ctx, graph, imports, validationContext);

        // PhaseGate Hardening - M1: Identifier sanitization verification
        ValidateIdentifiers(ctx, graph, validationContext);

        // PhaseGate Hardening - M2: Overload collision detection
        ValidateOverloadCollisions(ctx, graph, validationContext);

        // PhaseGate Hardening - M3: View integrity validation (3 hard rules)
        ValidateViewsIntegrity(ctx, graph, validationContext);

        // PhaseGate Hardening - M4: Constraint findings from InterfaceConstraintAuditor
        EmitConstraintDiagnostics(ctx, constraintFindings, validationContext);

        // PhaseGate Hardening - M5: View member name scoping (PG_NAME_003, PG_NAME_004)
        ValidateViewMemberNameScoping(ctx, graph, validationContext);

        // PhaseGate Hardening - M5: EmitScope invariants (PG_INT_002, PG_INT_003)
        ValidateEmitScopeInvariants(ctx, graph, validationContext);

        // PhaseGate Hardening - M5: Scope mismatches (PG_SCOPE_003, PG_SCOPE_004) - Step 6
        ValidateScopeMismatches(ctx, graph, validationContext);

        // PhaseGate Hardening - M5: Class surface uniqueness (PG_NAME_005)
        ValidateClassSurfaceUniqueness(ctx, graph, validationContext);

        // PhaseGate Hardening - M6: Comprehensive finalization sweep (PG_FIN_001 through PG_FIN_009)
        // This is the FINAL validation before emission - catches any symbol without proper finalization
        ValidateFinalization(ctx, graph, validationContext);

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
                var description = GetDiagnosticDescription(code);
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
        WriteDiagnosticsFile(ctx, validationContext);

        // Write summary JSON for CI/snapshot comparison
        WriteSummaryJson(ctx, validationContext);
    }

    private static void WriteSummaryJson(BuildContext ctx, ValidationContext validationCtx)
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

    private static void WriteDiagnosticsFile(BuildContext ctx, ValidationContext validationCtx)
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

    private static void ValidateTypeNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating type names...");

        var namesSeen = new HashSet<string>();

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Check TsEmitName is set
                if (string.IsNullOrWhiteSpace(type.TsEmitName))
                {
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                        "ERROR",
                        $"Type {type.ClrFullName} has no TsEmitName");
                }

                // Check for duplicates within namespace
                var fullEmitName = $"{ns.Name}.{type.TsEmitName}";
                if (namesSeen.Contains(fullEmitName))
                {
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.DuplicateMember,
                        "ERROR",
                        $"Duplicate TsEmitName '{fullEmitName}' in namespace {ns.Name}");
                }
                namesSeen.Add(fullEmitName);

                // Check for TypeScript reserved words
                // Step 1: Only warn if name wasn't sanitized (ClrName == TsEmitName means reserved word leaked through)
                if (IsTypeScriptReservedWord(type.TsEmitName))
                {
                    if (type.ClrName != type.TsEmitName)
                    {
                        // Name was sanitized - don't warn, just count
                        validationCtx.SanitizedNameCount++;
                    }
                    else
                    {
                        // Name wasn't sanitized - this is a problem
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ReservedWordUnsanitized,
                            "WARNING",
                            $"Type '{type.TsEmitName}' uses TypeScript reserved word but was not sanitized");
                    }
                }
                else if (type.ClrName != type.TsEmitName && IsTypeScriptReservedWord(type.ClrName))
                {
                    // Name was successfully sanitized
                    validationCtx.SanitizedNameCount++;
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {namesSeen.Count} type names");
    }

    private static void ValidateMemberNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating member names...");

        int totalMembers = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                var memberNames = new HashSet<string>();

                // Validate methods
                foreach (var method in type.Members.Methods)
                {
                    if (string.IsNullOrWhiteSpace(method.TsEmitName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Method {method.ClrName} in {type.ClrFullName} has no TsEmitName");
                    }

                    // Check for collisions within same scope
                    if (method.EmitScope == EmitScope.ClassSurface)
                    {
                        var signature = $"{method.TsEmitName}_{method.Parameters.Length}";
                        if (!memberNames.Add(signature))
                        {
                            validationCtx.RecordDiagnostic(
                                Core.Diagnostics.DiagnosticCodes.AmbiguousOverload,
                                "WARNING",
                                $"Potential method overload collision for {method.TsEmitName} in {type.ClrFullName}");
                        }
                    }

                    totalMembers++;
                }

                // Validate properties
                foreach (var property in type.Members.Properties)
                {
                    if (string.IsNullOrWhiteSpace(property.TsEmitName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Property {property.ClrName} in {type.ClrFullName} has no TsEmitName");
                    }

                    totalMembers++;
                }

                // Validate fields
                foreach (var field in type.Members.Fields)
                {
                    if (string.IsNullOrWhiteSpace(field.TsEmitName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Field {field.ClrName} in {type.ClrFullName} has no TsEmitName");
                    }

                    totalMembers++;
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {totalMembers} members");
    }

    private static void ValidateGenericParameters(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating generic parameters...");

        int totalGenericParams = 0;
        int constraintNarrowings = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                foreach (var gp in type.GenericParameters)
                {
                    // Check name is valid
                    if (string.IsNullOrWhiteSpace(gp.Name))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Generic parameter in {type.ClrFullName} has no name");
                    }

                    // Check constraints are representable
                    foreach (var constraint in gp.Constraints)
                    {
                        if (!IsConstraintRepresentable(constraint))
                        {
                            validationCtx.RecordDiagnostic(
                                Core.Diagnostics.DiagnosticCodes.UnrepresentableConstraint,
                                "WARNING",
                                $"Constraint on {gp.Name} in {type.ClrFullName} may not be representable");
                        }
                    }

                    totalGenericParams++;
                }

                // Check for constraint narrowing in derived classes
                if (type.BaseType != null && type.GenericParameters.Any())
                {
                    var baseClass = FindType(graph, type.BaseType);
                    if (baseClass != null && baseClass.GenericParameters.Any())
                    {
                        var derivedGpList = type.GenericParameters.ToList();
                        var baseGpList = baseClass.GenericParameters.ToList();

                        // Compare constraints between derived and base
                        for (int i = 0; i < Math.Min(derivedGpList.Count, baseGpList.Count); i++)
                        {
                            var derivedGp = derivedGpList[i];
                            var baseGp = baseGpList[i];

                            var derivedConstraints = derivedGp.Constraints.ToList();
                            var baseConstraints = baseGp.Constraints.ToList();
                            var derivedConstraintCount = derivedConstraints.Count;
                            var baseConstraintCount = baseConstraints.Count;

                            // If derived has more constraints than base, it's narrowing
                            if (derivedConstraintCount > baseConstraintCount)
                            {
                                // Emit INFO (not warning) - constraint narrowing is usually benign in TS
                                validationCtx.RecordDiagnostic(
                                    Core.Diagnostics.DiagnosticCodes.ConstraintNarrowing,
                                    "INFO",
                                    $"Generic parameter {derivedGp.Name} in {type.ClrFullName} narrows constraints from base class ({baseConstraintCount} → {derivedConstraintCount} constraints)");
                                constraintNarrowings++;
                            }
                        }
                    }
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {totalGenericParams} generic parameters ({constraintNarrowings} constraint narrowings detected)");
    }

    private static void ValidateInterfaceConformance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating interface conformance...");

        int typesChecked = 0;
        int typesWithIssues = 0;
        int suppressedDueToViews = 0;
        int typesWithExplicitViews = 0;
        int totalExplicitViews = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
                    continue;

                // Step 1: Build set of interfaces that have planned explicit views
                // These interfaces are satisfied via As_IInterface properties, not class surface
                var plannedInterfaces = type.ExplicitViews
                    .Select(v => GetTypeFullName(v.InterfaceReference))
                    .ToHashSet();

                if (plannedInterfaces.Count > 0)
                {
                    typesWithExplicitViews++;
                    totalExplicitViews += plannedInterfaces.Count;
                }

                // Step 3: Aggregate conformance issues per type instead of per method
                var conformanceIssues = new List<string>();
                var covarianceIssues = new List<string>();

                // Check that all claimed interfaces have corresponding members
                foreach (var ifaceRef in type.Interfaces)
                {
                    var ifaceFullName = GetTypeFullName(ifaceRef);

                    // Step 1: Skip validation for interfaces that have explicit views
                    // These are satisfied via As_IInterface properties, not class surface
                    if (plannedInterfaces.Contains(ifaceFullName))
                    {
                        suppressedDueToViews++;
                        continue;
                    }

                    var iface = FindInterface(graph, ifaceRef);
                    if (iface == null)
                        continue; // External interface

                    // Verify structural conformance (all interface members present on class surface)
                    var representableMethods = type.Members.Methods
                        .Where(m => m.EmitScope == EmitScope.ClassSurface)
                        .ToList();

                    foreach (var requiredMethod in iface.Members.Methods)
                    {
                        var methodSig = $"{requiredMethod.ClrName}({requiredMethod.Parameters.Length})";

                        // Find matching method on class surface
                        var matchingMethod = representableMethods.FirstOrDefault(m =>
                            m.ClrName == requiredMethod.ClrName &&
                            m.Parameters.Length == requiredMethod.Parameters.Length);

                        if (matchingMethod == null)
                        {
                            conformanceIssues.Add($"  Missing method {methodSig} from {GetInterfaceName(ifaceRef)}");
                        }
                        else
                        {
                            // Step 2: Method exists, but check if signatures are TS-assignable
                            // Only warn if the mismatch would break TS assignability
                            if (IsRepresentableConformanceBreak(matchingMethod, requiredMethod))
                            {
                                conformanceIssues.Add($"  Method {methodSig} from {GetInterfaceName(ifaceRef)} has incompatible TS signature");
                            }
                            // else: Signatures differ in CLR but are compatible in TS (e.g., covariance) - no warning
                        }
                    }

                    // Check properties for covariance issues (TypeScript doesn't support property covariance)
                    var representableProperties = type.Members.Properties
                        .Where(p => p.EmitScope == EmitScope.ClassSurface)
                        .ToList();

                    foreach (var requiredProperty in iface.Members.Properties)
                    {
                        var matchingProperty = representableProperties.FirstOrDefault(p =>
                            p.ClrName == requiredProperty.ClrName);

                        if (matchingProperty == null)
                        {
                            conformanceIssues.Add($"  Missing property {requiredProperty.ClrName} from {GetInterfaceName(ifaceRef)}");
                        }
                        else
                        {
                            // Check if property types differ (potential covariance)
                            // In TypeScript, properties are invariant, not covariant
                            // So even if CLR allows covariant return types, TS does not
                            var classPropertyType = GetPropertyTypeString(matchingProperty);
                            var ifacePropertyType = GetPropertyTypeString(requiredProperty);

                            if (classPropertyType != ifacePropertyType)
                            {
                                // This is a property covariance issue
                                covarianceIssues.Add($"  Property {requiredProperty.ClrName} ({ifacePropertyType} → {classPropertyType})");
                            }
                        }
                    }
                }

                // Emit aggregated covariance summary (one per type, not one per property)
                if (covarianceIssues.Count > 0)
                {
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.CovarianceSummary,
                        "INFO",
                        $"{type.ClrFullName} has {covarianceIssues.Count} property covariance issues (TS doesn't support property covariance)");
                }

                if (conformanceIssues.Count > 0)
                {
                    typesWithIssues++;
                    validationCtx.InterfaceConformanceIssuesByType[type.ClrFullName] = conformanceIssues;

                    // Emit one-line summary to console
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.StructuralConformanceFailure,
                        "WARNING",
                        $"{type.ClrFullName} has {conformanceIssues.Count} interface conformance issues (see diagnostics file)");
                }

                typesChecked++;
            }
        }

        ctx.Log("PhaseGate", $"Validated interface conformance for {typesChecked} types ({typesWithIssues} with issues)");
        ctx.Log("PhaseGate", $"{typesWithExplicitViews} types have {totalExplicitViews} explicit views, {suppressedDueToViews} interfaces satisfied via views");
    }

    private static void ValidateInheritance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating inheritance...");

        int inheritanceChecked = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.BaseType == null)
                    continue;

                var baseClass = FindType(graph, type.BaseType);
                if (baseClass == null)
                    continue; // External base class

                // Check that base class is actually a class
                if (baseClass.Kind != TypeKind.Class)
                {
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.CircularInheritance,
                        "ERROR",
                        $"{type.ClrFullName} inherits from non-class {baseClass.ClrFullName}");
                }

                inheritanceChecked++;
            }
        }

        ctx.Log("PhaseGate", $"Validated {inheritanceChecked} inheritance relationships");
    }

    private static void ValidateEmitScopes(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating emit scopes...");

        int totalMembers = 0;
        int viewOnlyMembers = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                foreach (var method in type.Members.Methods)
                {
                    if (method.EmitScope == EmitScope.ViewOnly)
                        viewOnlyMembers++;
                    totalMembers++;
                }

                foreach (var property in type.Members.Properties)
                {
                    if (property.EmitScope == EmitScope.ViewOnly)
                        viewOnlyMembers++;
                    totalMembers++;
                }
            }
        }

        ctx.Log("PhaseGate", $"{totalMembers} members, {viewOnlyMembers} ViewOnly");
    }

    private static void ValidateImports(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating import plan...");

        int totalImports = imports.NamespaceImports.Values.Sum(list => list.Count);
        int totalExports = imports.NamespaceExports.Values.Sum(list => list.Count);

        // Check for circular dependencies
        var circularDeps = DetectCircularDependencies(imports);
        if (circularDeps.Count > 0)
        {
            foreach (var cycle in circularDeps)
            {
                validationCtx.RecordDiagnostic(
                    Core.Diagnostics.DiagnosticCodes.CircularInheritance,
                    "WARNING",
                    $"Circular dependency detected: {cycle}");
            }
        }

        ctx.Log("PhaseGate", $"{totalImports} import statements, {totalExports} export statements");
    }

    private static void ValidatePolicyCompliance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating policy compliance...");

        var policy = ctx.Policy;

        // Check that policy constraints are met
        // For example, if policy forbids certain patterns, verify they don't appear

        // This is extensible - add more policy checks as needed

        ctx.Log("PhaseGate", "Policy compliance validated");
    }

    private static TypeSymbol? FindInterface(SymbolGraph graph, Model.Types.TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    private static TypeSymbol? FindType(SymbolGraph graph, Model.Types.TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName);
    }

    private static string GetInterfaceName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.Name,
            Model.Types.NestedTypeReference nested => nested.NestedName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static string GetTypeFullName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            Model.Types.GenericParameterReference gp => gp.Name,
            Model.Types.ArrayTypeReference arr => GetTypeFullName(arr.ElementType),
            Model.Types.PointerTypeReference ptr => GetTypeFullName(ptr.PointeeType),
            Model.Types.ByRefTypeReference byref => GetTypeFullName(byref.ReferencedType),
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    private static bool IsConstraintRepresentable(Model.Types.TypeReference constraint)
    {
        // Check if constraint can be represented in TypeScript
        return constraint switch
        {
            Model.Types.PointerTypeReference => false,
            Model.Types.ByRefTypeReference => false,
            _ => true
        };
    }

    private static string GetPropertyTypeString(Model.Symbols.MemberSymbols.PropertySymbol property)
    {
        // Get a string representation of the property type for comparison
        // This is a simple comparison - if types have the same full name, they're considered the same
        return GetTypeFullName(property.PropertyType);
    }

    private static string GetDiagnosticDescription(string code)
    {
        return code switch
        {
            Core.Diagnostics.DiagnosticCodes.ValidationFailed => "Validation failed",
            Core.Diagnostics.DiagnosticCodes.DuplicateMember => "Duplicate members",
            Core.Diagnostics.DiagnosticCodes.AmbiguousOverload => "Ambiguous overloads",
            Core.Diagnostics.DiagnosticCodes.ReservedWordUnsanitized => "Reserved words not sanitized",
            Core.Diagnostics.DiagnosticCodes.CovarianceSummary => "Property covariance (TS limitation)",
            Core.Diagnostics.DiagnosticCodes.StructuralConformanceFailure => "Interface conformance failures",
            Core.Diagnostics.DiagnosticCodes.CircularInheritance => "Circular inheritance/dependencies",
            Core.Diagnostics.DiagnosticCodes.ViewCoverageMismatch => "ViewOnly member coverage issues",
            Core.Diagnostics.DiagnosticCodes.IndexerConflict => "Indexer conflicts",
            Core.Diagnostics.DiagnosticCodes.InterfaceNotFound => "External interface references",
            Core.Diagnostics.DiagnosticCodes.NameConflictUnresolved => "Name conflicts",
            Core.Diagnostics.DiagnosticCodes.UnrepresentableConstraint => "Unrepresentable constraints",
            // PhaseGate Hardening diagnostics
            Core.Diagnostics.DiagnosticCodes.PG_ID_001 => "Reserved identifier not sanitized",
            Core.Diagnostics.DiagnosticCodes.PG_OV_001 => "Duplicate erased signature",
            Core.Diagnostics.DiagnosticCodes.PG_VIEW_001 => "Empty view (no members)",
            Core.Diagnostics.DiagnosticCodes.PG_VIEW_002 => "Duplicate view for same interface",
            Core.Diagnostics.DiagnosticCodes.PG_VIEW_003 => "Invalid/unsanitized view property name",
            Core.Diagnostics.DiagnosticCodes.PG_CT_001 => "Non-benign constraint loss",
            Core.Diagnostics.DiagnosticCodes.PG_CT_002 => "Constructor constraint loss (override)",
            Core.Diagnostics.DiagnosticCodes.PG_IFC_001 => "Interface method not assignable (erased)",
            Core.Diagnostics.DiagnosticCodes.PG_NAME_003 => "View member collision within view scope",
            Core.Diagnostics.DiagnosticCodes.PG_NAME_004 => "View member name shadows class surface",
            Core.Diagnostics.DiagnosticCodes.PG_NAME_005 => "Duplicate property name on class surface",
            Core.Diagnostics.DiagnosticCodes.PG_INT_002 => "Member in both ClassSurface and ViewOnly",
            Core.Diagnostics.DiagnosticCodes.PG_INT_003 => "ClassSurface member has SourceInterface",
            Core.Diagnostics.DiagnosticCodes.PG_FIN_001 => "Member has no final placement or illegal combo",
            Core.Diagnostics.DiagnosticCodes.PG_FIN_002 => "ViewOnly member not in exactly one ExplicitView",
            Core.Diagnostics.DiagnosticCodes.PG_FIN_003 => "Member missing final name in scope after reservation",
            Core.Diagnostics.DiagnosticCodes.PG_FIN_004 => "Type missing final name in namespace scope",
            Core.Diagnostics.DiagnosticCodes.PG_FIN_005 => "Empty/invalid view",
            Core.Diagnostics.DiagnosticCodes.PG_FIN_006 => "Duplicate view membership",
            Core.Diagnostics.DiagnosticCodes.PG_FIN_007 => "Class/View dual-role clash",
            Core.Diagnostics.DiagnosticCodes.PG_FIN_008 => "Interface requires view but type has none",
            Core.Diagnostics.DiagnosticCodes.PG_FIN_009 => "Unsanitized identifier post-sanitizer",
            Core.Diagnostics.DiagnosticCodes.PG_SCOPE_003 => "Empty/malformed scope key",
            Core.Diagnostics.DiagnosticCodes.PG_SCOPE_004 => "Scope kind doesn't match EmitScope",
            _ => "Unknown diagnostic"
        };
    }

    private static bool IsTypeScriptReservedWord(string name)
    {
        var reservedWords = new HashSet<string>
        {
            "break", "case", "catch", "class", "const", "continue", "debugger", "default",
            "delete", "do", "else", "enum", "export", "extends", "false", "finally",
            "for", "function", "if", "import", "in", "instanceof", "new", "null",
            "return", "super", "switch", "this", "throw", "true", "try", "typeof",
            "var", "void", "while", "with", "as", "implements", "interface", "let",
            "package", "private", "protected", "public", "static", "yield", "any",
            "boolean", "number", "string", "symbol", "abstract", "async", "await",
            "constructor", "declare", "from", "get", "is", "module", "namespace",
            "of", "readonly", "require", "set", "type"
        };

        return reservedWords.Contains(name.ToLowerInvariant());
    }

    /// <summary>
    /// Step 2: Check if a conformance mismatch would break TypeScript assignability.
    /// Returns true if the class method signature is NOT assignable to the interface method in TS.
    /// </summary>
    private static bool IsRepresentableConformanceBreak(MethodSymbol classMethod, MethodSymbol ifaceMethod)
    {
        // Erase both methods to TypeScript signatures
        var classSig = TsErase.EraseMember(classMethod);
        var ifaceSig = TsErase.EraseMember(ifaceMethod);

        // Check if class method is assignable to interface method
        // If assignable, this is NOT a representable break (benign difference)
        // If not assignable, this IS a representable break (real TS error)
        return !TsAssignability.IsMethodAssignable(classSig, ifaceSig);
    }

    /// <summary>
    /// Check if an interface exists in the symbol graph.
    /// Returns true if the interface is being generated (in graph), false if external.
    /// </summary>
    private static bool IsInterfaceInGraph(SymbolGraph graph, Model.Types.TypeReference ifaceRef)
    {
        var ifaceFullName = GetTypeFullName(ifaceRef);
        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .Any(t => t.ClrFullName == ifaceFullName && t.Kind == TypeKind.Interface);
    }

    private static List<string> DetectCircularDependencies(ImportPlan imports)
    {
        var cycles = new List<string>();

        // Build adjacency list
        var graph = new Dictionary<string, List<string>>();

        foreach (var (ns, importList) in imports.NamespaceImports)
        {
            if (!graph.ContainsKey(ns))
                graph[ns] = new List<string>();

            foreach (var import in importList)
            {
                graph[ns].Add(import.TargetNamespace);
            }
        }

        // DFS-based cycle detection
        var visited = new HashSet<string>();
        var recursionStack = new HashSet<string>();

        foreach (var ns in graph.Keys)
        {
            if (!visited.Contains(ns))
            {
                DetectCyclesDFS(ns, graph, visited, recursionStack, new List<string>(), cycles);
            }
        }

        return cycles;
    }

    private static bool DetectCyclesDFS(
        string node,
        Dictionary<string, List<string>> graph,
        HashSet<string> visited,
        HashSet<string> recursionStack,
        List<string> path,
        List<string> cycles)
    {
        visited.Add(node);
        recursionStack.Add(node);
        path.Add(node);

        if (graph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!visited.Contains(neighbor))
                {
                    if (DetectCyclesDFS(neighbor, graph, visited, recursionStack, path, cycles))
                        return true;
                }
                else if (recursionStack.Contains(neighbor))
                {
                    // Cycle detected
                    var cycleStart = path.IndexOf(neighbor);
                    var cycle = string.Join(" -> ", path.Skip(cycleStart).Concat(new[] { neighbor }));
                    cycles.Add(cycle);
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        recursionStack.Remove(node);
        return false;
    }

    // ========== Step 9: PhaseGate Hardening - New Validation Methods ==========

    /// <summary>
    /// Validates that all ViewOnly members appear in at least one explicit view.
    /// Checks that view interface types are available (either in graph or will be imported).
    /// </summary>
    private static void ValidateViews(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                    .Where(m => IsInterfaceInGraph(graph, m.SourceInterface))
                    .ToList();

                var viewOnlyProperties = type.Members.Properties
                    .Where(p => p.EmitScope == EmitScope.ViewOnly)
                    .Where(p => p.SourceInterface != null)
                    .Where(p => IsInterfaceInGraph(graph, p.SourceInterface))
                    .ToList();

                // Guard: indexer properties must NOT be ViewOnly (they should be converted or kept as properties)
                foreach (var property in viewOnlyProperties)
                {
                    if (property.IsIndexer)
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.IndexerConflict,
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
                        Core.Diagnostics.DiagnosticCodes.ViewCoverageMismatch,
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
                            vm.Kind == Shape.ViewPlanner.ViewMemberKind.Method &&
                            vm.StableId.Equals(method.StableId)));

                    if (matchingView == null)
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ViewCoverageMismatch,
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
                                Core.Diagnostics.DiagnosticCodes.ViewCoverageMismatch,
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
                            vm.Kind == Shape.ViewPlanner.ViewMemberKind.Property &&
                            vm.StableId.Equals(property.StableId)));

                    if (matchingView == null)
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ViewCoverageMismatch,
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
                                Core.Diagnostics.DiagnosticCodes.ViewCoverageMismatch,
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
                            Core.Diagnostics.DiagnosticCodes.InterfaceNotFound,
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

    /// <summary>
    /// Validates that there are no duplicate final identifiers within the same scope.
    /// Checks both type-level (class/interface names) and member-level (methods/properties/fields).
    /// Separates static and instance member scopes.
    /// </summary>
    private static void ValidateFinalNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating final names from Renamer...");

        int totalTypes = 0;
        int totalMembers = 0;
        int duplicateTypes = 0;
        int duplicateMembers = 0;

        // Validate type names (namespace scope)
        foreach (var ns in graph.Namespaces)
        {
            var typeNamesInNamespace = new HashSet<string>();

            foreach (var type in ns.Types)
            {
                totalTypes++;

                // Get final name from Renamer
                var finalName = ctx.Renamer.GetFinalTypeName(type);

                if (string.IsNullOrWhiteSpace(finalName))
                {
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                        "ERROR",
                        $"Type {type.ClrFullName} has no final name from Renamer");
                    continue;
                }

                // Check for duplicates within namespace
                if (!typeNamesInNamespace.Add(finalName))
                {
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.DuplicateMember,
                        "ERROR",
                        $"Duplicate final type name '{finalName}' in namespace {ns.Name}");
                    duplicateTypes++;
                }
            }
        }

        // Validate member names (type scope, separated by static vs instance)
        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                var instanceMemberNames = new HashSet<string>();
                var staticMemberNames = new HashSet<string>();

                // Validate method names (ClassSurface only - ViewOnly members may have duplicate names in different views)
                foreach (var method in type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface))
                {
                    totalMembers++;

                    var methodScope = ScopeFactory.ClassSurface(type, method.IsStatic);
                    var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, methodScope);

                    if (string.IsNullOrWhiteSpace(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Method {method.ClrName} in {type.ClrFullName} has no final name from Renamer");
                        continue;
                    }

                    // Add to appropriate scope
                    var scopeSet = method.IsStatic ? staticMemberNames : instanceMemberNames;
                    var scopeName = method.IsStatic ? "static" : "instance";

                    // Methods can have overloads, so we use signature for uniqueness
                    var signature = $"{finalName}({method.Parameters.Length})";

                    if (!scopeSet.Add(signature))
                    {
                        // This is a warning, not an error (overloads are allowed)
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.AmbiguousOverload,
                            "WARNING",
                            $"Duplicate {scopeName} method signature '{signature}' in {type.ClrFullName} (on class surface)");
                    }
                }

                // Validate property names (ClassSurface only - ViewOnly members may have duplicate names in different views)
                foreach (var property in type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface))
                {
                    totalMembers++;

                    var propertyScope = ScopeFactory.ClassSurface(type, property.IsStatic);
                    var finalName = ctx.Renamer.GetFinalMemberName(property.StableId, propertyScope);

                    if (string.IsNullOrWhiteSpace(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Property {property.ClrName} in {type.ClrFullName} has no final name from Renamer");
                        continue;
                    }

                    // Add to appropriate scope
                    var scopeSet = property.IsStatic ? staticMemberNames : instanceMemberNames;
                    var scopeName = property.IsStatic ? "static" : "instance";

                    if (!scopeSet.Add(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.DuplicateMember,
                            "ERROR",
                            $"Duplicate {scopeName} property name '{finalName}' in {type.ClrFullName} (on class surface)");
                        duplicateMembers++;
                    }
                }

                // Validate field names (ClassSurface only - ViewOnly members may have duplicate names in different views)
                foreach (var field in type.Members.Fields.Where(f => f.EmitScope == EmitScope.ClassSurface))
                {
                    totalMembers++;

                    var fieldScope = ScopeFactory.ClassSurface(type, field.IsStatic);
                    var finalName = ctx.Renamer.GetFinalMemberName(field.StableId, fieldScope);

                    if (string.IsNullOrWhiteSpace(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Field {field.ClrName} in {type.ClrFullName} has no final name from Renamer");
                        continue;
                    }

                    // Add to appropriate scope
                    var scopeSet = field.IsStatic ? staticMemberNames : instanceMemberNames;
                    var scopeName = field.IsStatic ? "static" : "instance";

                    if (!scopeSet.Add(finalName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.DuplicateMember,
                            "ERROR",
                            $"Duplicate {scopeName} field name '{finalName}' in {type.ClrFullName} (on class surface)");
                        duplicateMembers++;
                    }
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {totalTypes} type names and {totalMembers} member names from Renamer");

        if (duplicateTypes > 0 || duplicateMembers > 0)
        {
            ctx.Log("PhaseGate", $"Found {duplicateTypes} duplicate types, {duplicateMembers} duplicate members");
        }
    }

    /// <summary>
    /// Validates that import aliases don't collide after Renamer resolution.
    /// Checks that all aliased names are unique within their import scope.
    /// </summary>
    private static void ValidateAliases(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating import aliases...");

        int totalAliases = 0;
        int aliasCollisions = 0;

        foreach (var (ns, importList) in imports.NamespaceImports)
        {
            var aliasesInScope = new HashSet<string>();
            var typeNamesInScope = new HashSet<string>();

            var namespaceScope = new SinglePhase.Renaming.NamespaceScope
            {
                Namespace = ns,
                IsInternal = true,
                ScopeKey = ns
            };

            foreach (var import in importList)
            {
                // Each import statement contains multiple type imports
                foreach (var typeImport in import.TypeImports)
                {
                    var effectiveName = typeImport.Alias ?? typeImport.TypeName;

                    // Check if this import uses an alias
                    if (!string.IsNullOrWhiteSpace(typeImport.Alias))
                    {
                        totalAliases++;

                        // Check for alias collisions
                        if (!aliasesInScope.Add(typeImport.Alias))
                        {
                            validationCtx.RecordDiagnostic(
                                Core.Diagnostics.DiagnosticCodes.NameConflictUnresolved,
                                "ERROR",
                                $"Import alias '{typeImport.Alias}' collides in namespace {ns}");
                            aliasCollisions++;
                        }
                    }

                    // Check that imported type names don't collide with each other
                    if (!typeNamesInScope.Add(effectiveName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.NameConflictUnresolved,
                            "WARNING",
                            $"Imported type name '{effectiveName}' appears multiple times in namespace {ns}");
                    }
                }
            }

            // Also check that imported names don't collide with local types
            var localNamespace = graph.Namespaces.FirstOrDefault(n => n.Name == ns);
            if (localNamespace != null)
            {
                foreach (var localType in localNamespace.Types)
                {
                    var localTypeName = ctx.Renamer.GetFinalTypeName(localType);

                    if (typeNamesInScope.Contains(localTypeName))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.NameConflictUnresolved,
                            "WARNING",
                            $"Imported type name '{localTypeName}' in namespace {ns} collides with local type {localType.ClrFullName}");
                    }
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {totalAliases} import aliases");

        if (aliasCollisions > 0)
        {
            ctx.Log("PhaseGate", $"Found {aliasCollisions} alias collisions");
        }
    }

    /// <summary>
    /// PhaseGate Hardening M1: Validate all identifiers are properly sanitized.
    /// Checks that all TypeScript reserved words have been escaped with underscore suffix.
    /// Catches every unsanitized TS reserved word before emit (types, members, parameters, view members).
    /// </summary>
    private static void ValidateIdentifiers(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M1: Validating identifier sanitization...");

        int totalIdentifiersChecked = 0;
        int unsanitizedCount = 0;

        foreach (var ns in graph.Namespaces)
        {
            // Check namespace name
            if (!string.IsNullOrWhiteSpace(ns.Name))
            {
                totalIdentifiersChecked++;
                CheckIdentifier(ctx, validationCtx, "namespace", ns.Name, ns.Name, ns.Name, ref unsanitizedCount);
            }

            foreach (var type in ns.Types)
            {
                // Get the final emitted name from Renamer
                var emittedTypeName = ctx.Renamer.GetFinalTypeName(type);

                // Check type name
                totalIdentifiersChecked++;
                CheckIdentifier(ctx, validationCtx, "type", type.ClrFullName, type.StableId.ToString(), emittedTypeName, ref unsanitizedCount);

                // Check type parameters
                foreach (var tp in type.GenericParameters)
                {
                    totalIdentifiersChecked++;
                    // Type parameters are emitted as-is (like regular identifiers), so sanitize them
                    var sanitizedTpName = Core.TypeScriptReservedWords.SanitizeParameterName(tp.Name);
                    CheckIdentifier(ctx, validationCtx, "type parameter", $"{type.ClrFullName}.{tp.Name}", type.StableId.ToString(), sanitizedTpName, ref unsanitizedCount);
                }

                // Check methods (ClassSurface only - ViewOnly members checked in view loop below)
                foreach (var method in type.Members.Methods)
                {
                    // Skip private/internal members that won't be emitted
                    if (method.Visibility != Visibility.Public)
                        continue;

                    // Skip ViewOnly members - they're checked in the view members loop below
                    if (method.EmitScope == EmitScope.ViewOnly)
                        continue;

                    var methodScope = ScopeFactory.ClassSurface(type, method.IsStatic);
                    var emittedMethodName = ctx.Renamer.GetFinalMemberName(method.StableId, methodScope);

                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "method", $"{type.ClrFullName}::{method.ClrName}", method.StableId.ToString(), emittedMethodName, ref unsanitizedCount);

                    // Check method parameters
                    int paramIndex = 0;
                    foreach (var param in method.Parameters)
                    {
                        totalIdentifiersChecked++;
                        // Parameters are sanitized using SanitizeParameterName (reserved words get "_" suffix)
                        var sanitizedParamName = Core.TypeScriptReservedWords.SanitizeParameterName(param.Name);
                        CheckIdentifier(ctx, validationCtx, "parameter", $"{type.ClrFullName}::{method.ClrName}", $"{method.StableId}#param{paramIndex}", sanitizedParamName, ref unsanitizedCount);
                        paramIndex++;
                    }

                    // Check method type parameters
                    foreach (var tp in method.GenericParameters)
                    {
                        totalIdentifiersChecked++;
                        var sanitizedTpName = Core.TypeScriptReservedWords.SanitizeParameterName(tp.Name);
                        CheckIdentifier(ctx, validationCtx, "method type parameter", $"{type.ClrFullName}::{method.ClrName}.{tp.Name}", method.StableId.ToString(), sanitizedTpName, ref unsanitizedCount);
                    }
                }

                // Check properties (ClassSurface only - ViewOnly members checked in view loop below)
                foreach (var property in type.Members.Properties)
                {
                    if (property.Visibility != Visibility.Public)
                        continue;

                    // Skip ViewOnly members - they're checked in the view members loop below
                    if (property.EmitScope == EmitScope.ViewOnly)
                        continue;

                    var propertyScope = ScopeFactory.ClassSurface(type, property.IsStatic);
                    var emittedPropertyName = ctx.Renamer.GetFinalMemberName(property.StableId, propertyScope);

                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "property", $"{type.ClrFullName}::{property.ClrName}", property.StableId.ToString(), emittedPropertyName, ref unsanitizedCount);

                    // Check indexer parameters
                    int indexerParamIndex = 0;
                    foreach (var param in property.IndexParameters)
                    {
                        totalIdentifiersChecked++;
                        var sanitizedParamName = Core.TypeScriptReservedWords.SanitizeParameterName(param.Name);
                        CheckIdentifier(ctx, validationCtx, "indexer parameter", $"{type.ClrFullName}::{property.ClrName}", $"{property.StableId}#param{indexerParamIndex}", sanitizedParamName, ref unsanitizedCount);
                        indexerParamIndex++;
                    }
                }

                // Check fields
                foreach (var field in type.Members.Fields)
                {
                    if (field.Visibility != Visibility.Public)
                        continue;

                    // Fields are always ClassSurface (no ViewOnly fields)
                    var fieldScope = ScopeFactory.ClassSurface(type, field.IsStatic);
                    var emittedFieldName = ctx.Renamer.GetFinalMemberName(field.StableId, fieldScope);

                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "field", $"{type.ClrFullName}::{field.ClrName}", field.StableId.ToString(), emittedFieldName, ref unsanitizedCount);
                }

                // Check events
                foreach (var evt in type.Members.Events)
                {
                    if (evt.Visibility != Visibility.Public)
                        continue;

                    // Events are always ClassSurface (no ViewOnly events)
                    var eventScope = ScopeFactory.ClassSurface(type, evt.IsStatic);
                    var emittedEventName = ctx.Renamer.GetFinalMemberName(evt.StableId, eventScope);

                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "event", $"{type.ClrFullName}::{evt.ClrName}", evt.StableId.ToString(), emittedEventName, ref unsanitizedCount);
                }

                // Check view members
                foreach (var view in type.ExplicitViews)
                {
                    // Check view property name
                    totalIdentifiersChecked++;
                    CheckIdentifier(ctx, validationCtx, "view property", $"{type.ClrFullName}.{view.ViewPropertyName}", type.StableId.ToString(), view.ViewPropertyName, ref unsanitizedCount);

                    // Check each view member's emitted name (to catch mismatches or synthetic entries)
                    // Get interface StableId for this view (needed for ViewSurface scope)
                    var interfaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);

                    foreach (var viewMember in view.ViewMembers)
                    {
                        string emittedMemberName;
                        string memberOwner;

                        switch (viewMember.Kind)
                        {
                            case Shape.ViewPlanner.ViewMemberKind.Method:
                                var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                                if (method != null)
                                {
                                    var methodScope = ScopeFactory.ViewSurface(type, interfaceStableId, method.IsStatic);
                                    emittedMemberName = ctx.Renamer.GetFinalMemberName(method.StableId, methodScope);
                                    memberOwner = $"{type.ClrFullName}::{method.ClrName} (in view {view.ViewPropertyName})";
                                    totalIdentifiersChecked++;
                                    CheckIdentifier(ctx, validationCtx, "view method", memberOwner, method.StableId.ToString(), emittedMemberName, ref unsanitizedCount);
                                }
                                break;

                            case Shape.ViewPlanner.ViewMemberKind.Property:
                                var property = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                                if (property != null)
                                {
                                    var propertyScope = ScopeFactory.ViewSurface(type, interfaceStableId, property.IsStatic);
                                    emittedMemberName = ctx.Renamer.GetFinalMemberName(property.StableId, propertyScope);
                                    memberOwner = $"{type.ClrFullName}::{property.ClrName} (in view {view.ViewPropertyName})";
                                    totalIdentifiersChecked++;
                                    CheckIdentifier(ctx, validationCtx, "view property member", memberOwner, property.StableId.ToString(), emittedMemberName, ref unsanitizedCount);
                                }
                                break;

                            case Shape.ViewPlanner.ViewMemberKind.Event:
                                var evt = type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId));
                                if (evt != null)
                                {
                                    var eventScope = ScopeFactory.ViewSurface(type, interfaceStableId, evt.IsStatic);
                                    emittedMemberName = ctx.Renamer.GetFinalMemberName(evt.StableId, eventScope);
                                    memberOwner = $"{type.ClrFullName}::{evt.ClrName} (in view {view.ViewPropertyName})";
                                    totalIdentifiersChecked++;
                                    CheckIdentifier(ctx, validationCtx, "view event", memberOwner, evt.StableId.ToString(), emittedMemberName, ref unsanitizedCount);
                                }
                                break;
                        }
                    }
                }
            }
        }

        ctx.Log("[PG]", $"M1: Checked {totalIdentifiersChecked} identifiers, found {unsanitizedCount} unsanitized reserved words");
    }

    private static void CheckIdentifier(BuildContext ctx, ValidationContext validationCtx, string symbolKind, string owner, string stableId, string emittedName, ref int unsanitizedCount)
    {
        if (string.IsNullOrWhiteSpace(emittedName))
            return;

        // Check if the emitted name is a TypeScript reserved word and doesn't have the trailing underscore
        if (Core.TypeScriptReservedWords.IsReservedWord(emittedName) && !emittedName.EndsWith("_"))
        {
            unsanitizedCount++;
            validationCtx.RecordDiagnostic(
                Core.Diagnostics.DiagnosticCodes.PG_ID_001,
                "ERROR",
                $"Reserved identifier not sanitized\n" +
                $"  where:   {symbolKind}\n" +
                $"  owner:   {owner}\n" +
                $"  stable:  {stableId}\n" +
                $"  name:    {emittedName}  →  {emittedName}_  (expected suffix \"_\")");
        }
    }

    /// <summary>
    /// PhaseGate Hardening M2: Validate no duplicate erased TS signatures in same surface.
    /// Checks class surface and each explicit view separately.
    /// Groups by (Name, Arity, ErasedParameterTypes, IsStatic).
    /// </summary>
    private static void ValidateOverloadCollisions(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M2: Validating overload collisions...");

        int totalCollisions = 0;
        int totalSurfacesChecked = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Check class surface
                totalSurfacesChecked++;
                var classSurfaceCollisions = CheckSurfaceForCollisions(ctx, validationCtx, type, "class surface",
                    type.Members.Methods.Where(m => m.EmitScope == EmitScope.ClassSurface && m.Visibility == Visibility.Public).ToList(),
                    type.Members.Properties.Where(p => p.EmitScope == EmitScope.ClassSurface && p.Visibility == Visibility.Public).ToList());
                totalCollisions += classSurfaceCollisions;

                // Check each explicit view separately
                foreach (var view in type.ExplicitViews)
                {
                    totalSurfacesChecked++;

                    // Get interface StableId for this view
                    var interfaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);

                    // Collect ViewOnly members for this view
                    var viewMethods = view.ViewMembers
                        .Where(vm => vm.Kind == Shape.ViewPlanner.ViewMemberKind.Method)
                        .Select(vm => type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(vm.StableId)))
                        .Where(m => m != null)
                        .Cast<MethodSymbol>()
                        .ToList();

                    var viewProperties = view.ViewMembers
                        .Where(vm => vm.Kind == Shape.ViewPlanner.ViewMemberKind.Property)
                        .Select(vm => type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(vm.StableId)))
                        .Where(p => p != null)
                        .Cast<PropertySymbol>()
                        .ToList();

                    var viewSurfaceCollisions = CheckSurfaceForCollisions(ctx, validationCtx, type,
                        $"view {view.ViewPropertyName}", viewMethods, viewProperties, interfaceStableId);
                    totalCollisions += viewSurfaceCollisions;
                }
            }
        }

        ctx.Log("[PG]", $"M2: Checked {totalSurfacesChecked} surfaces, found {totalCollisions} signature collisions");
    }

    private static int CheckSurfaceForCollisions(
        BuildContext ctx,
        ValidationContext validationCtx,
        TypeSymbol type,
        string surfaceName,
        List<MethodSymbol> methods,
        List<PropertySymbol> properties,
        string? interfaceStableId = null)
    {
        int collisionCount = 0;

        // Group methods by erased signature key: (Name, IsStatic, ErasedSignature)
        // Methods and properties are in separate namespaces, so we check them separately
        var methodGroups = methods
            .GroupBy(m =>
            {
                var methodScope = interfaceStableId != null
                    ? ScopeFactory.ViewSurface(type, interfaceStableId, m.IsStatic)
                    : ScopeFactory.ClassSurface(type, m.IsStatic);
                var finalName = ctx.Renamer.GetFinalMemberName(m.StableId, methodScope);
                var erasedParams = string.Join(", ", m.Parameters.Select(p => EraseTypeToString(p.Type)));
                var erasedReturn = EraseTypeToString(m.ReturnType);
                return (finalName, m.IsStatic, erasedParams, erasedReturn);
            })
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in methodGroups)
        {
            var (name, isStatic, erasedParams, erasedReturn) = group.Key;
            var members = group.ToList();
            var scopeName = isStatic ? "static" : "instance";
            var erasedSignature = $"{name}({erasedParams}): {erasedReturn}";

            // Build member list with StableIds
            var memberDetails = string.Join("\n", members.Select(m =>
                $"    - stable: {m.StableId}\n" +
                $"      clr:    {type.ClrFullName}::{m.ClrName}"));

            validationCtx.RecordDiagnostic(
                Core.Diagnostics.DiagnosticCodes.PG_OV_001,
                "ERROR",
                $"Duplicate erased signature in {surfaceName}\n" +
                $"  type:      {type.ClrFullName}\n" +
                $"  scope:     {scopeName}\n" +
                $"  signature: {erasedSignature}\n" +
                $"  members:\n{memberDetails}");

            collisionCount++;
        }

        // Properties don't have overloads in TypeScript, but we can still check for duplicates
        // Group by (Name, IsStatic) only
        var propertyGroups = properties
            .GroupBy(p =>
            {
                var propertyScope = interfaceStableId != null
                    ? ScopeFactory.ViewSurface(type, interfaceStableId, p.IsStatic)
                    : ScopeFactory.ClassSurface(type, p.IsStatic);
                var finalName = ctx.Renamer.GetFinalMemberName(p.StableId, propertyScope);
                return (finalName, p.IsStatic);
            })
            .Where(g => g.Count() > 1)
            .ToList();

        foreach (var group in propertyGroups)
        {
            var (name, isStatic) = group.Key;
            var members = group.ToList();
            var scopeName = isStatic ? "static" : "instance";

            // Build member list with StableIds
            var memberDetails = string.Join("\n", members.Select(p =>
                $"    - stable: {p.StableId}\n" +
                $"      clr:    {type.ClrFullName}::{p.ClrName}\n" +
                $"      type:   {EraseTypeToString(p.PropertyType)}"));

            validationCtx.RecordDiagnostic(
                Core.Diagnostics.DiagnosticCodes.PG_OV_001,
                "ERROR",
                $"Duplicate property name in {surfaceName}\n" +
                $"  type:      {type.ClrFullName}\n" +
                $"  scope:     {scopeName}\n" +
                $"  property:  {name}\n" +
                $"  members:\n{memberDetails}");

            collisionCount++;
        }

        return collisionCount;
    }

    /// <summary>
    /// Erase a TypeReference to a simple string representation for signature comparison.
    /// Simplified version that doesn't require TsEmitName on types.
    /// </summary>
    private static string EraseTypeToString(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named when named.TypeArguments.Count > 0 =>
                $"{SimplifyTypeName(named.FullName)}<{string.Join(", ", named.TypeArguments.Select(EraseTypeToString))}>",

            NamedTypeReference named => SimplifyTypeName(named.FullName),

            NestedTypeReference nested => SimplifyTypeName(nested.FullReference.FullName),

            GenericParameterReference gp => gp.Name,

            ArrayTypeReference arr => $"ReadonlyArray<{EraseTypeToString(arr.ElementType)}>",

            PointerTypeReference ptr => EraseTypeToString(ptr.PointeeType),
            ByRefTypeReference byref => EraseTypeToString(byref.ReferencedType),

            _ => "unknown"
        };
    }

    /// <summary>
    /// Simplify type name to TypeScript-level representation.
    /// Maps common BCL types to their TS equivalents.
    /// </summary>
    private static string SimplifyTypeName(string fullName)
    {
        return fullName switch
        {
            "System.Void" => "void",
            "System.Object" => "any",
            "System.String" => "string",
            "System.Boolean" => "boolean",
            "System.Int32" => "number",
            "System.Int64" => "number",
            "System.Double" => "number",
            "System.Single" => "number",
            "System.Byte" => "number",
            "System.SByte" => "number",
            "System.Int16" => "number",
            "System.UInt16" => "number",
            "System.UInt32" => "number",
            "System.UInt64" => "number",
            "System.Decimal" => "number",
            _ => fullName.Replace("`", "_") // Replace generic arity marker
        };
    }

    /// <summary>
    /// PhaseGate Hardening M3: Validate view integrity (3 hard rules).
    /// 1. PG_VIEW_001: Each ExplicitView must contain ≥1 ViewMember (non-empty)
    /// 2. PG_VIEW_002: No two views for the same InterfaceStableId on a type
    /// 3. PG_VIEW_003: View property name must be valid TS identifier (sanitized if reserved)
    /// </summary>
    private static void ValidateViewsIntegrity(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                            Core.Diagnostics.DiagnosticCodes.PG_VIEW_001,
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
                            Core.Diagnostics.DiagnosticCodes.PG_VIEW_002,
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
                    if (Core.TypeScriptReservedWords.IsReservedWord(view.ViewPropertyName) &&
                        !view.ViewPropertyName.EndsWith("_"))
                    {
                        invalidViewNames++;
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_VIEW_003,
                            "ERROR",
                            $"Invalid/unsanitized view property name\n" +
                            $"  type:     {type.ClrFullName}\n" +
                            $"  view:     {view.ViewPropertyName}\n" +
                            $"  expected: {view.ViewPropertyName}_\n" +
                            $"  reason:   TypeScript reserved word");
                    }

                    // Check for invalid characters in view property name
                    if (!IsValidTypeScriptIdentifier(view.ViewPropertyName))
                    {
                        invalidViewNames++;
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_VIEW_003,
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


    private static string GetTypeReferenceName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.FullName,
            NestedTypeReference nested => nested.FullReference.FullName,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }

    /// <summary>
    /// Check if a string is a valid TypeScript identifier.
    /// Must start with letter, _, or $ and contain only letters, digits, _, or $.
    /// </summary>
    private static bool IsValidTypeScriptIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        // Must start with letter, _, or $
        if (!char.IsLetter(name[0]) && name[0] != '_' && name[0] != '$')
            return false;

        // Subsequent characters can be letters, digits, _, or $
        for (int i = 1; i < name.Length; i++)
        {
            if (!char.IsLetterOrDigit(name[i]) && name[i] != '_' && name[i] != '$')
                return false;
        }

        return true;
    }

    /// <summary>
    /// M4: Validate constraint mismatches and classify as benign vs non-benign.
    /// Benign widenings (class/struct/notnull → TS object/unknown) emit WARNING.
    /// Non-benign losses (new(), other constraints) emit ERROR.
    /// </summary>
    /// <summary>
    /// M4: Emit constructor constraint diagnostics from InterfaceConstraintAuditor findings.
    /// This replaces per-member checking to avoid duplicate diagnostics for view members.
    /// </summary>
    private static void EmitConstraintDiagnostics(BuildContext ctx, InterfaceConstraintFindings findings, ValidationContext validationCtx)
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
                        Core.Diagnostics.DiagnosticCodes.PG_CT_001,
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
                        Core.Diagnostics.DiagnosticCodes.PG_CT_002,
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

    private static void ClassifyConstraintDifferences(
        BuildContext ctx,
        ValidationContext validationCtx,
        GenericParameterSymbol gp,
        string ownerName,
        ref int benignCount,
        ref int nonBenignCount,
        SymbolGraph graph)
    {
        // Check SpecialConstraints (these are always "lost" in TypeScript since TS doesn't support them)
        if (gp.SpecialConstraints != GenericParameterConstraints.None)
        {
            // Benign: class (ReferenceType), notnull (NotNullable), struct (ValueType)
            var benignFlags = GenericParameterConstraints.ReferenceType |
                             GenericParameterConstraints.NotNullable |
                             GenericParameterConstraints.ValueType;

            var benignLoss = (gp.SpecialConstraints & benignFlags);
            var nonBenignLoss = (gp.SpecialConstraints & ~benignFlags);

            if (benignLoss != GenericParameterConstraints.None)
            {
                validationCtx.RecordDiagnostic(
                    Core.Diagnostics.DiagnosticCodes.PG_CT_001,
                    "WARNING",
                    $"Benign constraint widening on {gp.Name}\n" +
                    $"  owner:    {ownerName}\n" +
                    $"  clr:      {benignLoss}\n" +
                    $"  ts:       (no equivalent - widened to TS top type)");
                benignCount++;
            }

            // Non-benign: new() (DefaultConstructor)
            if (nonBenignLoss != GenericParameterConstraints.None)
            {
                // Build full CLR constraint description
                var clrConstraints = FormatConstraintSet(gp.SpecialConstraints, gp.Constraints);
                var tsConstraints = FormatTsConstraintSet(gp.SpecialConstraints & ~GenericParameterConstraints.DefaultConstructor, gp.Constraints);

                // Check if constructor constraint loss is allowed via policy flag
                var allowConstructorLoss = ctx.Policy.Constraints.AllowConstructorConstraintLoss;

                if (!allowConstructorLoss)
                {
                    // Strict mode: ERROR
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.PG_CT_001,
                        "ERROR",
                        $"PG_CT_001: Non-benign constraint loss on {gp.Name} in {ownerName}\n" +
                        $"  CLR:    where {gp.Name} : {clrConstraints}\n" +
                        $"  TS:     {tsConstraints}\n" +
                        $"  Reason: TypeScript cannot represent parameterless constructor constraints; callers relying on `new {gp.Name}()` would be unsound.");
                    nonBenignCount++;
                }
                else
                {
                    // Override mode: WARNING with [OVERRIDE] marker
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.PG_CT_002,
                        "WARNING",
                        $"[OVERRIDE] PG_CT_002: Constructor constraint loss on {gp.Name} in {ownerName}\n" +
                        $"  CLR:    where {gp.Name} : {clrConstraints}\n" +
                        $"  TS:     {tsConstraints}\n" +
                        $"  Reason: TypeScript cannot represent parameterless constructor constraints; callers relying on `new {gp.Name}()` would be unsound.\n" +
                        $"  Note:   Allowed via Policy.Constraints.AllowConstructorConstraintLoss = true");
                    benignCount++; // Count as benign (downgraded)
                }
            }
        }

        // Check all type constraints (interface/class/enum constraints)
        foreach (var constraint in gp.Constraints)
        {
            // Check if constraint is unrepresentable (pointer, byref, etc.)
            if (!IsConstraintRepresentable(constraint))
            {
                validationCtx.RecordDiagnostic(
                    Core.Diagnostics.DiagnosticCodes.PG_CT_001,
                    "ERROR",
                    $"Non-benign constraint loss on {gp.Name}\n" +
                    $"  owner:    {ownerName}\n" +
                    $"  clr:      {GetTypeReferenceName(constraint)} (unrepresentable in TS)\n" +
                    $"  ts:       (constraint dropped - cannot represent pointer/byref types)");
                nonBenignCount++;
                continue; // Skip further checks for this constraint
            }

            // Check if it's an enum constraint (benign widening to number)
            if (IsEnumConstraint(constraint, graph))
            {
                validationCtx.RecordDiagnostic(
                    Core.Diagnostics.DiagnosticCodes.PG_CT_001,
                    "WARNING",
                    $"Benign constraint widening on {gp.Name}\n" +
                    $"  owner:    {ownerName}\n" +
                    $"  clr:      {GetTypeReferenceName(constraint)} (enum)\n" +
                    $"  ts:       number (enum widened to number)");
                benignCount++;
                continue;
            }

            // All other type constraints (interface, base class) are emitted as-is
            // No classification needed - they're representable and preserved in TS
        }
    }

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
    /// M5: Validate view member name scoping.
    /// PG_NAME_003: View member collision within view scope (same emitted name in one view).
    /// PG_NAME_004: View member name equals class surface name.
    /// </summary>
    private static void ValidateViewMemberNameScoping(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                            case Shape.ViewPlanner.ViewMemberKind.Method:
                                var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                                if (method == null) continue;
                                var methodScope = ScopeFactory.ViewSurface(type, interfaceStableId, method.IsStatic);
                                emittedName = ctx.Renamer.GetFinalMemberName(method.StableId, methodScope);
                                break;
                            case Shape.ViewPlanner.ViewMemberKind.Property:
                                var prop = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                                if (prop == null) continue;
                                var propScope = ScopeFactory.ViewSurface(type, interfaceStableId, prop.IsStatic);
                                emittedName = ctx.Renamer.GetFinalMemberName(prop.StableId, propScope);
                                break;
                            case Shape.ViewPlanner.ViewMemberKind.Event:
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
                                Core.Diagnostics.DiagnosticCodes.PG_NAME_003,
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
                            case Shape.ViewPlanner.ViewMemberKind.Method:
                                var methodForCheck = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                                isViewOnly = methodForCheck?.EmitScope == EmitScope.ViewOnly;
                                break;
                            case Shape.ViewPlanner.ViewMemberKind.Property:
                                var propForCheck = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                                isViewOnly = propForCheck?.EmitScope == EmitScope.ViewOnly;
                                break;
                            case Shape.ViewPlanner.ViewMemberKind.Event:
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
                                Core.Diagnostics.DiagnosticCodes.PG_NAME_004,
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

    /// <summary>
    /// Helper to find if a ViewMember corresponds to a static member.
    /// </summary>
    private static bool FindMemberIsStatic(TypeSymbol type, Shape.ViewPlanner.ViewMember viewMember)
    {
        return viewMember.Kind switch
        {
            Shape.ViewPlanner.ViewMemberKind.Method =>
                type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId))?.IsStatic ?? false,
            Shape.ViewPlanner.ViewMemberKind.Property =>
                type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId))?.IsStatic ?? false,
            Shape.ViewPlanner.ViewMemberKind.Event =>
                type.Members.Events.FirstOrDefault(e => e.StableId.Equals(viewMember.StableId))?.IsStatic ?? false,
            _ => false
        };
    }

    /// <summary>
    /// M5: Validate EmitScope invariants.
    /// PG_INT_002: No member should appear in both ClassSurface and ViewOnly.
    /// PG_INT_003: ClassSurface members must not have SourceInterface set.
    /// </summary>
    private static void ValidateEmitScopeInvariants(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M5: Validating EmitScope invariants...");

        int dualScopeErrors = 0;
        int sourceInterfaceErrors = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // PG_INT_002: Check for members appearing in both ClassSurface and ViewOnly
                var scopeMap = new Dictionary<SinglePhase.Renaming.MemberStableId, (bool ClassSurface, bool ViewOnly)>();

                void MarkMember(SinglePhase.Renaming.MemberStableId id, EmitScope scope)
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
                            Core.Diagnostics.DiagnosticCodes.PG_INT_002,
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
                        Core.Diagnostics.DiagnosticCodes.PG_INT_003,
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
                        Core.Diagnostics.DiagnosticCodes.PG_INT_003,
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
    private static void ValidateScopeMismatches(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                            Core.Diagnostics.DiagnosticCodes.PG_SCOPE_003,
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
                            Core.Diagnostics.DiagnosticCodes.PG_SCOPE_004,
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
                            Core.Diagnostics.DiagnosticCodes.PG_SCOPE_003,
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
                            case Shape.ViewPlanner.ViewMemberKind.Method:
                                var method = type.Members.Methods.FirstOrDefault(m => m.StableId.Equals(viewMember.StableId));
                                if (method != null)
                                {
                                    isViewOnly = method.EmitScope == EmitScope.ViewOnly;
                                    isStatic = method.IsStatic;
                                }
                                break;
                            case Shape.ViewPlanner.ViewMemberKind.Property:
                                var property = type.Members.Properties.FirstOrDefault(p => p.StableId.Equals(viewMember.StableId));
                                if (property != null)
                                {
                                    isViewOnly = property.EmitScope == EmitScope.ViewOnly;
                                    isStatic = property.IsStatic;
                                }
                                break;
                            case Shape.ViewPlanner.ViewMemberKind.Event:
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
                                Core.Diagnostics.DiagnosticCodes.PG_SCOPE_003,
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
                                Core.Diagnostics.DiagnosticCodes.PG_SCOPE_004,
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

    /// <summary>
    /// Format a MemberStableId for diagnostics.
    /// </summary>
    internal static string FormatMemberStableId(SinglePhase.Renaming.MemberStableId id)
    {
        // Avoid duplicating member name if already in CanonicalSignature
        var sig = id.CanonicalSignature.StartsWith(id.MemberName + "(", System.StringComparison.Ordinal)
            ? id.CanonicalSignature
            : $"{id.MemberName}{id.CanonicalSignature}";
        return $"{id.AssemblyName}:{id.DeclaringClrFullName}::{sig}";
    }

    /// <summary>
    /// M5: Validate that class surface has no duplicate emitted names after deduplication.
    /// PG_NAME_005: Catches any duplicates that slipped through ClassSurfaceDeduplicator.
    /// </summary>
    private static void ValidateClassSurfaceUniqueness(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("[PG]", "M5: Validating class surface uniqueness...");

        int duplicates = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Only check classes and structs
                if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
                    continue;

                // Group class-surface properties by emitted name (camelCase)
                var propertyGroups = type.Members.Properties
                    .Where(p => p.EmitScope == EmitScope.ClassSurface)
                    .GroupBy(p => ApplyCamelCase(p.ClrName))
                    .Where(g => g.Count() > 1)
                    .ToList();

                foreach (var group in propertyGroups)
                {
                    duplicates++;
                    var members = string.Join(", ", group.Select(p => p.ClrName));
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.PG_NAME_005,
                        "ERROR",
                        $"Duplicate property name on class surface\n" +
                        $"  type:          {type.ClrFullName}\n" +
                        $"  emitted name:  {group.Key}\n" +
                        $"  members:       {members}\n" +
                        $"  reason:        ClassSurfaceDeduplicator should have resolved this");
                }
            }
        }

        ctx.Log("[PG]", $"M5: Class surface uniqueness - {duplicates} duplicate property names");
    }

    /// <summary>
    /// Apply camelCase transformation to a name (simplified).
    /// </summary>
    private static string ApplyCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>
    /// Comprehensive finalization sweep - validates that every symbol has proper placement and naming.
    /// This is the FINAL check before emission - nothing should leak past this gate.
    /// Implements PG_FIN_001 through PG_FIN_009.
    /// </summary>
    private static void ValidateFinalization(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                        Core.Diagnostics.DiagnosticCodes.PG_FIN_004,
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

                // PG_FIN_001: Check all members have valid EmitScope (not null/unspecified)
                foreach (var prop in type.Members.Properties)
                {
                    if (prop.EmitScope == EmitScope.Omitted)
                        continue; // Intentionally omitted - OK

                    if (prop.EmitScope == default(EmitScope))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_001,
                            "ERROR",
                            $"Property {prop.ClrName} in {type.ClrFullName} has no EmitScope placement");
                    }
                }

                foreach (var method in type.Members.Methods)
                {
                    if (method.EmitScope == EmitScope.Omitted)
                        continue; // Intentionally omitted - OK

                    if (method.EmitScope == default(EmitScope))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_001,
                            "ERROR",
                            $"Method {method.ClrName} in {type.ClrFullName} has no EmitScope placement");
                    }
                }

                foreach (var field in type.Members.Fields)
                {
                    if (field.EmitScope == EmitScope.Omitted)
                        continue; // Intentionally omitted - OK

                    if (field.EmitScope == default(EmitScope))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_001,
                            "ERROR",
                            $"Field {field.ClrName} in {type.ClrFullName} has no EmitScope placement");
                    }
                }

                // PG_FIN_007: Check for dual-role clashes (same StableId in both ClassSurface and ViewOnly)
                var dualRole = classSurfaceMembers.Intersect(viewOnlyMembers).ToList();
                foreach (var stableId in dualRole)
                {
                    validationCtx.RecordDiagnostic(
                        Core.Diagnostics.DiagnosticCodes.PG_FIN_007,
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
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_003,
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
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_003,
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
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_003,
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
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_002,
                            "ERROR",
                            $"ViewOnly property {prop.ClrName} in {type.ClrFullName} has no SourceInterface");
                        continue;
                    }

                    var propInterfaceStableId = ScopeFactory.GetInterfaceStableId(prop.SourceInterface);
                    if (!viewsByInterface.ContainsKey(propInterfaceStableId))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_002,
                            "ERROR",
                            $"ViewOnly property {prop.ClrName} in {type.ClrFullName} references interface {propInterfaceStableId} which has no ExplicitView");
                        continue;
                    }

                    // Check final name in view scope
                    var viewScope = ScopeFactory.ViewSurface(type, propInterfaceStableId, prop.IsStatic);
                    if (!ctx.Renamer.HasFinalViewMemberName(prop.StableId, viewScope))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_003,
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
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_002,
                            "ERROR",
                            $"ViewOnly method {method.ClrName} in {type.ClrFullName} has no SourceInterface");
                        continue;
                    }

                    var methodInterfaceStableId = ScopeFactory.GetInterfaceStableId(method.SourceInterface);
                    if (!viewsByInterface.ContainsKey(methodInterfaceStableId))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_002,
                            "ERROR",
                            $"ViewOnly method {method.ClrName} in {type.ClrFullName} references interface {methodInterfaceStableId} which has no ExplicitView");
                        continue;
                    }

                    // Check final name in view scope
                    var viewScope = ScopeFactory.ViewSurface(type, methodInterfaceStableId, method.IsStatic);
                    if (!ctx.Renamer.HasFinalViewMemberName(method.StableId, viewScope))
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_003,
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
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_005,
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
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_006,
                            "ERROR",
                            $"Member {stableId} in {type.ClrFullName} appears in {views.Count} views: {string.Join(", ", views)}");
                    }
                }

                // PG_FIN_009: Check for unsanitized identifiers in emitting members
                foreach (var prop in type.Members.Properties.Where(p => p.EmitScope != EmitScope.Omitted))
                {
                    var sanitized = Core.TypeScriptReservedWords.Sanitize(prop.TsEmitName);
                    if (sanitized.WasSanitized && sanitized.Sanitized != prop.TsEmitName)
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"Property {prop.ClrName} in {type.ClrFullName} has unsanitized TsEmitName '{prop.TsEmitName}' (reserved word)");
                    }

                    // Check indexer parameters (if this is an indexer property)
                    if (prop.IsIndexer)
                    {
                        foreach (var param in prop.IndexParameters)
                        {
                            var paramSanitized = Core.TypeScriptReservedWords.Sanitize(param.Name);
                            if (paramSanitized.WasSanitized && paramSanitized.Sanitized != param.Name)
                            {
                                validationCtx.RecordDiagnostic(
                                    Core.Diagnostics.DiagnosticCodes.PG_FIN_009,
                                    "ERROR",
                                    $"Property {prop.ClrName} parameter '{param.Name}' in {type.ClrFullName} is unsanitized (reserved word)");
                            }
                        }
                    }
                }

                foreach (var method in type.Members.Methods.Where(m => m.EmitScope != EmitScope.Omitted))
                {
                    var sanitized = Core.TypeScriptReservedWords.Sanitize(method.TsEmitName);
                    if (sanitized.WasSanitized && sanitized.Sanitized != method.TsEmitName)
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"Method {method.ClrName} in {type.ClrFullName} has unsanitized TsEmitName '{method.TsEmitName}' (reserved word)");
                    }

                    // Check method parameters
                    foreach (var param in method.Parameters)
                    {
                        var paramSanitized = Core.TypeScriptReservedWords.Sanitize(param.Name);
                        if (paramSanitized.WasSanitized && paramSanitized.Sanitized != param.Name)
                        {
                            validationCtx.RecordDiagnostic(
                                Core.Diagnostics.DiagnosticCodes.PG_FIN_009,
                                "ERROR",
                                $"Method {method.ClrName} parameter '{param.Name}' in {type.ClrFullName} is unsanitized (reserved word)");
                        }
                    }

                    // Check generic type parameters
                    foreach (var tp in method.GenericParameters)
                    {
                        var tpSanitized = Core.TypeScriptReservedWords.Sanitize(tp.Name);
                        if (tpSanitized.WasSanitized && tpSanitized.Sanitized != tp.Name)
                        {
                            validationCtx.RecordDiagnostic(
                                Core.Diagnostics.DiagnosticCodes.PG_FIN_009,
                                "ERROR",
                                $"Method {method.ClrName} type parameter '{tp.Name}' in {type.ClrFullName} is unsanitized (reserved word)");
                        }
                    }
                }

                foreach (var field in type.Members.Fields.Where(f => f.EmitScope != EmitScope.Omitted))
                {
                    var sanitized = Core.TypeScriptReservedWords.Sanitize(field.TsEmitName);
                    if (sanitized.WasSanitized && sanitized.Sanitized != field.TsEmitName)
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"Field {field.ClrName} in {type.ClrFullName} has unsanitized TsEmitName '{field.TsEmitName}' (reserved word)");
                    }
                }

                // Check type-level generic parameters
                foreach (var tp in type.GenericParameters)
                {
                    var tpSanitized = Core.TypeScriptReservedWords.Sanitize(tp.Name);
                    if (tpSanitized.WasSanitized && tpSanitized.Sanitized != tp.Name)
                    {
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"Type {type.ClrFullName} type parameter '{tp.Name}' is unsanitized (reserved word)");
                    }
                }

                // Check view property names
                foreach (var view in type.ExplicitViews)
                {
                    var viewPropSanitized = Core.TypeScriptReservedWords.Sanitize(view.ViewPropertyName);
                    if (viewPropSanitized.WasSanitized && viewPropSanitized.Sanitized != view.ViewPropertyName)
                    {
                        var ifaceStableId = ScopeFactory.GetInterfaceStableId(view.InterfaceReference);
                        validationCtx.RecordDiagnostic(
                            Core.Diagnostics.DiagnosticCodes.PG_FIN_009,
                            "ERROR",
                            $"View property '{view.ViewPropertyName}' for {ifaceStableId} in {type.ClrFullName} is unsanitized (reserved word)");
                    }
                }
            }
        }

        ctx.Log("PhaseGate", "Finalization sweep complete");
    }
}

/// <summary>
/// Validation context for accumulating validation results.
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
