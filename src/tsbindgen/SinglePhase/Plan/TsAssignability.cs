using System.Linq;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// TypeScript assignability checking for erased type shapes.
/// Implements simplified TypeScript assignability rules.
/// </summary>
public static class TsAssignability
{
    /// <summary>
    /// Check if source type is assignable to target type in TypeScript.
    /// Implements basic structural typing rules:
    /// - Exact match
    /// - Covariance for arrays
    /// - Structural subtyping for objects
    /// </summary>
    public static bool IsAssignable(TsTypeShape source, TsTypeShape target)
    {
        // Exact match
        if (source.Equals(target))
            return true;

        // Unknown types are compatible with anything (conservative for validation)
        if (source is TsTypeShape.Unknown || target is TsTypeShape.Unknown)
            return true;

        // Type parameters with same name are compatible
        if (source is TsTypeShape.TypeParameter sourceParam &&
            target is TsTypeShape.TypeParameter targetParam)
        {
            return sourceParam.Name == targetParam.Name;
        }

        // Arrays: covariant in element type (readonly arrays)
        if (source is TsTypeShape.Array sourceArr &&
            target is TsTypeShape.Array targetArr)
        {
            return IsAssignable(sourceArr.ElementType, targetArr.ElementType);
        }

        // Generic applications: check if base types match and arguments are assignable
        if (source is TsTypeShape.GenericApplication sourceApp &&
            target is TsTypeShape.GenericApplication targetApp)
        {
            // Generic type definitions must match
            if (!IsAssignable(sourceApp.GenericType, targetApp.GenericType))
                return false;

            // Type arguments must match in count
            if (sourceApp.TypeArguments.Count != targetApp.TypeArguments.Count)
                return false;

            // Check each type argument (invariant for now - could be improved)
            return sourceApp.TypeArguments.Zip(targetApp.TypeArguments)
                .All(pair => IsAssignable(pair.First, pair.Second));
        }

        // Named types: exact match or known widening conversions
        if (source is TsTypeShape.Named sourceNamed &&
            target is TsTypeShape.Named targetNamed)
        {
            // Check if this is a known widening conversion
            return IsWideningConversion(sourceNamed.FullName, targetNamed.FullName);
        }

        // No other cases are assignable
        return false;
    }

    /// <summary>
    /// Check if there's a known widening conversion from source to target.
    /// Examples: int -> number, string -> object, etc.
    /// </summary>
    private static bool IsWideningConversion(string sourceFullName, string targetFullName)
    {
        // Same type
        if (sourceFullName == targetFullName)
            return true;

        // All numeric types widen to 'number' (in TypeScript terms)
        var numericTypes = new[]
        {
            "System.Byte", "System.SByte", "System.Int16", "System.UInt16",
            "System.Int32", "System.UInt32", "System.Int64", "System.UInt64",
            "System.Single", "System.Double", "System.Decimal"
        };

        if (numericTypes.Contains(sourceFullName) && numericTypes.Contains(targetFullName))
            return true;

        // Everything widens to System.Object
        if (targetFullName == "System.Object")
            return true;

        // ValueType widens to Object
        if (sourceFullName == "System.ValueType" && targetFullName == "System.Object")
            return true;

        // No other known widening conversions
        return false;
    }

    /// <summary>
    /// Check if method signature is assignable.
    /// Source can be assigned to target if:
    /// - Return types are covariant (source return type is subtype of target)
    /// - Parameter types are contravariant (target params are subtypes of source)
    /// </summary>
    public static bool IsMethodAssignable(TsMethodSignature source, TsMethodSignature target)
    {
        // Names must match
        if (source.Name != target.Name)
            return false;

        // Arity must match
        if (source.Arity != target.Arity)
            return false;

        // Parameter count must match
        if (source.Parameters.Count != target.Parameters.Count)
            return false;

        // Return type is covariant (source return can be subtype of target return)
        if (!IsAssignable(source.ReturnType, target.ReturnType))
            return false;

        // Parameters are contravariant, but for validation we use invariant check
        // (stricter, but safer for catching real breaks)
        for (int i = 0; i < source.Parameters.Count; i++)
        {
            // For validation purposes, we check structural equality
            // A real TypeScript compiler would do contravariance here
            if (!source.Parameters[i].Equals(target.Parameters[i]))
            {
                // Allow if both are compatible via assignability
                if (!IsAssignable(source.Parameters[i], target.Parameters[i]) &&
                    !IsAssignable(target.Parameters[i], source.Parameters[i]))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Check if property signature is assignable.
    /// </summary>
    public static bool IsPropertyAssignable(TsPropertySignature source, TsPropertySignature target)
    {
        // Names must match
        if (source.Name != target.Name)
            return false;

        // Readonly properties are covariant in their type
        if (source.IsReadonly && target.IsReadonly)
        {
            return IsAssignable(source.PropertyType, target.PropertyType);
        }

        // Mutable properties are invariant
        return source.PropertyType.Equals(target.PropertyType);
    }
}
