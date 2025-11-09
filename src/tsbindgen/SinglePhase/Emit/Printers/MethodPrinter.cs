using System.Text;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Emit.Printers;

/// <summary>
/// Prints TypeScript method signatures from MethodSymbol.
/// Handles generic methods, parameters, return types, and modifiers.
/// </summary>
public static class MethodPrinter
{
    /// <summary>
    /// Print a method signature to TypeScript.
    /// </summary>
    public static string Print(MethodSymbol method, BuildContext ctx)
    {
        var sb = new StringBuilder();

        // Get the final TS name from Renamer
        var typeScope = new SinglePhase.Renaming.TypeScope
        {
            TypeFullName = method.StableId.DeclaringClrFullName,
            IsStatic = method.IsStatic,
            ScopeKey = $"{method.StableId.DeclaringClrFullName}#{(method.IsStatic ? "static" : "instance")}"
        };

        var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, typeScope, method.IsStatic);

        // Modifiers
        if (method.IsStatic)
            sb.Append("static ");

        if (method.IsAbstract)
            sb.Append("abstract ");

        // Method name
        sb.Append(finalName);

        // Generic parameters: <T, U>
        if (method.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.GenericParameters.Select(gp => PrintGenericParameter(gp, ctx))));
            sb.Append('>');
        }

        // Parameters: (a: int, b: string)
        sb.Append('(');
        sb.Append(string.Join(", ", method.Parameters.Select(p => PrintParameter(p, ctx))));
        sb.Append(')');

        // Return type: : int
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(method.ReturnType, ctx));

        return sb.ToString();
    }

    private static string PrintGenericParameter(GenericParameterSymbol gp, BuildContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(gp.Name);

        // Print constraints from the IReadOnlyList<TypeReference>
        if (gp.Constraints.Length > 0)
        {
            sb.Append(" extends ");

            // If multiple constraints, use intersection type
            if (gp.Constraints.Length == 1)
            {
                sb.Append(TypeRefPrinter.Print(gp.Constraints[0], ctx));
            }
            else
            {
                // Multiple constraints: T extends IFoo & IBar
                var constraints = gp.Constraints.Select(c => TypeRefPrinter.Print(c, ctx));
                sb.Append(string.Join(" & ", constraints));
            }
        }

        return sb.ToString();
    }

    private static string PrintParameter(ParameterSymbol param, BuildContext ctx)
    {
        var sb = new StringBuilder();

        // Parameter name
        sb.Append(param.Name);

        // Optional parameter: name?
        if (param.HasDefaultValue)
            sb.Append('?');

        // Parameter type: name: int
        sb.Append(": ");

        // Handle ref/out parameters
        if (param.IsOut || param.IsRef)
        {
            // TypeScript has no ref/out
            // Map to { value: T } wrapper (metadata tracks original semantics)
            var innerType = TypeRefPrinter.Print(param.Type, ctx);
            sb.Append($"{{ value: {innerType} }}");
        }
        else if (param.IsParams)
        {
            // params T[] → ...args: T[]
            // Note: params keyword handled by caller (adds ... to parameter name)
            sb.Append(TypeRefPrinter.Print(param.Type, ctx));
        }
        else
        {
            sb.Append(TypeRefPrinter.Print(param.Type, ctx));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Print method with params array handling.
    /// Converts params T[] parameter to ...name: T[]
    /// </summary>
    public static string PrintWithParamsExpansion(MethodSymbol method, BuildContext ctx)
    {
        // Check if last parameter is params
        var hasParams = method.Parameters.Length > 0 && method.Parameters[^1].IsParams;

        if (!hasParams)
            return Print(method, ctx);

        // Build method signature with params expansion
        var sb = new StringBuilder();

        // Get final name
        var typeScope = new SinglePhase.Renaming.TypeScope
        {
            TypeFullName = method.StableId.DeclaringClrFullName,
            IsStatic = method.IsStatic,
            ScopeKey = $"{method.StableId.DeclaringClrFullName}#{(method.IsStatic ? "static" : "instance")}"
        };

        var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, typeScope, method.IsStatic);

        // Modifiers
        if (method.IsStatic)
            sb.Append("static ");

        if (method.IsAbstract)
            sb.Append("abstract ");

        // Method name
        sb.Append(finalName);

        // Generic parameters
        if (method.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.GenericParameters.Select(gp => PrintGenericParameter(gp, ctx))));
            sb.Append('>');
        }

        // Parameters with params expansion
        sb.Append('(');

        // Regular parameters
        if (method.Parameters.Length > 1)
        {
            var regularParams = method.Parameters.Take(method.Parameters.Length - 1);
            sb.Append(string.Join(", ", regularParams.Select(p => PrintParameter(p, ctx))));
            sb.Append(", ");
        }

        // Params parameter with ... prefix
        var paramsParam = method.Parameters[^1];
        sb.Append("...");
        sb.Append(paramsParam.Name);
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(paramsParam.Type, ctx));

        sb.Append(')');

        // Return type
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(method.ReturnType, ctx));

        return sb.ToString();
    }

    /// <summary>
    /// Print multiple method overloads.
    /// Used for methods with same name but different signatures.
    /// </summary>
    public static IEnumerable<string> PrintOverloads(IEnumerable<MethodSymbol> overloads, BuildContext ctx)
    {
        foreach (var method in overloads)
        {
            yield return Print(method, ctx);
        }
    }

    /// <summary>
    /// Print method as a property getter/setter.
    /// Used for property accessors in interfaces.
    /// </summary>
    public static string PrintAsPropertyAccessor(MethodSymbol method, bool isGetter, BuildContext ctx)
    {
        var sb = new StringBuilder();

        // Get property name from method name (get_Foo → Foo)
        var propertyName = method.ClrName;
        if (propertyName.StartsWith("get_") || propertyName.StartsWith("set_"))
            propertyName = propertyName.Substring(4);

        // Modifiers
        if (method.IsStatic)
            sb.Append("static ");

        // Property name
        sb.Append(propertyName);

        // Type
        sb.Append(": ");

        if (isGetter)
        {
            // Getter returns the property type
            sb.Append(TypeRefPrinter.Print(method.ReturnType, ctx));
        }
        else
        {
            // Setter takes property type as parameter
            if (method.Parameters.Length > 0)
                sb.Append(TypeRefPrinter.Print(method.Parameters[0].Type, ctx));
            else
                sb.Append("any"); // Fallback
        }

        return sb.ToString();
    }
}
