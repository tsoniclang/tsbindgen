using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Analysis;

/// <summary>
/// Phase 3: Detects interfaces that have covariant member conflicts with the class.
///
/// When a class implements an interface but has covariant return types (e.g., property
/// returns TValue but interface expects KeyValuePair_2<TKey, TValue>), TypeScript
/// produces TS2416 errors even with method overloads.
///
/// This pass identifies such interfaces and marks them as "conflicting" so they can be:
/// 1. Removed from the implements clause
/// 2. Exposed as explicit interface views (As_InterfaceName properties)
///
/// Example:
/// class OrderedDictionary_2&lt;TKey, TValue&gt; implements IList_1&lt;KeyValuePair_2&lt;TKey, TValue&gt;&gt; {
///     Item(): TValue;  // Class returns just the value
/// }
///
/// interface IList_1&lt;T&gt; {
///     Item(): T;  // Interface expects KeyValuePair_2&lt;TKey, TValue&gt;
/// }
///
/// Result: TS2416 error - TValue not assignable to KeyValuePair_2&lt;TKey, TValue&gt;
///
/// After this pass:
/// - IList_1 removed from implements
/// - Added to ConflictingInterfaces
/// - Emitter will create: readonly As_IList_1_KeyValuePair: IList_1&lt;KeyValuePair_2&lt;TKey, TValue&gt;&gt;
/// </summary>
public static class ExplicitInterfaceViewDetection
{
    public static NamespaceModel Apply(NamespaceModel model, IReadOnlyDictionary<string, NamespaceModel> allModels, AnalysisContext ctx)
    {
        // Build global type lookup
        var globalTypeLookup = new Dictionary<string, TypeModel>();
        foreach (var ns in allModels.Values)
        {
            foreach (var type in ns.Types)
            {
                var key = GetTypeKey(type.Binding.Type);
                globalTypeLookup[key] = type;
            }
        }

        // Process each class/struct type
        var updatedTypes = model.Types.Select(type =>
            type.Kind == TypeKind.Class || type.Kind == TypeKind.Struct
                ? DetectConflicts(type, globalTypeLookup, ctx)
                : type
        ).ToList();

        return model with { Types = updatedTypes };
    }

    private static TypeModel DetectConflicts(TypeModel type, Dictionary<string, TypeModel> typeLookup, AnalysisContext ctx)
    {
        if (type.Implements.Count == 0)
            return type; // No interfaces to check

        var conflictingInterfaces = new List<TypeReference>();

        // Check each implemented interface
        foreach (var interfaceRef in type.Implements)
        {
            var interfaceType = FindInterfaceType(interfaceRef, typeLookup);
            if (interfaceType == null)
                continue;

            // Build substitution map for generic parameters
            var substitutions = GenericSubstitution.BuildSubstitutionMap(interfaceRef, interfaceType.GenericParameters);

            // Check for property/method signature conflicts
            if (HasMemberSignatureConflicts(type, interfaceType, substitutions, ctx))
            {
                conflictingInterfaces.Add(interfaceRef);
            }
        }

        if (conflictingInterfaces.Count == 0)
            return type; // No conflicts

        return type with { ConflictingInterfaces = conflictingInterfaces };
    }

