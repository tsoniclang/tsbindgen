using System.Collections.Immutable;
using System.Linq;
using System.Text;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Renaming;

namespace tsbindgen.SinglePhase.Emit.Printers;

/// <summary>
/// Prints TypeScript method signatures from MethodSymbol.
/// Handles generic methods, parameters, return types, and modifiers.
/// </summary>
public static class MethodPrinter
{
    /// <summary>
    /// METHOD CONSTRAINTS (Pattern A - Shadowing): Enriches method generic parameters with class constraints.
    /// When a method generic parameter has the same name as a class generic but no constraints of its own,
    /// inherit the class generic's constraints for TypeScript emission.
    ///
    /// This fixes TS2344 errors where:
    ///   class Foo<T extends IEquatable_1<T>> {
    ///       static Bar<T>(x: T): Foo<T>;  // method T unconstrained
    ///   }
    ///
    /// Becomes:
    ///   class Foo<T extends IEquatable_1<T>> {
    ///       static Bar<T extends IEquatable_1<T>>(x: T): Foo<T>;  // inherited constraint
    ///   }
    /// </summary>
    private static ImmutableArray<GenericParameterSymbol> EnrichMethodGenericParametersWithClassConstraints(
        MethodSymbol method,
        TypeSymbol declaringType)
    {
        // Build lookup: class generic name → constraints
        var classConstraints = declaringType.GenericParameters
            .Where(gp => gp.Constraints.Length > 0)
            .ToDictionary(gp => gp.Name, gp => gp.Constraints);

        if (classConstraints.Count == 0)
        {
            // No class constraints to inherit
            return method.GenericParameters;
        }

        var enriched = new List<GenericParameterSymbol>(method.GenericParameters.Length);
        bool anyEnriched = false;

        foreach (var methodGp in method.GenericParameters)
        {
            // Pattern A: Shadowing fallback
            // If method parameter has no constraints but class has constraints for same name, inherit them
            if (methodGp.Constraints.Length == 0 &&
                classConstraints.TryGetValue(methodGp.Name, out var inheritedConstraints))
            {
                // Create enriched parameter with class constraints
                enriched.Add(methodGp with { Constraints = inheritedConstraints });
                anyEnriched = true;
            }
            else
            {
                // Keep as-is (method has its own constraints, or no matching class constraint)
                enriched.Add(methodGp);
            }
        }

        return anyEnriched
            ? enriched.ToImmutableArray()
            : method.GenericParameters;
    }


    /// <summary>
    /// Print a method signature to TypeScript.
    /// </summary>
    public static string Print(MethodSymbol method, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
    {
        // Get the final TS name from Renamer using correct scope
        var scope = ScopeFactory.ClassSurface(declaringType, method.IsStatic);
        var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, scope);

        return PrintWithName(method, declaringType, finalName, resolver, ctx);
    }

    /// <summary>
    /// TS2416/TS2420 FIX: Print a method signature with a custom name (for overload sets).
    /// Used when emitting TypeScript overloads with CLR-cased names instead of Renamer names.
    /// </summary>
    /// <param name="emitAbstract">Optional: override whether to emit abstract keyword (for TS2512 fix)</param>
    public static string PrintWithName(MethodSymbol method, TypeSymbol declaringType, string methodName, TypeNameResolver resolver, BuildContext ctx, bool? emitAbstract = null)
    {
        var sb = new StringBuilder();

        // Modifiers
        // IMPORTANT: Don't emit static/abstract modifiers for interface members
        // - TypeScript interfaces don't support static members (C# 11 feature)
        // - TypeScript interface methods are implicitly abstract
        var isInterface = declaringType.Kind == TypeKind.Interface;

        if (method.IsStatic && !isInterface)
            sb.Append("static ");

        // TS2512 FIX: Use group-level abstract status if provided, otherwise use method's individual status
        var shouldEmitAbstract = emitAbstract ?? method.IsAbstract;
        if (shouldEmitAbstract && !isInterface)
            sb.Append("abstract ");

        // Method name (provided by caller)
        sb.Append(methodName);

        // Generic parameters: <T, U>
        // METHOD CONSTRAINTS: Enrich with class constraints for shadowing case (Pattern A)
        var enrichedGenericParams = EnrichMethodGenericParametersWithClassConstraints(method, declaringType);
        if (enrichedGenericParams.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", enrichedGenericParams.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        // Parameters: (a: int, b: string)
        sb.Append('(');
        sb.Append(string.Join(", ", method.Parameters.Select(p => PrintParameter(p, resolver, ctx))));
        sb.Append(')');

        // Return type: : int
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(method.ReturnType, resolver, ctx));

        return sb.ToString();
    }

