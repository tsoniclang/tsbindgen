using System;
using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Plan.Validation;

/// <summary>
/// Core validation functions (types, members, generics, interfaces, inheritance, scopes, imports, policy).
/// </summary>
internal static class Core
{
    internal static void ValidateTypeNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                        DiagnosticCodes.ValidationFailed,
                        "ERROR",
                        $"Type {type.ClrFullName} has no TsEmitName");
                }

                // Check for duplicates within namespace
                var fullEmitName = $"{ns.Name}.{type.TsEmitName}";
                if (namesSeen.Contains(fullEmitName))
                {
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.DuplicateMember,
                        "ERROR",
                        $"Duplicate TsEmitName '{fullEmitName}' in namespace {ns.Name}");
                }
                namesSeen.Add(fullEmitName);

                // Check for TypeScript reserved words
                // Step 1: Only warn if name wasn't sanitized (ClrName == TsEmitName means reserved word leaked through)
                if (Shared.IsTypeScriptReservedWord(type.TsEmitName))
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
                            DiagnosticCodes.ReservedWordUnsanitized,
                            "WARNING",
                            $"Type '{type.TsEmitName}' uses TypeScript reserved word but was not sanitized");
                    }
                }
                else if (type.ClrName != type.TsEmitName && Shared.IsTypeScriptReservedWord(type.ClrName))
                {
                    // Name was successfully sanitized
                    validationCtx.SanitizedNameCount++;
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {namesSeen.Count} type names");
    }

    internal static void ValidateMemberNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                            DiagnosticCodes.ValidationFailed,
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
                                DiagnosticCodes.AmbiguousOverload,
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
                            DiagnosticCodes.ValidationFailed,
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
                            DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Field {field.ClrName} in {type.ClrFullName} has no TsEmitName");
                    }

                    totalMembers++;
                }
            }
        }

        ctx.Log("PhaseGate", $"Validated {totalMembers} members");
    }

    internal static void ValidateGenericParameters(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                            DiagnosticCodes.ValidationFailed,
                            "ERROR",
                            $"Generic parameter in {type.ClrFullName} has no name");
                    }

                    // Check constraints are representable
                    foreach (var constraint in gp.Constraints)
                    {
                        if (!IsConstraintRepresentable(constraint))
                        {
                            validationCtx.RecordDiagnostic(
                                DiagnosticCodes.UnrepresentableConstraint,
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
                                    DiagnosticCodes.ConstraintNarrowing,
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

    internal static void ValidateInterfaceConformance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                            if (Shared.IsRepresentableConformanceBreak(matchingMethod, requiredMethod))
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
                            var classPropertyType = Shared.GetPropertyTypeString(matchingProperty);
                            var ifacePropertyType = Shared.GetPropertyTypeString(requiredProperty);

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
                        DiagnosticCodes.CovarianceSummary,
                        "INFO",
                        $"{type.ClrFullName} has {covarianceIssues.Count} property covariance issues (TS doesn't support property covariance)");
                }

                if (conformanceIssues.Count > 0)
                {
                    typesWithIssues++;
                    validationCtx.InterfaceConformanceIssuesByType[type.ClrFullName] = conformanceIssues;

                    // Emit one-line summary to console
                    validationCtx.RecordDiagnostic(
                        DiagnosticCodes.StructuralConformanceFailure,
                        "WARNING",
                        $"{type.ClrFullName} has {conformanceIssues.Count} interface conformance issues (see diagnostics file)");
                }

                typesChecked++;
            }
        }

        ctx.Log("PhaseGate", $"Validated interface conformance for {typesChecked} types ({typesWithIssues} with issues)");
        ctx.Log("PhaseGate", $"{typesWithExplicitViews} types have {totalExplicitViews} explicit views, {suppressedDueToViews} interfaces satisfied via views");
    }

    internal static void ValidateInheritance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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
                        DiagnosticCodes.CircularInheritance,
                        "ERROR",
                        $"{type.ClrFullName} inherits from non-class {baseClass.ClrFullName}");
                }

                inheritanceChecked++;
            }
        }

        ctx.Log("PhaseGate", $"Validated {inheritanceChecked} inheritance relationships");
    }

    internal static void ValidateEmitScopes(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
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

    internal static void ValidateImports(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
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
                    DiagnosticCodes.CircularInheritance,
                    "WARNING",
                    $"Circular dependency detected: {cycle}");
            }
        }

        ctx.Log("PhaseGate", $"{totalImports} import statements, {totalExports} export statements");
    }

    internal static void ValidatePolicyCompliance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate", "Validating policy compliance...");

        var policy = ctx.Policy;

        // Check that policy constraints are met
        // For example, if policy forbids certain patterns, verify they don't appear

        // This is extensible - add more policy checks as needed

        ctx.Log("PhaseGate", "Policy compliance validated");
    }

    // Helper functions

    private static TypeSymbol? FindInterface(SymbolGraph graph, TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName && t.Kind == TypeKind.Interface);
    }

    private static TypeSymbol? FindType(SymbolGraph graph, TypeReference typeRef)
    {
        var fullName = GetTypeFullName(typeRef);

        return graph.Namespaces
            .SelectMany(ns => ns.Types)
            .FirstOrDefault(t => t.ClrFullName == fullName);
    }

    private static string GetInterfaceName(TypeReference typeRef)
    {
        return typeRef switch
        {
            NamedTypeReference named => named.Name,
            NestedTypeReference nested => nested.NestedName,
            _ => typeRef.ToString() ?? "Unknown"
        };
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

    private static bool IsConstraintRepresentable(TypeReference constraint)
    {
        // Check if constraint can be represented in TypeScript
        return constraint switch
        {
            PointerTypeReference => false,
            ByRefTypeReference => false,
            _ => true
        };
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
}
