using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Analysis;

/// <summary>
/// Utility for substituting generic type parameters with concrete types.
///
/// When a non-generic class implements a generic interface like IEqualityComparer_1&lt;T&gt;,
/// the generic parameter T needs to be substituted with the concrete type.
///
/// Example:
/// ByteEqualityComparer implements IEqualityComparer_1&lt;System.Byte&gt;
/// Interface method: Equals(x: T, y: T): Boolean
/// Substituted: Equals(x: System.Byte, y: System.Byte): Boolean
/// </summary>
public static class GenericSubstitution
{
    /// <summary>
    /// Builds a substitution map from a generic interface reference.
    /// Maps generic parameter names to their concrete types.
    /// Note: Generic parameter names are used as-is (no transformation).
    /// </summary>
    public static Dictionary<string, TypeReference> BuildSubstitutionMap(
        TypeReference interfaceRef,
        IReadOnlyList<GenericParameterModel> interfaceGenericParams)
    {
        var substitutions = new Dictionary<string, TypeReference>();

        // If the interface reference has generic arguments, map them to the parameter names
        if (interfaceRef.GenericArgs.Count > 0 &&
            interfaceGenericParams.Count == interfaceRef.GenericArgs.Count)
        {
            for (int i = 0; i < interfaceGenericParams.Count; i++)
            {
                // Generic parameters are not transformed - use Name directly
                var paramName = interfaceGenericParams[i].Name;
                var concreteType = interfaceRef.GenericArgs[i];
                substitutions[paramName] = concreteType;
            }
        }

        return substitutions;
    }

    /// <summary>
    /// Substitutes generic type parameters in a TypeReference with full recursion.
    /// Handles nested generics, arrays, pointers, and declaring types.
    /// When a type parameter is found, substitutes it and then recursively processes the result.
    /// </summary>
    public static TypeReference SubstituteType(
        TypeReference type,
        Dictionary<string, TypeReference> substitutions)
    {
        return SubstituteTypeInternal(type, substitutions, new HashSet<string>());
    }

    private static TypeReference SubstituteTypeInternal(
        TypeReference type,
        Dictionary<string, TypeReference> substitutions,
        HashSet<string> visiting)
    {
        // Check if this is a type parameter that needs substitution
        if (substitutions.TryGetValue(type.TypeName, out var concreteType))
        {
            // Cycle detection: if we're already visiting this type parameter, return it as-is to break the cycle
            if (visiting.Contains(type.TypeName))
            {
                return type;
            }

            // Mark this type parameter as being visited
            visiting.Add(type.TypeName);

            // CRITICAL: Recursively substitute the concrete type itself
            // This handles cases like T → KeyValuePair_2<TKey, TValue> where TKey/TValue need substitution
            var result = SubstituteTypeInternal(concreteType, substitutions, visiting);

            // Unmark after substitution (backtrack)
            visiting.Remove(type.TypeName);

            return result;
        }

        // Handle arrays: substitute the element type
        if (type.ArrayRank > 0)
        {
            var substitutedElement = SubstituteTypeInternal(
                new TypeReference(
                    type.Namespace,
                    type.TypeName,
                    type.GenericArgs,
                    0, // Remove array rank temporarily
                    type.PointerDepth,
                    type.DeclaringType,
                    type.Assembly
                ),
                substitutions,
                visiting
            );

            // Reapply the array rank
            return new TypeReference(
                substitutedElement.Namespace,
                substitutedElement.TypeName,
                substitutedElement.GenericArgs,
                type.ArrayRank,
                substitutedElement.PointerDepth,
                substitutedElement.DeclaringType,
                substitutedElement.Assembly
            );
        }

        // Handle pointers: substitute the pointed-to type
        if (type.PointerDepth > 0)
        {
            var substitutedPointed = SubstituteTypeInternal(
                new TypeReference(
                    type.Namespace,
                    type.TypeName,
                    type.GenericArgs,
                    type.ArrayRank,
                    0, // Remove pointer depth temporarily
                    type.DeclaringType,
                    type.Assembly
                ),
                substitutions,
                visiting
            );

            // Reapply the pointer depth
            return new TypeReference(
                substitutedPointed.Namespace,
                substitutedPointed.TypeName,
                substitutedPointed.GenericArgs,
                substitutedPointed.ArrayRank,
                type.PointerDepth,
                substitutedPointed.DeclaringType,
                substitutedPointed.Assembly
            );
        }

        // Handle declaring types: substitute the declaring type
        if (type.DeclaringType != null)
        {
            var substitutedDeclaring = SubstituteTypeInternal(type.DeclaringType, substitutions, visiting);

            return new TypeReference(
                type.Namespace,
                type.TypeName,
                type.GenericArgs,
                type.ArrayRank,
                type.PointerDepth,
                substitutedDeclaring,
                type.Assembly
            );
        }

        // If this type has generic arguments, recursively substitute them
        if (type.GenericArgs.Count > 0)
        {
            var substitutedArgs = type.GenericArgs
                .Select(arg => SubstituteTypeInternal(arg, substitutions, visiting))
                .ToList();

            return new TypeReference(
                type.Namespace,
                type.TypeName,
                substitutedArgs,
                type.ArrayRank,
                type.PointerDepth,
                type.DeclaringType,
                type.Assembly
            );
        }

        return type; // No substitution needed
    }

    /// <summary>
    /// Substitutes generic type parameters in a MethodModel.
    /// Returns a new MethodModel with substituted types.
    /// </summary>
    public static MethodModel SubstituteMethod(
        MethodModel method,
        Dictionary<string, TypeReference> substitutions)
    {
        // Substitute return type
        var newReturnType = SubstituteType(method.ReturnType, substitutions);

        // Substitute parameter types
        var newParams = method.Parameters
            .Select(p => p with { Type = SubstituteType(p.Type, substitutions) })
            .ToList();

        // Substitute generic parameters constraints
        var newGenericParams = method.GenericParameters
            .Select(gp => SubstituteGenericParameter(gp, substitutions))
            .ToList();

        return method with
        {
            ReturnType = newReturnType,
            Parameters = newParams,
            GenericParameters = newGenericParams
        };
    }

    /// <summary>
    /// Substitutes generic type parameters in a PropertyModel.
    /// </summary>
    public static PropertyModel SubstituteProperty(
        PropertyModel property,
        Dictionary<string, TypeReference> substitutions)
    {
        var newType = SubstituteType(property.Type, substitutions);
        var newContractType = property.ContractType != null
            ? SubstituteType(property.ContractType, substitutions)
            : null;

        return property with
        {
            Type = newType,
            ContractType = newContractType
        };
    }

    private static GenericParameterModel SubstituteGenericParameter(
        GenericParameterModel gp,
        Dictionary<string, TypeReference> substitutions)
    {
        var newConstraints = gp.Constraints
            .Select(c => SubstituteType(c, substitutions))
            .ToList();

        return gp with { Constraints = newConstraints };
    }
}
