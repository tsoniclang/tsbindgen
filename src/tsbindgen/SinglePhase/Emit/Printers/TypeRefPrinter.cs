using System.Text;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Types;

namespace tsbindgen.SinglePhase.Emit.Printers;

/// <summary>
/// Prints TypeScript type references from TypeReference model.
/// Handles all type constructs: named, generic parameters, arrays, pointers, byrefs, nested.
/// CRITICAL: Uses TypeNameResolver to ensure printed names match imports (single source of truth).
/// </summary>
public static class TypeRefPrinter
{
    /// <summary>
    /// Print a TypeReference to TypeScript syntax.
    /// CRITICAL: Always pass TypeNameResolver - never use CLR names directly.
    /// </summary>
    public static string Print(TypeReference typeRef, TypeNameResolver resolver, BuildContext ctx)
    {
        return typeRef switch
        {
            // Defensive guard: Placeholders should never reach output after ConstraintCloser
            PlaceholderTypeReference placeholder => PrintPlaceholder(placeholder, ctx),
            NamedTypeReference named => PrintNamed(named, resolver, ctx),
            GenericParameterReference gp => PrintGenericParameter(gp),
            ArrayTypeReference arr => PrintArray(arr, resolver, ctx),
            PointerTypeReference ptr => PrintPointer(ptr, resolver, ctx),
            ByRefTypeReference byref => PrintByRef(byref, resolver, ctx),
            NestedTypeReference nested => PrintNested(nested, resolver, ctx),
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

    private static string PrintNamed(NamedTypeReference named, TypeNameResolver resolver, BuildContext ctx)
    {
        // Map CLR primitive types to TypeScript built-in types (short-circuit)
        var primitiveType = TypeNameResolver.TryMapPrimitive(named.FullName);
        if (primitiveType != null)
        {
            return primitiveType;
        }

        // Handle TypeScript built-in types that we synthesize (not from CLR)
        if (named.FullName == "unknown")
        {
            return "unknown";
        }

        // CRITICAL: Get final TypeScript name from Renamer via resolver
        // This ensures printed names match import statements (single source of truth)
        // For types in graph: uses Renamer final name (may have suffix)
        // For external types: uses sanitized CLR simple name
        var baseName = resolver.ResolveTypeName(named);

        // HARDENING: Guarantee non-empty type names (defensive check)
        if (string.IsNullOrWhiteSpace(baseName))
        {
            ctx.Diagnostics.Warning(
                Core.Diagnostics.DiagnosticCodes.UnresolvedType,
                $"Empty type name for {named.AssemblyName}:{named.FullName}. " +
                $"Emitting 'unknown' as fallback.");
            return "unknown";
        }

        // Handle generic type arguments
        if (named.TypeArguments.Count == 0)
            return baseName;

        // Print generic type with arguments: Foo<T, U>
        // CRITICAL: Wrap ONLY concrete primitive types with CLROf<> to lift to their CLR types
        // This ensures generic constraints (IEquatable_1<Int32>, IComparable_1<Int32>) are satisfied
        // CLROf<T> maps: int → Int32, string → String_, byte → Byte, etc.
        // Generic parameters (T, U, TKey) pass through unchanged to avoid double-wrapping
        // Uses PrimitiveLift.IsLiftableTs as single source of truth (PG_GENERIC_PRIM_LIFT_001)
        var argParts = named.TypeArguments.Select(arg =>
        {
            var printed = Print(arg, resolver, ctx);
            // Only wrap liftable primitives with CLROf<>
            var isPrimitive = PrimitiveLift.IsLiftableTs(printed);
            return isPrimitive ? $"CLROf<{printed}>" : printed;
        }).ToList();
        var nonEmptyArgs = argParts.Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

        if (nonEmptyArgs.Count == 0)
        {
            // All type arguments erased - emit without generics
            ctx.Diagnostics.Warning(
                Core.Diagnostics.DiagnosticCodes.UnresolvedType,
                $"All type arguments erased for {named.FullName}. Emitting non-generic form.");
            return baseName;
        }

        var args = string.Join(", ", nonEmptyArgs);
        return $"{baseName}<{args}>";
    }

    private static string PrintGenericParameter(GenericParameterReference gp)
    {
        // Generic parameters use their declared name: T, U, TKey, TValue
        return gp.Name;
    }

    private static string PrintArray(ArrayTypeReference arr, TypeNameResolver resolver, BuildContext ctx)
    {
        var elementType = Print(arr.ElementType, resolver, ctx);

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

    private static string PrintPointer(PointerTypeReference ptr, TypeNameResolver resolver, BuildContext ctx)
    {
        // TypeScript has no pointer types
        // Use branded marker type: TSUnsafePointer<T> = unknown
        // This preserves type information while being type-safe (forces explicit handling)
        var pointeeType = Print(ptr.PointeeType, resolver, ctx);
        return $"TSUnsafePointer<{pointeeType}>";
    }

    private static string PrintByRef(ByRefTypeReference byref, TypeNameResolver resolver, BuildContext ctx)
    {
        // TypeScript has no ref types (ref/out/in parameters)
        // Use branded marker type: TSByRef<T> = unknown
        // This preserves type information while being type-safe
        var referencedType = Print(byref.ReferencedType, resolver, ctx);
        return $"TSByRef<{referencedType}>";
    }

    private static string PrintNested(NestedTypeReference nested, TypeNameResolver resolver, BuildContext ctx)
    {
        // CRITICAL: Nested types use resolver just like named types
        // The FullReference is a NamedTypeReference that the resolver will handle correctly
        return PrintNamed(nested.FullReference, resolver, ctx);
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
    public static string PrintList(IEnumerable<TypeReference> typeRefs, TypeNameResolver resolver, BuildContext ctx)
    {
        return string.Join(", ", typeRefs.Select(t => Print(t, resolver, ctx)));
    }

    /// <summary>
    /// Print a type reference with optional nullability.
    /// Used for nullable value types and reference types.
    /// </summary>
    public static string PrintNullable(TypeReference typeRef, bool isNullable, TypeNameResolver resolver, BuildContext ctx)
    {
        var baseType = Print(typeRef, resolver, ctx);
        return isNullable ? $"{baseType} | null" : baseType;
    }

    /// <summary>
    /// Print a readonly array type.
    /// Used for ReadonlyArray<T> mappings from IEnumerable<T>, etc.
    /// </summary>
    public static string PrintReadonlyArray(TypeReference elementType, TypeNameResolver resolver, BuildContext ctx)
    {
        var element = Print(elementType, resolver, ctx);
        return $"ReadonlyArray<{element}>";
    }

    /// <summary>
    /// Print a Promise type for Task<T> mappings.
    /// </summary>
    public static string PrintPromise(TypeReference resultType, TypeNameResolver resolver, BuildContext ctx)
    {
        var result = Print(resultType, resolver, ctx);
        return $"Promise<{result}>";
    }

    /// <summary>
    /// Print a tuple type for ValueTuple mappings.
    /// </summary>
    public static string PrintTuple(IReadOnlyList<TypeReference> elementTypes, TypeNameResolver resolver, BuildContext ctx)
    {
        var elements = string.Join(", ", elementTypes.Select(t => Print(t, resolver, ctx)));
        return $"[{elements}]";
    }

    /// <summary>
    /// Print a union type for TypeScript union types.
    /// </summary>
    public static string PrintUnion(IReadOnlyList<TypeReference> types, TypeNameResolver resolver, BuildContext ctx)
    {
        var parts = string.Join(" | ", types.Select(t => Print(t, resolver, ctx)));
        return parts;
    }

    /// <summary>
    /// Print an intersection type for TypeScript intersection types.
    /// </summary>
    public static string PrintIntersection(IReadOnlyList<TypeReference> types, TypeNameResolver resolver, BuildContext ctx)
    {
        var parts = string.Join(" & ", types.Select(t => Print(t, resolver, ctx)));
        return parts;
    }

    /// <summary>
    /// Print a typeof expression for static class references.
    /// Used for: typeof ClassName → (typeof ClassName)
    /// </summary>
    public static string PrintTypeof(TypeReference typeRef, TypeNameResolver resolver, BuildContext ctx)
    {
        var typeName = Print(typeRef, resolver, ctx);
        return $"typeof {typeName}";
    }
}
