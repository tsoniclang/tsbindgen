using System.Collections.Generic;
using System.Linq;
using System.Text;
using tsbindgen.SinglePhase.Model.Symbols;

namespace tsbindgen.SinglePhase.Emit;

/// <summary>
/// Unified type alias emission logic.
/// Ensures consistent generic parameter handling across all alias emission sites:
/// - Facade exports
/// - Internal convenience exports
/// - View composition aliases
/// This prevents TS2315 "Type is not generic" errors by guaranteeing LHS and RHS arity match.
/// </summary>
internal static class AliasEmit
{
    /// <summary>
    /// Emits a type alias with proper generic parameter handling.
    /// Guarantees LHS and RHS have matching arity and parameter names.
    /// </summary>
    /// <param name="sb">StringBuilder to append to</param>
    /// <param name="aliasName">LHS alias name (e.g., "Foo")</param>
    /// <param name="sourceType">Source type symbol (determines arity and constraints)</param>
    /// <param name="rhsExpression">RHS expression base (e.g., "Internal.Foo" or "Foo$instance & __Foo$views")</param>
    /// <param name="resolver">Type name resolver for printing constraints</param>
    /// <param name="ctx">Build context</param>
    /// <param name="withConstraints">Whether to include constraints on LHS (default: false for simple re-exports)</param>
    internal static void EmitGenericAlias(
        StringBuilder sb,
        string aliasName,
        TypeSymbol sourceType,
        string rhsExpression,
        TypeNameResolver resolver,
        BuildContext ctx,
        bool withConstraints = false)
    {
        var gps = sourceType.GenericParameters;

        // Non-generic: trivial case
        if (gps.Length == 0)
        {
            sb.Append("export type ");
            sb.Append(aliasName);
            sb.Append(" = ");
            sb.Append(rhsExpression);
            sb.AppendLine(";");
            return;
        }

        // Generic: emit with type parameters
        sb.Append("export type ");
        sb.Append(aliasName);

        // LHS: Generate type parameters (with or without constraints)
        if (withConstraints)
        {
            var typeParamsLHS = GenerateTypeParametersWithConstraints(sourceType, resolver, ctx);
            sb.Append(typeParamsLHS);
        }
        else
        {
            // Simple parameter list without constraints
            sb.Append('<');
            sb.Append(string.Join(", ", gps.Select(gp => gp.Name)));
            sb.Append('>');
        }

        sb.Append(" = ");
        sb.Append(rhsExpression);

        // RHS: Generate type arguments (parameter names only, no constraints)
        sb.Append('<');
        sb.Append(string.Join(", ", gps.Select(gp => gp.Name)));
        sb.AppendLine(">;");
    }

    /// <summary>
    /// Generates generic type parameters WITH constraints for LHS of alias.
    /// Example: "<T extends IFoo, U extends IBar>"
    /// </summary>
    internal static string GenerateTypeParametersWithConstraints(
        TypeSymbol sourceType,
        TypeNameResolver resolver,
        BuildContext ctx)
    {
        var gps = sourceType.GenericParameters;
        if (gps.Length == 0)
            return string.Empty;

        var parts = new List<string>(gps.Length);

        foreach (var gp in gps)
        {
            // Collect type constraints (interfaces/classes)
            var typeConstraints = gp.Constraints
                .Where(c => c is not null && !IsSpecialConstraint(c))
                .ToList();

            if (typeConstraints.Count == 0)
            {
                parts.Add(gp.Name);
            }
            else
            {
                // Print each constraint using TypeRefPrinter
                var constraintStrings = typeConstraints
                    .Select(c => Printers.TypeRefPrinter.Print(c, resolver, ctx))
                    .ToArray();
                var constraintList = string.Join(" & ", constraintStrings);
                parts.Add($"{gp.Name} extends {constraintList}");
            }
        }

        return $"<{string.Join(", ", parts)}>";
    }

    /// <summary>
    /// Generates generic type arguments for RHS of alias.
    /// Example: "<T, U>" (parameter names only, no constraints)
    /// </summary>
    internal static string GenerateTypeArguments(TypeSymbol sourceType)
    {
        var gps = sourceType.GenericParameters;
        if (gps.Length == 0)
            return string.Empty;

        var names = gps.Select(gp => gp.Name);
        return $"<{string.Join(", ", names)}>";
    }

    /// <summary>
    /// Checks if a constraint is a special C# constraint that doesn't translate to TypeScript.
    /// Special constraints: struct (System.ValueType), class (System.Object), new()
    /// </summary>
    private static bool IsSpecialConstraint(Model.Types.TypeReference constraint)
    {
        // Filter out C# special constraints: struct, class, new()
        // These don't translate to TypeScript extends clauses
        if (constraint is Model.Types.NamedTypeReference named)
        {
            return named.FullName is "System.ValueType" or "System.Object"
                && named.TypeArguments.Count == 0;
        }
        return false;
    }
}
