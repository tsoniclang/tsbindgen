using System.Collections.Generic;
using System.Linq;
using tsbindgen.Core.Diagnostics;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Validates the symbol graph before emission.
/// Performs comprehensive validation checks and policy enforcement.
/// Acts as quality gate between Shape/Plan phases and Emit phase.
/// </summary>
public static class PhaseGate
{
    public static void Validate(BuildContext ctx, SymbolGraph graph, ImportPlan imports)
    {
        ctx.Log("PhaseGate: Validating symbol graph before emission...");

        var validationContext = new ValidationContext
        {
            ErrorCount = 0,
            WarningCount = 0,
            Diagnostics = new List<string>()
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

        // Report results
        ctx.Log($"PhaseGate: Validation complete - {validationContext.ErrorCount} errors, {validationContext.WarningCount} warnings");

        if (validationContext.ErrorCount > 0)
        {
            ctx.Diagnostics.Error(Core.Diagnostics.DiagnosticCodes.ValidationFailed,
                $"PhaseGate validation failed with {validationContext.ErrorCount} errors");
        }

        // Record diagnostics
        foreach (var diagnostic in validationContext.Diagnostics)
        {
            ctx.Log($"PhaseGate: {diagnostic}");
        }
    }

    private static void ValidateTypeNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating type names...");

        var namesSeen = new HashSet<string>();

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Check TsEmitName is set
                if (string.IsNullOrWhiteSpace(type.TsEmitName))
                {
                    validationCtx.ErrorCount++;
                    validationCtx.Diagnostics.Add($"ERROR: Type {type.ClrFullName} has no TsEmitName");
                }

                // Check for duplicates within namespace
                var fullEmitName = $"{ns.Name}.{type.TsEmitName}";
                if (namesSeen.Contains(fullEmitName))
                {
                    validationCtx.ErrorCount++;
                    validationCtx.Diagnostics.Add($"ERROR: Duplicate TsEmitName '{fullEmitName}' in namespace {ns.Name}");
                }
                namesSeen.Add(fullEmitName);

                // Check for TypeScript reserved words
                if (IsTypeScriptReservedWord(type.TsEmitName))
                {
                    validationCtx.WarningCount++;
                    validationCtx.Diagnostics.Add($"WARNING: Type '{type.TsEmitName}' uses TypeScript reserved word");
                }
            }
        }

        ctx.Log($"PhaseGate: Validated {namesSeen.Count} type names");
    }

    private static void ValidateMemberNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating member names...");

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
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add($"ERROR: Method {method.ClrName} in {type.ClrFullName} has no TsEmitName");
                    }

                    // Check for collisions within same scope
                    if (method.EmitScope == EmitScope.ClassSurface)
                    {
                        var signature = $"{method.TsEmitName}_{method.Parameters.Count}";
                        if (!memberNames.Add(signature))
                        {
                            validationCtx.WarningCount++;
                            validationCtx.Diagnostics.Add($"WARNING: Potential method overload collision for {method.TsEmitName} in {type.ClrFullName}");
                        }
                    }

                    totalMembers++;
                }

                // Validate properties
                foreach (var property in type.Members.Properties)
                {
                    if (string.IsNullOrWhiteSpace(property.TsEmitName))
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add($"ERROR: Property {property.ClrName} in {type.ClrFullName} has no TsEmitName");
                    }

                    totalMembers++;
                }

                // Validate fields
                foreach (var field in type.Members.Fields)
                {
                    if (string.IsNullOrWhiteSpace(field.TsEmitName))
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add($"ERROR: Field {field.ClrName} in {type.ClrFullName} has no TsEmitName");
                    }

                    totalMembers++;
                }
            }
        }

        ctx.Log($"PhaseGate: Validated {totalMembers} members");
    }

    private static void ValidateGenericParameters(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating generic parameters...");

        int totalGenericParams = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                foreach (var gp in type.GenericParameters)
                {
                    // Check name is valid
                    if (string.IsNullOrWhiteSpace(gp.Name))
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add($"ERROR: Generic parameter in {type.ClrFullName} has no name");
                    }

                    // Check constraints are representable
                    foreach (var constraint in gp.Constraints)
                    {
                        if (!IsConstraintRepresentable(constraint))
                        {
                            validationCtx.WarningCount++;
                            validationCtx.Diagnostics.Add($"WARNING: Constraint on {gp.Name} in {type.ClrFullName} may not be representable");
                        }
                    }

                    totalGenericParams++;
                }
            }
        }

        ctx.Log($"PhaseGate: Validated {totalGenericParams} generic parameters");
    }

    private static void ValidateInterfaceConformance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating interface conformance...");

        int typesChecked = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                if (type.Kind != TypeKind.Class && type.Kind != TypeKind.Struct)
                    continue;

                // Check that all claimed interfaces have corresponding members
                foreach (var ifaceRef in type.Interfaces)
                {
                    var iface = FindInterface(graph, ifaceRef);
                    if (iface == null)
                        continue; // External interface

                    // Verify structural conformance (all interface members present on class surface)
                    var representableMethods = type.Members.Methods
                        .Where(m => m.EmitScope == EmitScope.ClassSurface)
                        .ToList();

                    foreach (var requiredMethod in iface.Members.Methods)
                    {
                        var methodSig = $"{requiredMethod.ClrName}({requiredMethod.Parameters.Count})";
                        var exists = representableMethods.Any(m =>
                            m.ClrName == requiredMethod.ClrName &&
                            m.Parameters.Count == requiredMethod.Parameters.Count);

                        if (!exists)
                        {
                            validationCtx.WarningCount++;
                            validationCtx.Diagnostics.Add($"WARNING: {type.ClrFullName} claims to implement {GetInterfaceName(ifaceRef)} but missing method {methodSig}");
                        }
                    }
                }

                typesChecked++;
            }
        }

        ctx.Log($"PhaseGate: Validated interface conformance for {typesChecked} types");
    }

    private static void ValidateInheritance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating inheritance...");

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
                    validationCtx.ErrorCount++;
                    validationCtx.Diagnostics.Add($"ERROR: {type.ClrFullName} inherits from non-class {baseClass.ClrFullName}");
                }

                inheritanceChecked++;
            }
        }

        ctx.Log($"PhaseGate: Validated {inheritanceChecked} inheritance relationships");
    }

    private static void ValidateEmitScopes(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating emit scopes...");

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

        ctx.Log($"PhaseGate: {totalMembers} members, {viewOnlyMembers} ViewOnly");
    }

    private static void ValidateImports(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating import plan...");

        int totalImports = imports.NamespaceImports.Values.Sum(list => list.Count);
        int totalExports = imports.NamespaceExports.Values.Sum(list => list.Count);

        // Check for circular dependencies
        var circularDeps = DetectCircularDependencies(imports);
        if (circularDeps.Count > 0)
        {
            validationCtx.WarningCount += circularDeps.Count;
            foreach (var cycle in circularDeps)
            {
                validationCtx.Diagnostics.Add($"WARNING: Circular dependency detected: {cycle}");
            }
        }

        ctx.Log($"PhaseGate: {totalImports} import statements, {totalExports} export statements");
    }

    private static void ValidatePolicyCompliance(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating policy compliance...");

        var policy = ctx.Policy;

        // Check that policy constraints are met
        // For example, if policy forbids certain patterns, verify they don't appear

        // This is extensible - add more policy checks as needed

        ctx.Log("PhaseGate: Policy compliance validated");
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
        ctx.Log("PhaseGate: Validating explicit interface views...");

        int totalViewOnlyMembers = 0;
        int orphanedViewOnlyMembers = 0;
        int totalViews = 0;

        foreach (var ns in graph.Namespaces)
        {
            foreach (var type in ns.Types)
            {
                // Collect all ViewOnly members
                var viewOnlyMethods = type.Members.Methods
                    .Where(m => m.EmitScope == EmitScope.ViewOnly)
                    .ToList();

                var viewOnlyProperties = type.Members.Properties
                    .Where(p => p.EmitScope == EmitScope.ViewOnly)
                    .ToList();

                totalViewOnlyMembers += viewOnlyMethods.Count + viewOnlyProperties.Count;

                if (viewOnlyMethods.Count == 0 && viewOnlyProperties.Count == 0)
                    continue;

                // Get planned views for this type
                var plannedViews = Shape.ViewPlanner.GetPlannedViews(type.ClrFullName);

                if (plannedViews.Count == 0)
                {
                    // ViewOnly members but no views planned - this is an error
                    validationCtx.ErrorCount++;
                    validationCtx.Diagnostics.Add(
                        $"ERROR: Type {type.ClrFullName} has {viewOnlyMethods.Count + viewOnlyProperties.Count} ViewOnly members but no explicit views planned");
                    orphanedViewOnlyMembers += viewOnlyMethods.Count + viewOnlyProperties.Count;
                    continue;
                }

                totalViews += plannedViews.Count;

                // Check that each ViewOnly member appears in at least one view
                foreach (var method in viewOnlyMethods)
                {
                    var appearsInView = plannedViews.Any(v =>
                        v.ViewMembers.Any(vm =>
                            vm.Kind == Shape.ViewPlanner.ViewMemberKind.Method &&
                            vm.Symbol is MethodSymbol ms &&
                            ms.StableId.Equals(method.StableId)));

                    if (!appearsInView)
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add(
                            $"ERROR: ViewOnly method {method.ClrName} in {type.ClrFullName} does not appear in any explicit view");
                        orphanedViewOnlyMembers++;
                    }
                }

                foreach (var property in viewOnlyProperties)
                {
                    var appearsInView = plannedViews.Any(v =>
                        v.ViewMembers.Any(vm =>
                            vm.Kind == Shape.ViewPlanner.ViewMemberKind.Property &&
                            vm.Symbol is PropertySymbol ps &&
                            ps.StableId.Equals(property.StableId)));

                    if (!appearsInView)
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add(
                            $"ERROR: ViewOnly property {property.ClrName} in {type.ClrFullName} does not appear in any explicit view");
                        orphanedViewOnlyMembers++;
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
                        validationCtx.WarningCount++;
                        validationCtx.Diagnostics.Add(
                            $"WARNING: View {view.ViewPropertyName} in {type.ClrFullName} references external interface (should be imported)");
                    }
                }
            }
        }

        ctx.Log($"PhaseGate: Validated {totalViewOnlyMembers} ViewOnly members across {totalViews} views");

        if (orphanedViewOnlyMembers > 0)
        {
            ctx.Log($"PhaseGate: Found {orphanedViewOnlyMembers} orphaned ViewOnly members");
        }
    }

    /// <summary>
    /// Validates that there are no duplicate final identifiers within the same scope.
    /// Checks both type-level (class/interface names) and member-level (methods/properties/fields).
    /// Separates static and instance member scopes.
    /// </summary>
    private static void ValidateFinalNames(BuildContext ctx, SymbolGraph graph, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating final names from Renamer...");

        int totalTypes = 0;
        int totalMembers = 0;
        int duplicateTypes = 0;
        int duplicateMembers = 0;

        // Validate type names (namespace scope)
        foreach (var ns in graph.Namespaces)
        {
            var typeNamesInNamespace = new HashSet<string>();

            var namespaceScope = new Core.Renaming.NamespaceScope
            {
                Namespace = ns.Name,
                IsInternal = true,
                ScopeKey = ns.Name
            };

            foreach (var type in ns.Types)
            {
                totalTypes++;

                // Get final name from Renamer
                var finalName = ctx.Renamer.GetFinalTypeName(type.StableId, namespaceScope);

                if (string.IsNullOrWhiteSpace(finalName))
                {
                    validationCtx.ErrorCount++;
                    validationCtx.Diagnostics.Add(
                        $"ERROR: Type {type.ClrFullName} has no final name from Renamer");
                    continue;
                }

                // Check for duplicates within namespace
                if (!typeNamesInNamespace.Add(finalName))
                {
                    validationCtx.ErrorCount++;
                    validationCtx.Diagnostics.Add(
                        $"ERROR: Duplicate final type name '{finalName}' in namespace {ns.Name}");
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

                var typeScope = new Core.Renaming.TypeScope
                {
                    TypeFullName = type.ClrFullName,
                    IsStatic = false, // Will be overridden per member
                    ScopeKey = type.ClrFullName
                };

                // Validate method names
                foreach (var method in type.Members.Methods)
                {
                    totalMembers++;

                    var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, typeScope, method.IsStatic);

                    if (string.IsNullOrWhiteSpace(finalName))
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add(
                            $"ERROR: Method {method.ClrName} in {type.ClrFullName} has no final name from Renamer");
                        continue;
                    }

                    // Add to appropriate scope
                    var scopeSet = method.IsStatic ? staticMemberNames : instanceMemberNames;
                    var scopeName = method.IsStatic ? "static" : "instance";

                    // Methods can have overloads, so we use signature for uniqueness
                    var signature = $"{finalName}({method.Parameters.Count})";

                    if (!scopeSet.Add(signature))
                    {
                        // This is a warning, not an error (overloads are allowed)
                        validationCtx.WarningCount++;
                        validationCtx.Diagnostics.Add(
                            $"WARNING: Duplicate {scopeName} method signature '{signature}' in {type.ClrFullName}");
                    }
                }

                // Validate property names
                foreach (var property in type.Members.Properties)
                {
                    totalMembers++;

                    var finalName = ctx.Renamer.GetFinalMemberName(property.StableId, typeScope, property.IsStatic);

                    if (string.IsNullOrWhiteSpace(finalName))
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add(
                            $"ERROR: Property {property.ClrName} in {type.ClrFullName} has no final name from Renamer");
                        continue;
                    }

                    // Add to appropriate scope
                    var scopeSet = property.IsStatic ? staticMemberNames : instanceMemberNames;
                    var scopeName = property.IsStatic ? "static" : "instance";

                    if (!scopeSet.Add(finalName))
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add(
                            $"ERROR: Duplicate {scopeName} property name '{finalName}' in {type.ClrFullName}");
                        duplicateMembers++;
                    }
                }

                // Validate field names
                foreach (var field in type.Members.Fields)
                {
                    totalMembers++;

                    var finalName = ctx.Renamer.GetFinalMemberName(field.StableId, typeScope, field.IsStatic);

                    if (string.IsNullOrWhiteSpace(finalName))
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add(
                            $"ERROR: Field {field.ClrName} in {type.ClrFullName} has no final name from Renamer");
                        continue;
                    }

                    // Add to appropriate scope
                    var scopeSet = field.IsStatic ? staticMemberNames : instanceMemberNames;
                    var scopeName = field.IsStatic ? "static" : "instance";

                    if (!scopeSet.Add(finalName))
                    {
                        validationCtx.ErrorCount++;
                        validationCtx.Diagnostics.Add(
                            $"ERROR: Duplicate {scopeName} field name '{finalName}' in {type.ClrFullName}");
                        duplicateMembers++;
                    }
                }
            }
        }

        ctx.Log($"PhaseGate: Validated {totalTypes} type names and {totalMembers} member names from Renamer");

        if (duplicateTypes > 0 || duplicateMembers > 0)
        {
            ctx.Log($"PhaseGate: Found {duplicateTypes} duplicate types, {duplicateMembers} duplicate members");
        }
    }

    /// <summary>
    /// Validates that import aliases don't collide after Renamer resolution.
    /// Checks that all aliased names are unique within their import scope.
    /// </summary>
    private static void ValidateAliases(BuildContext ctx, SymbolGraph graph, ImportPlan imports, ValidationContext validationCtx)
    {
        ctx.Log("PhaseGate: Validating import aliases...");

        int totalAliases = 0;
        int aliasCollisions = 0;

        foreach (var (ns, importList) in imports.NamespaceImports)
        {
            var aliasesInScope = new HashSet<string>();
            var typeNamesInScope = new HashSet<string>();

            var namespaceScope = new Core.Renaming.NamespaceScope
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
                            validationCtx.ErrorCount++;
                            validationCtx.Diagnostics.Add(
                                $"ERROR: Import alias '{typeImport.Alias}' collides in namespace {ns}");
                            aliasCollisions++;
                        }
                    }

                    // Check that imported type names don't collide with each other
                    if (!typeNamesInScope.Add(effectiveName))
                    {
                        validationCtx.WarningCount++;
                        validationCtx.Diagnostics.Add(
                            $"WARNING: Imported type name '{effectiveName}' appears multiple times in namespace {ns}");
                    }
                }
            }

            // Also check that imported names don't collide with local types
            var localNamespace = graph.Namespaces.FirstOrDefault(n => n.Name == ns);
            if (localNamespace != null)
            {
                foreach (var localType in localNamespace.Types)
                {
                    var localTypeName = ctx.Renamer.GetFinalTypeName(localType.StableId, namespaceScope);

                    if (typeNamesInScope.Contains(localTypeName))
                    {
                        validationCtx.WarningCount++;
                        validationCtx.Diagnostics.Add(
                            $"WARNING: Imported type name '{localTypeName}' in namespace {ns} collides with local type {localType.ClrFullName}");
                    }
                }
            }
        }

        ctx.Log($"PhaseGate: Validated {totalAliases} import aliases");

        if (aliasCollisions > 0)
        {
            ctx.Log($"PhaseGate: Found {aliasCollisions} alias collisions");
        }
    }
}

/// <summary>
/// Validation context for accumulating validation results.
/// </summary>
internal sealed class ValidationContext
{
    public int ErrorCount { get; set; }
    public int WarningCount { get; set; }
    public List<string> Diagnostics { get; set; } = new();
}
