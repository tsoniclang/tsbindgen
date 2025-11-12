using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Renaming;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Plans import statements and aliasing for TypeScript declarations.
/// Generates import/export statements based on dependency graph.
/// Handles namespace-to-module mapping and name collision resolution.
/// </summary>
public static class ImportPlanner
{
    public static ImportPlan PlanImports(BuildContext ctx, SymbolGraph graph, ImportGraphData importGraph)
    {
        ctx.Log("ImportPlanner", "Planning import statements...");

        var plan = new ImportPlan
        {
            NamespaceImports = new Dictionary<string, List<ImportStatement>>(),
            NamespaceExports = new Dictionary<string, List<ExportStatement>>(),
            ImportAliases = new Dictionary<string, Dictionary<string, string>>()
        };

        // Plan imports for each namespace
        foreach (var ns in graph.Namespaces)
        {
            PlanNamespaceImports(ctx, ns, graph, importGraph, plan);
            PlanNamespaceExports(ctx, ns, plan);
        }

        ctx.Log("ImportPlanner", $"Planned imports for {plan.NamespaceImports.Count} namespaces");

        return plan;
    }

    private static void PlanNamespaceImports(
        BuildContext ctx,
        NamespaceSymbol ns,
        SymbolGraph graph,
        ImportGraphData importGraph,
        ImportPlan plan)
    {
        if (!importGraph.NamespaceDependencies.TryGetValue(ns.Name, out var dependencies))
        {
            // No dependencies, no imports needed
            return;
        }

        var imports = new List<ImportStatement>();
        var aliases = new Dictionary<string, string>();

        foreach (var targetNamespace in dependencies.OrderBy(d => d))
        {
            // Get all types referenced from target namespace (CLR names)
            var referencedTypeClrNames = importGraph.CrossNamespaceReferences
                .Where(r => r.SourceNamespace == ns.Name && r.TargetNamespace == targetNamespace)
                .Select(r => r.TargetType)
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            if (referencedTypeClrNames.Count == 0)
                continue;

            // Determine import path using PathPlanner
            var importPath = PathPlanner.GetSpecifier(ns.Name, targetNamespace);

            // Check for name collisions and create aliases if needed
            var typeImports = new List<TypeImport>();

            foreach (var clrName in referencedTypeClrNames)
            {
                // PRE-EMIT GUARD: Catch assembly-qualified garbage in CLR names
                // This prevents the import garbage bug from ever reaching import planning
                if (clrName.Contains('[') || clrName.Contains("Culture=") || clrName.Contains("PublicKeyToken="))
                {
                    ctx.Diagnostics.Error(
                        Core.Diagnostics.DiagnosticCodes.InvalidImportModulePath,
                        $"PRE-EMIT GUARD: CrossNamespaceReference contains assembly-qualified CLR name: '{clrName}' " +
                        $"(namespace {ns.Name} importing from {targetNamespace}). " +
                        $"This indicates CollectTypeReferences() failed to use GetOpenGenericClrKey().");
                    continue; // Skip this type reference
                }

                string tsName;

                // Try to look up TypeSymbol in local graph to get TypeScript emit name
                if (graph.TryGetType(clrName, out var typeSymbol) && typeSymbol != null)
                {
                    // Type is in local graph - use Renamer's final name
                    tsName = ctx.Renamer.GetFinalTypeName(typeSymbol);
                }
                else
                {
                    // Type is external (from another namespace) - construct TS name from CLR name
                    // CRITICAL: This handles cross-namespace generic types like IEnumerable_1, Func_2, etc.
                    // Apply same logic as TypeNameResolver for external types
                    tsName = GetTypeScriptNameForExternalType(clrName);
                    ctx.Log("ImportPlanner", $"External type {clrName} → {tsName}");
                }

                // PRE-EMIT GUARD: Detect assembly-qualified garbage before it reaches output
                // Prevents regressions of the import garbage bug (fixed in commit 70d21db)
                if (tsName.Contains('[') || tsName.Contains("Culture=") || tsName.Contains("PublicKeyToken="))
                {
                    ctx.Diagnostics.Error(
                        Core.Diagnostics.DiagnosticCodes.InvalidImportModulePath,
                        $"PRE-EMIT GUARD: Import statement would contain assembly-qualified garbage: '{tsName}' " +
                        $"(from CLR name: '{clrName}' in namespace {ns.Name} importing from {targetNamespace}). " +
                        $"This must be fixed before emission.");
                    continue; // Skip this import to prevent emission
                }

                var alias = DetermineAlias(ctx, ns.Name, targetNamespace, tsName, aliases);

                // TS2693 FIX: Determine if this type needs a value import (not just type import)
                // Base classes and interfaces used in extends/implements need to be imported as values
                var isValueImport = IsTypeUsedAsValue(importGraph, ns.Name, targetNamespace, clrName);

                typeImports.Add(new TypeImport(
                    TypeName: tsName,
                    Alias: alias,
                    IsValueImport: isValueImport));

                if (alias != null)
                {
                    aliases[tsName] = alias;
                }
            }

            if (typeImports.Count == 0)
                continue;

            // Generate namespace alias for this import module
            // Format: "System" → "System_Internal", "System.Collections.Generic" → "System_Collections_Generic_Internal"
            var namespaceAlias = GenerateNamespaceAlias(targetNamespace);

            var importStatement = new ImportStatement(
                ImportPath: importPath,
                TargetNamespace: targetNamespace,
                TypeImports: typeImports,
                NamespaceAlias: namespaceAlias);

            imports.Add(importStatement);

            // TS2693 FIX: Build qualified name mapping for value imports
            // This allows printers to qualify type names with namespace alias
            foreach (var ti in typeImports.Where(t => t.IsValueImport))
            {
                // Get the CLR name for this type (need to map back from TS name)
                var clrName = referencedTypeClrNames.FirstOrDefault(c =>
                {
                    var tsNameForClr = graph.TryGetType(c, out var ts) && ts != null
                        ? ctx.Renamer.GetFinalTypeName(ts)
                        : GetTypeScriptNameForExternalType(c);
                    return tsNameForClr == ti.TypeName;
                });

                if (!string.IsNullOrEmpty(clrName))
                {
                    // TS2339 FIX: Use instance class name for types with views
                    // Types with views emit as: Exception$instance + type alias Exception
                    // Heritage clauses need the INSTANCE CLASS (value), not type alias
                    string emittedName = ti.TypeName;

                    // Check if this type has views by looking it up in the graph
                    if (graph.TryGetType(clrName, out var targetType) && targetType != null)
                    {
                        // If type has explicit views, use $instance suffix
                        if (targetType.ExplicitViews.Length > 0 &&
                            (targetType.Kind == Model.Symbols.TypeKind.Class || targetType.Kind == Model.Symbols.TypeKind.Struct))
                        {
                            emittedName = $"{ti.TypeName}$instance";
                        }
                    }

                    // TS2339 FIX: Qualified name must include the target namespace
                    // because types are inside "export namespace X {}" blocks
                    // Format: NamespaceAlias.TargetNamespace.InstanceClassName
                    // Example: "System_Internal.System.Exception$instance"
                    var qualifiedName = $"{namespaceAlias}.{targetNamespace}.{emittedName}";
                    plan.ValueImportQualifiedNames[(ns.Name, clrName)] = qualifiedName;
                }
            }

            ctx.Log("ImportPlanner", $"{ns.Name} imports {typeImports.Count} types from {targetNamespace}");
        }

        if (imports.Count > 0)
        {
            plan.NamespaceImports[ns.Name] = imports;
            plan.ImportAliases[ns.Name] = aliases;
        }
    }

