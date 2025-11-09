using System.Collections.Generic;
using System.Linq;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;

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
            PlanNamespaceImports(ctx, ns, importGraph, plan);
            PlanNamespaceExports(ctx, ns, plan);
        }

        ctx.Log("ImportPlanner", $"Planned imports for {plan.NamespaceImports.Count} namespaces");

        return plan;
    }

    private static void PlanNamespaceImports(
        BuildContext ctx,
        NamespaceSymbol ns,
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
            // Get all types referenced from target namespace
            var referencedTypes = importGraph.CrossNamespaceReferences
                .Where(r => r.SourceNamespace == ns.Name && r.TargetNamespace == targetNamespace)
                .Select(r => r.TargetType)
                .Distinct()
                .OrderBy(t => t)
                .ToList();

            if (referencedTypes.Count == 0)
                continue;

            // Determine import path
            var importPath = NamespaceToModulePath(ctx, targetNamespace);

            // Check for name collisions and create aliases if needed
            var typeImports = new List<TypeImport>();

            foreach (var typeName in referencedTypes)
            {
                var simpleName = GetSimpleTypeName(typeName);
                var alias = DetermineAlias(ctx, ns.Name, targetNamespace, simpleName, aliases);

                typeImports.Add(new TypeImport(
                    TypeName: simpleName,
                    Alias: alias));

                if (alias != null)
                {
                    aliases[simpleName] = alias;
                }
            }

            var importStatement = new ImportStatement(
                ImportPath: importPath,
                TargetNamespace: targetNamespace,
                TypeImports: typeImports);

            imports.Add(importStatement);

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
        var nsScope = new SinglePhase.Renaming.NamespaceScope
        {
            Namespace = ns.Name,
            IsInternal = true,
            ScopeKey = $"ns:{ns.Name}:internal"
        };

        // Export all public types in the namespace
        foreach (var type in ns.Types)
        {
            if (type.Accessibility == Model.Symbols.Accessibility.Public)
            {
                var finalName = ctx.Renamer.GetFinalTypeName(type.StableId, nsScope);
                exports.Add(new ExportStatement(
                    ExportName: finalName,
                    ExportKind: DetermineExportKind(type)));
            }
        }

        if (exports.Count > 0)
        {
            plan.NamespaceExports[ns.Name] = exports;
            ctx.Log("ImportPlanner", $"{ns.Name} exports {exports.Count} types");
        }
    }

    private static string NamespaceToModulePath(BuildContext ctx, string namespaceName)
    {
        // Convert namespace to relative module path
        // Example: "System.Collections.Generic" -> "../System.Collections.Generic/index"

        var policy = ctx.Policy.Modules;

        if (policy.UseNamespaceDirectories)
        {
            // Each namespace gets its own directory with index.d.ts
            return $"../{namespaceName}/index";
        }
        else
        {
            // Flat structure: all in same directory
            return $"./{namespaceName}";
        }
    }

    private static string GetSimpleTypeName(string fullTypeName)
    {
        // Extract simple type name from full name
        // "System.Collections.Generic.List`1" -> "List`1"
        var lastDot = fullTypeName.LastIndexOf('.');
        return lastDot >= 0 ? fullTypeName.Substring(lastDot + 1) : fullTypeName;
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
}

/// <summary>
/// Represents a TypeScript import statement.
/// </summary>
public sealed record ImportStatement(
    string ImportPath,
    string TargetNamespace,
    List<TypeImport> TypeImports);

/// <summary>
/// Represents a single type import within an import statement.
/// </summary>
public sealed record TypeImport(
    string TypeName,
    string? Alias);

/// <summary>
/// Represents a TypeScript export statement.
/// </summary>
public sealed record ExportStatement(
    string ExportName,
    ExportKind ExportKind);

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
