using System.Text;
using GenerateDts.Pipeline;

namespace GenerateDts.Emit.Writers;

/// <summary>
/// Generates import statements for intrinsics and cross-assembly dependencies.
/// </summary>
public static class ImportWriter
{
    /// <summary>
    /// Renders the standard intrinsics import that all declaration files need.
    /// </summary>
    public static void RenderIntrinsicsImport(StringBuilder sb)
    {
        sb.AppendLine("import type { int, uint, byte, sbyte, short, ushort, long, ulong, float, double, decimal, Covariant } from './_intrinsics.js';");
        sb.AppendLine();
    }

    /// <summary>
    /// Generates import statements for cross-assembly dependencies.
    /// Uses namespace imports with aliases to avoid naming conflicts.
    /// </summary>
    public static void RenderDependencyImports(StringBuilder sb, DependencyTracker dependencyTracker)
    {
        var dependentAssemblies = dependencyTracker.GetDependentAssemblies();

        if (dependentAssemblies.Count == 0)
        {
            return; // No external dependencies
        }

        sb.AppendLine("// Cross-assembly type imports");

        foreach (var assemblyName in dependentAssemblies)
        {
            var alias = DependencyTracker.GetModuleAlias(assemblyName);

            // Import entire namespace with alias
            // Example: import type * as System_Private_CoreLib from './System.Private.CoreLib.js';
            sb.AppendLine($"import type * as {alias} from './{assemblyName}.js';");
        }

        sb.AppendLine();
    }
}