    private static void PlanNamespaceExports(
        BuildContext ctx,
        NamespaceSymbol ns,
        ImportPlan plan)
    {
        var exports = new List<ExportStatement>();

        // Create namespace scope for name resolution
        // Export all public types in the namespace
        foreach (var type in ns.Types)
        {
            if (type.Accessibility == Model.Symbols.Accessibility.Public)
            {
                var finalName = ctx.Renamer.GetFinalTypeName(type);
                exports.Add(new ExportStatement(
                    ExportName: finalName,
                    ExportKind: DetermineExportKind(type),
                    Arity: type.Arity)); // TS2314 FIX: Capture generic arity
            }
        }

        if (exports.Count > 0)
        {
            plan.NamespaceExports[ns.Name] = exports;
            ctx.Log("ImportPlanner", $"{ns.Name} exports {exports.Count} types");
        }
    }

    private static string? DetermineAlias(
        BuildContext ctx,
        string sourceNamespace,
        string targetNamespace,
        string typeName,
        Dictionary<string, string> existingAliases)
    {
        // Check if alias is needed (name collision)
        if (existingAliases.ContainsKey(typeName))
        {
            // Name collision - need alias
            var targetNsShort = GetNamespaceShortName(targetNamespace);
            return $"{typeName}_{targetNsShort}";
        }

        // Check policy - always alias imports?
        var policy = ctx.Policy.Modules;
        if (policy.AlwaysAliasImports)
        {
            var targetNsShort = GetNamespaceShortName(targetNamespace);
            return $"{typeName}_{targetNsShort}";
        }

        // No alias needed
        return null;
    }