    private static string PrintGenericParameter(GenericParameterSymbol gp, TypeNameResolver resolver, BuildContext ctx)
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
                sb.Append(TypeRefPrinter.Print(gp.Constraints[0], resolver, ctx));
            }
            else
            {
                // Multiple constraints: T extends IFoo & IBar
                var constraints = gp.Constraints.Select(c => TypeRefPrinter.Print(c, resolver, ctx));
                sb.Append(string.Join(" & ", constraints));
            }
        }

        return sb.ToString();
    }

    private static string PrintParameter(ParameterSymbol param, TypeNameResolver resolver, BuildContext ctx)
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
            var innerType = TypeRefPrinter.Print(param.Type, resolver, ctx);
            sb.Append($"{{ value: {innerType} }}");
        }
        else if (param.IsParams)
        {
            // params T[] → ...args: T[]
            // Note: params keyword handled by caller (adds ... to parameter name)
            sb.Append(TypeRefPrinter.Print(param.Type, resolver, ctx));
        }
        else
        {
            sb.Append(TypeRefPrinter.Print(param.Type, resolver, ctx));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Print method with params array handling.
    /// Converts params T[] parameter to ...name: T[]
    /// </summary>
    public static string PrintWithParamsExpansion(MethodSymbol method, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
    {
        // Check if last parameter is params
        var hasParams = method.Parameters.Length > 0 && method.Parameters[^1].IsParams;

        if (!hasParams)
            return Print(method, declaringType, resolver, ctx);

        // Build method signature with params expansion
        var sb = new StringBuilder();

        // Get final name using correct scope
        var scope = ScopeFactory.ClassSurface(declaringType, method.IsStatic);
        var finalName = ctx.Renamer.GetFinalMemberName(method.StableId, scope);

        // Modifiers
        // IMPORTANT: Don't emit static/abstract modifiers for interface members
        var isInterface = declaringType.Kind == TypeKind.Interface;

        if (method.IsStatic && !isInterface)
            sb.Append("static ");

        if (method.IsAbstract && !isInterface)
            sb.Append("abstract ");

        // Method name
        sb.Append(finalName);

        // Generic parameters
        if (method.GenericParameters.Length > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", method.GenericParameters.Select(gp => PrintGenericParameter(gp, resolver, ctx))));
            sb.Append('>');
        }

        // Parameters with params expansion
        sb.Append('(');

        // Regular parameters
        if (method.Parameters.Length > 1)
        {
            var regularParams = method.Parameters.Take(method.Parameters.Length - 1);
            sb.Append(string.Join(", ", regularParams.Select(p => PrintParameter(p, resolver, ctx))));
            sb.Append(", ");
        }

        // Params parameter with ... prefix
        var paramsParam = method.Parameters[^1];
        sb.Append("...");
        sb.Append(paramsParam.Name);
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(paramsParam.Type, resolver, ctx));

        sb.Append(')');

        // Return type
        sb.Append(": ");
        sb.Append(TypeRefPrinter.Print(method.ReturnType, resolver, ctx));

        return sb.ToString();
    }

    /// <summary>
    /// Print multiple method overloads.
    /// Used for methods with same name but different signatures.
    /// </summary>
    public static IEnumerable<string> PrintOverloads(IEnumerable<MethodSymbol> overloads, TypeSymbol declaringType, TypeNameResolver resolver, BuildContext ctx)
    {
        foreach (var method in overloads)
        {
            yield return Print(method, declaringType, resolver, ctx);
        }
    }

    /// <summary>
    /// Print method as a property getter/setter.
    /// Used for property accessors in interfaces.
    /// </summary>
    public static string PrintAsPropertyAccessor(MethodSymbol method, bool isGetter, TypeNameResolver resolver, BuildContext ctx)
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
            sb.Append(TypeRefPrinter.Print(method.ReturnType, resolver, ctx));
        }
        else
        {
            // Setter takes property type as parameter
            if (method.Parameters.Length > 0)
                sb.Append(TypeRefPrinter.Print(method.Parameters[0].Type, resolver, ctx));
            else
                sb.Append("any"); // Fallback
        }

        return sb.ToString();
    }
}