    /// <summary>
    /// Checks if any property or method in the class has a signature that conflicts with the interface.
    /// A conflict occurs when:
    /// 1. Both class and interface have a member with the same name
    /// 2. The member signatures are not mutually assignable (covariance issue)
    /// </summary>
    private static bool HasMemberSignatureConflicts(
        TypeModel classType,
        TypeModel interfaceType,
        Dictionary<string, TypeReference> substitutions,
        AnalysisContext ctx)
    {
        // Check property conflicts
        foreach (var interfaceProp in interfaceType.Members.Properties)
        {
            // Find matching class property
            var classProp = classType.Members.Properties
                .FirstOrDefault(p => ctx.SameIdentifier(p, interfaceProp));

            if (classProp != null)
            {
                // Substitute generic parameters in interface property type
                var interfacePropType = GenericSubstitution.SubstituteType(interfaceProp.Type, substitutions);

                // Check if return types match (for getter)
                if (!TypesAreCompatible(classProp.Type, interfacePropType, ctx))
                {
                    return true; // Conflict found
                }

                // Check setter parameter type (if both have setters)
                if (!classProp.IsReadonly && !interfaceProp.IsReadonly)
                {
                    if (!TypesAreCompatible(classProp.Type, interfacePropType, ctx))
                    {
                        return true; // Conflict found
                    }
                }
            }
        }

        // Check method conflicts
        // Group methods by name (CLR name, not TS identifier)
        var classMethodsByName = classType.Members.Methods
            .GroupBy(m => m.ClrName)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var interfaceMethod in interfaceType.Members.Methods)
        {
            if (!classMethodsByName.TryGetValue(interfaceMethod.ClrName, out var classMethods))
                continue; // No matching method in class

            // Substitute generic parameters in interface method
            var interfaceMethodSubstituted = GenericSubstitution.SubstituteMethod(interfaceMethod, substitutions);

            // Check if any class method overload has a compatible signature
            bool foundCompatible = false;
            foreach (var classMethod in classMethods)
            {
                // Check if signatures are compatible (same arity, compatible parameter/return types)
                if (MethodSignaturesAreCompatible(classMethod, interfaceMethodSubstituted, ctx))
                {
                    foundCompatible = true;
                    break;
                }
            }

            // If we found class methods with the same name but NONE are compatible,
            // that's a potential conflict. However, we need to be conservative here:
            // Only mark as conflict if there's a method with matching arity but incompatible types.
            if (!foundCompatible)
            {
                // Check if there's a method with matching arity (different types = conflict)
                foreach (var classMethod in classMethods)
                {
                    if (classMethod.Parameters.Count == interfaceMethodSubstituted.Parameters.Count)
                    {
                        // Same arity but not compatible = conflict
                        return true;
                    }
                }
            }
        }

        return false; // No conflicts
    }

    /// <summary>
    /// Checks if two method signatures are compatible (can be unified in TypeScript).
    /// This includes checking return type and parameter types.
    /// </summary>
    private static bool MethodSignaturesAreCompatible(
        MethodModel classMethod,
        MethodModel interfaceMethod,
        AnalysisContext ctx)
    {
        // Must have same arity
        if (classMethod.Parameters.Count != interfaceMethod.Parameters.Count)
            return false;

        // Check return type compatibility
        if (!TypesAreCompatible(classMethod.ReturnType, interfaceMethod.ReturnType, ctx))
            return false;

        // Check parameter type compatibility
        for (int i = 0; i < classMethod.Parameters.Count; i++)
        {
            if (!TypesAreCompatible(classMethod.Parameters[i].Type, interfaceMethod.Parameters[i].Type, ctx))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if two TypeScript types are compatible (assignable).
    /// For now, we use a simple structural check based on the TypeReference.
    /// A more sophisticated implementation could use actual TS type checking.
    /// </summary>
    private static bool TypesAreCompatible(TypeReference type1, TypeReference type2, AnalysisContext ctx)
    {
        // Exact match
        if (TypeReferencesEqual(type1, type2))
            return true;

        // Object is compatible with everything (top type)
        if (type2.Namespace == "System" && type2.TypeName == "Object")
            return true;

        // Check if type1 is a subtype of type2 (simplified - not checking inheritance)
        // This is a conservative approximation
        return false;
    }

    /// <summary>
    /// Checks if two TypeReferences are equal (same namespace, name, and generic args).
    /// </summary>
    private static bool TypeReferencesEqual(TypeReference type1, TypeReference type2)
    {
        if (type1.Namespace != type2.Namespace)
            return false;

        if (type1.TypeName != type2.TypeName)
            return false;

        if (type1.GenericArgs.Count != type2.GenericArgs.Count)
            return false;

        for (int i = 0; i < type1.GenericArgs.Count; i++)
        {
            if (!TypeReferencesEqual(type1.GenericArgs[i], type2.GenericArgs[i]))
                return false;
        }

        return true;
    }

    private static TypeModel? FindInterfaceType(TypeReference typeRef, Dictionary<string, TypeModel> typeLookup)
    {
        var key = GetTypeKey(typeRef);
        typeLookup.TryGetValue(key, out var type);
        return type;
    }

    private static string GetTypeKey(TypeReference typeRef)
    {
        var ns = typeRef.Namespace != null ? typeRef.Namespace + "." : "";
        return ns + typeRef.TypeName;
    }
}