    private static string GetNamespaceShortName(string namespaceName)
    {
        // Get short name for namespace aliasing
        // "System.Collections.Generic" -> "Generic"
        var lastDot = namespaceName.LastIndexOf('.');
        return lastDot >= 0 ? namespaceName.Substring(lastDot + 1) : namespaceName;
    }

    /// <summary>
    /// TS2693 FIX: Generate a valid TypeScript identifier for namespace imports.
    /// Converts namespace to a safe identifier by replacing dots with underscores.
    /// Examples:
    ///   "System" → "System_Internal"
    ///   "System.Collections.Generic" → "System_Collections_Generic_Internal"
    ///   "Microsoft.Win32" → "Microsoft_Win32_Internal"
    /// </summary>
    private static string GenerateNamespaceAlias(string namespaceName)
    {
        // Replace dots with underscores to make valid TS identifier
        var safeName = namespaceName.Replace('.', '_');

        // Append _Internal suffix to avoid collisions with type names
        return $"{safeName}_Internal";
    }

    private static ExportKind DetermineExportKind(Model.Symbols.TypeSymbol type)
    {
        return type.Kind switch
        {
            Model.Symbols.TypeKind.Class => ExportKind.Class,
            Model.Symbols.TypeKind.Interface => ExportKind.Interface,
            Model.Symbols.TypeKind.Struct => ExportKind.Interface, // Structs emit as interfaces in TS
            Model.Symbols.TypeKind.Enum => ExportKind.Enum,
            Model.Symbols.TypeKind.Delegate => ExportKind.Type, // Delegates emit as type aliases
            _ => ExportKind.Type
        };
    }

    /// <summary>
    /// TS2693 FIX: Determines if a type is used as a value (not just a type).
    /// Types used in extends/implements clauses need value imports (not 'import type').
    /// Returns true if the type is referenced as BaseClass or Interface.
    ///
    /// NOTE: Generic constraints are TYPE-ONLY positions (type sites), not value sites.
    /// They get qualified names through TypeNameResolver, but use 'import type' (not namespace imports).
    /// </summary>
    private static bool IsTypeUsedAsValue(
        ImportGraphData importGraph,
        string sourceNamespace,
        string targetNamespace,
        string targetTypeClrName)
    {
        // Check if any cross-namespace reference for this type is BaseClass or Interface
        // Constraints are explicitly NOT included - they are type-only positions
        return importGraph.CrossNamespaceReferences.Any(r =>
            r.SourceNamespace == sourceNamespace &&
            r.TargetNamespace == targetNamespace &&
            r.TargetType == targetTypeClrName &&
            (r.ReferenceKind == ReferenceKind.BaseClass ||
             r.ReferenceKind == ReferenceKind.Interface));
    }

