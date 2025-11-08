using System.Text;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Emit.Printers;

/// <summary>
/// Prints TypeScript type references from TypeReference model.
/// Handles all type constructs: named, generic parameters, arrays, pointers, byrefs, nested.
/// </summary>
public static class TypeRefPrinter
{
    /// <summary>
    /// Print a TypeReference to TypeScript syntax.
    /// </summary>
    public static string Print(TypeReference typeRef, BuildContext ctx)
    {
        return typeRef switch
        {
            // Defensive guard: Placeholders should never reach output after ConstraintCloser
            PlaceholderTypeReference placeholder => PrintPlaceholder(placeholder, ctx),
            NamedTypeReference named => PrintNamed(named, ctx),
            GenericParameterReference gp => PrintGenericParameter(gp),
            ArrayTypeReference arr => PrintArray(arr, ctx),
            PointerTypeReference ptr => PrintPointer(ptr, ctx),
            ByRefTypeReference byref => PrintByRef(byref, ctx),
            NestedTypeReference nested => PrintNested(nested, ctx),
            _ => "any" // Fallback for unknown types
        };
    }

    private static string PrintPlaceholder(PlaceholderTypeReference placeholder, BuildContext ctx)
    {
        // PlaceholderTypeReference should never appear in final output
        // It's only used internally to break recursion cycles during type construction
        ctx.Diagnostics.Warning(
            Core.Diagnostics.DiagnosticCodes.UnresolvedType,
            $"Placeholder type reached output: {placeholder.DebugName}. " +
            $"This indicates a cycle that wasn't resolved. Emitting 'any'.");

        return "any";
    }

    private static string PrintNamed(NamedTypeReference named, BuildContext ctx)
    {
        // Use simple name and sanitize for TypeScript
        var baseName = SanitizeClrName(named.Name);

        // Handle generic type arguments
        if (named.TypeArguments.Count == 0)
            return baseName;

        // Print generic type with arguments: Foo<T, U>
        var args = string.Join(", ", named.TypeArguments.Select(arg => Print(arg, ctx)));
        return $"{baseName}<{args}>";
    }

    private static string PrintGenericParameter(GenericParameterReference gp)
    {
        // Generic parameters use their declared name: T, U, TKey, TValue
        return gp.Name;
    }

    private static string PrintArray(ArrayTypeReference arr, BuildContext ctx)
    {
        var elementType = Print(arr.ElementType, ctx);

        // Multi-dimensional arrays: T[][], T[][][]
        if (arr.Rank == 1)
            return $"{elementType}[]";

        // For rank > 1, TypeScript doesn't have native syntax
        // Use Array<Array<T>> form
        var result = elementType;
        for (int i = 0; i < arr.Rank; i++)
            result = $"Array<{result}>";

        return result;
    }

    private static string PrintPointer(PointerTypeReference ptr, BuildContext ctx)
    {
        // TypeScript has no pointer types
        // Map to the underlying type (pointer semantics lost)
        // This is tracked in metadata as a limitation
        ctx.Log($"TypeRefPrinter: Warning - Pointer type mapped to underlying type");
        return Print(ptr.PointeeType, ctx);
    }

    private static string PrintByRef(ByRefTypeReference byref, BuildContext ctx)
    {
        // TypeScript has no ref types
        // Map to the underlying type (ref semantics tracked in metadata)
        return Print(byref.ReferencedType, ctx);
    }

    private static string PrintNested(NestedTypeReference nested, BuildContext ctx)
    {
        // Nested types: Use the full reference which has complete CLR name
        // We flatten nested types in TypeScript: Outer_Inner
        return SanitizeClrName(nested.FullReference.Name);
    }

    /// <summary>
    /// Sanitize CLR type name for TypeScript.
    /// Handles generic arity (`1 → _1) and special characters.
    /// </summary>
    private static string SanitizeClrName(string clrName)
    {
        // Replace generic arity backtick with underscore: List`1 → List_1
        var sanitized = clrName.Replace('`', '_');

        // Remove any remaining invalid TypeScript identifier characters
        sanitized = sanitized.Replace('+', '_'); // Nested type separator
        sanitized = sanitized.Replace('<', '_');
        sanitized = sanitized.Replace('>', '_');
        sanitized = sanitized.Replace('[', '_');
        sanitized = sanitized.Replace(']', '_');

        return sanitized;
    }

    /// <summary>
    /// Print a list of type references separated by commas.
    /// Used for generic parameter lists, method parameters, etc.
    /// </summary>
    public static string PrintList(IEnumerable<TypeReference> typeRefs, BuildContext ctx)
    {
        return string.Join(", ", typeRefs.Select(t => Print(t, ctx)));
    }

    /// <summary>
    /// Print a type reference with optional nullability.
    /// Used for nullable value types and reference types.
    /// </summary>
    public static string PrintNullable(TypeReference typeRef, bool isNullable, BuildContext ctx)
    {
        var baseType = Print(typeRef, ctx);
        return isNullable ? $"{baseType} | null" : baseType;
    }

    /// <summary>
    /// Print a readonly array type.
    /// Used for ReadonlyArray<T> mappings from IEnumerable<T>, etc.
    /// </summary>
    public static string PrintReadonlyArray(TypeReference elementType, BuildContext ctx)
    {
        var element = Print(elementType, ctx);
        return $"ReadonlyArray<{element}>";
    }

    /// <summary>
    /// Print a Promise type for Task<T> mappings.
    /// </summary>
    public static string PrintPromise(TypeReference resultType, BuildContext ctx)
    {
        var result = Print(resultType, ctx);
        return $"Promise<{result}>";
    }

    /// <summary>
    /// Print a tuple type for ValueTuple mappings.
    /// </summary>
    public static string PrintTuple(IReadOnlyList<TypeReference> elementTypes, BuildContext ctx)
    {
        var elements = string.Join(", ", elementTypes.Select(t => Print(t, ctx)));
        return $"[{elements}]";
    }

    /// <summary>
    /// Print a union type for TypeScript union types.
    /// </summary>
    public static string PrintUnion(IReadOnlyList<TypeReference> types, BuildContext ctx)
    {
        var parts = string.Join(" | ", types.Select(t => Print(t, ctx)));
        return parts;
    }

    /// <summary>
    /// Print an intersection type for TypeScript intersection types.
    /// </summary>
    public static string PrintIntersection(IReadOnlyList<TypeReference> types, BuildContext ctx)
    {
        var parts = string.Join(" & ", types.Select(t => Print(t, ctx)));
        return parts;
    }

    /// <summary>
    /// Print a typeof expression for static class references.
    /// Used for: typeof ClassName → (typeof ClassName)
    /// </summary>
    public static string PrintTypeof(TypeReference typeRef, BuildContext ctx)
    {
        var typeName = Print(typeRef, ctx);
        return $"typeof {typeName}";
    }
}