    /// <summary>
    /// Get TypeScript name for an external type (not in current graph).
    /// Mirrors TypeNameResolver logic for external types.
    /// CRITICAL: Handles generic arity and reserved words.
    /// </summary>
    private static string GetTypeScriptNameForExternalType(string clrFullName)
    {
        // Extract simple name from full CLR name
        // Example: "System.Collections.Generic.IEnumerable`1" → "IEnumerable`1"
        var simpleName = clrFullName.Contains('.')
            ? clrFullName.Substring(clrFullName.LastIndexOf('.') + 1)
            : clrFullName;

        // Sanitize: backtick to underscore (IEnumerable`1 → IEnumerable_1)
        var sanitized = simpleName.Replace('`', '_');

        // Handle nested types
        sanitized = sanitized.Replace('+', '$');

        // CRITICAL: Check if sanitized name is a TypeScript reserved word
        // Example: "Type" → "Type_", "Object" → "Object_"
        var result = TypeScriptReservedWords.Sanitize(sanitized);
        return result.Sanitized;
    }
}

/// <summary>
/// Import plan containing all import/export statements for the symbol graph.
/// </summary>
public sealed class ImportPlan
{
    /// <summary>
    /// Maps namespace name to list of import statements for that namespace.
    /// </summary>
    public Dictionary<string, List<ImportStatement>> NamespaceImports { get; init; } = new();

    /// <summary>
    /// Maps namespace name to list of export statements for that namespace.
    /// </summary>
    public Dictionary<string, List<ExportStatement>> NamespaceExports { get; init; } = new();

    /// <summary>
    /// Maps namespace name to dictionary of type aliases (original name -> alias).
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> ImportAliases { get; init; } = new();

    /// <summary>
    /// TS2693 FIX: Maps (source namespace, target type CLR name) → qualified TypeScript name.
    /// Used for value-imported types that must be qualified with namespace alias.
    /// Example: ("Microsoft.CSharp.RuntimeBinder", "System.Exception") → "System_Internal.Exception"
    /// </summary>
    public Dictionary<(string SourceNamespace, string TargetTypeCLRName), string> ValueImportQualifiedNames { get; init; } = new();

    /// <summary>
    /// Gets import statements for a specific namespace.
    /// Returns empty list if namespace has no imports.
    /// </summary>
    public IReadOnlyList<ImportStatement> GetImportsFor(string namespaceName)
    {
        return NamespaceImports.TryGetValue(namespaceName, out var imports)
            ? imports
            : new List<ImportStatement>();
    }
}

/// <summary>
/// Represents a TypeScript import statement.
/// </summary>
public sealed record ImportStatement(
    string ImportPath,
    string TargetNamespace,
    List<TypeImport> TypeImports,
    string NamespaceAlias); // Alias for namespace imports (e.g., "System_Internal")

/// <summary>
/// Represents a single type import within an import statement.
/// </summary>
public sealed record TypeImport(
    string TypeName,
    string? Alias,
    bool IsValueImport); // True for base classes/interfaces (needs 'import'), false for type-only (can use 'import type')

/// <summary>
/// Represents a TypeScript export statement.
/// </summary>
public sealed record ExportStatement(
    string ExportName,
    ExportKind ExportKind,
    int Arity); // Number of generic type parameters (0 for non-generic types)

/// <summary>
/// Kind of export.
/// </summary>
public enum ExportKind
{
    Class,
    Interface,
    Enum,
    Type, // Type alias
    Const // Const value
}
