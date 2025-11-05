using System.Reflection;
using GenerateDts.Mapping;
using TypeInfo = GenerateDts.Model.TypeInfo;

namespace GenerateDts.Analysis;

public static class OverloadBuilder
{
    public static void AddInterfaceCompatibleOverloads(
        Type type,
        List<TypeInfo.PropertyInfo> properties,
        List<TypeInfo.MethodInfo> methods,
        TypeMapper typeMapper,
        Func<System.Reflection.ParameterInfo, TypeInfo.ParameterInfo> processParameter)
    {
        try
        {
            var interfaces = type.GetInterfaces();
            foreach (var iface in interfaces)
            {
                // Skip non-public interfaces (e.g., IAsyncStateMachineBox)
                if (!iface.IsPublic && !iface.IsNestedPublic)
                {
                    continue;
                }

                try
                {
                    var map = type.GetInterfaceMap(iface);
                    for (int i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        var interfaceMethod = map.InterfaceMethods[i];

                        // Skip property getters/setters - they can't have overloads in TypeScript
                        if (interfaceMethod.IsSpecialName)
                        {
                            continue;
                        }

                        // Skip explicit interface implementations (method name contains dot)
                        if (interfaceMethod.Name.Contains('.'))
                        {
                            continue;
                        }

                        // Skip methods with non-public parameter or return types
                        if (!interfaceMethod.ReturnType.IsPublic && !interfaceMethod.ReturnType.IsNestedPublic && interfaceMethod.ReturnType != typeof(void))
                        {
                            continue;
                        }

                        bool hasNonPublicParam = false;
                        foreach (var param in interfaceMethod.GetParameters())
                        {
                            if (!param.ParameterType.IsPublic && !param.ParameterType.IsNestedPublic)
                            {
                                hasNonPublicParam = true;
                                break;
                            }
                        }

                        if (hasNonPublicParam)
                        {
                            continue;
                        }

                        // Regular method - add interface-compatible overload
                        var interfaceReturnType = typeMapper.MapType(interfaceMethod.ReturnType);
                        var interfaceParams = interfaceMethod.GetParameters()
                            .Select(processParameter)
                            .ToList();

                        // Check if we already have this exact method signature
                        var hasExactMatch = methods.Any(m =>
                            m.Name == interfaceMethod.Name &&
                            m.ReturnType == interfaceReturnType &&
                            ParameterListsMatch(m.Parameters, interfaceParams));

                        if (!hasExactMatch)
                        {
                            // Add interface-compatible method signature
                            var genericParams = interfaceMethod.IsGenericMethod
                                ? interfaceMethod.GetGenericArguments().Select(t => t.Name).ToList()
                                : new List<string>();

                            methods.Add(new TypeInfo.MethodInfo(
                                interfaceMethod.Name,
                                interfaceReturnType,
                                interfaceParams,
                                false, // Instance method (interface methods are never static)
                                interfaceMethod.IsGenericMethod,
                                genericParams));
                        }
                    }
                }
                catch
                {
                    // GetInterfaceMap can fail for some types in MetadataLoadContext
                    // Skip and continue
                }
            }
        }
        catch
        {
            // Type may not support interface mapping
        }
    }

    /// <summary>
    /// Adds base class-compatible method and property overloads for TS2416 covariance issues.
    /// When a derived class overrides a base method with a more specific return type,
    /// TypeScript requires both signatures to be present.
    /// </summary>
    public static void AddBaseClassCompatibleOverloads(
        Type type,
        List<TypeInfo.PropertyInfo> properties,
        List<TypeInfo.MethodInfo> methods,
        TypeMapper typeMapper,
        Func<MemberInfo, bool> shouldIncludeMember,
        Func<System.Reflection.ParameterInfo, TypeInfo.ParameterInfo> processParameter,
        Func<Type, HashSet<Type>, bool> typeReferencesAnyTypeParam,
        Action<Type> trackTypeDependency)
    {
        if (type.BaseType == null
            || type.BaseType.FullName == "System.Object"
            || type.BaseType.FullName == "System.ValueType"
            || type.BaseType.FullName == "System.MarshalByRefObject")
        {
            return; // No base class to process
        }

        try
        {
            AddBaseClassOverloadsRecursive(type, type.BaseType, properties, methods, typeMapper, shouldIncludeMember, processParameter, typeReferencesAnyTypeParam, trackTypeDependency);
        }
        catch
        {
            // Base class may not be accessible in MetadataLoadContext
        }
    }

    /// <summary>
    /// Enumerates static methods from a base type, including method-level generics from the generic type definition.
    /// For constructed generic types like EqualityComparer&lt;byte&gt;, method-level generics like Create&lt;T&gt;()
    /// are only visible on the generic type definition EqualityComparer&lt;T&gt;.
    /// </summary>
    private static IEnumerable<System.Reflection.MethodInfo> EnumerateBaseStaticMethods(Type baseType)
    {
        var binding = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Methods declared directly on this constructed type
        foreach (var m in baseType.GetMethods(binding))
            yield return m;

        // If the type is generic, pull ONLY method-level generics from the type definition
        // Regular static methods are already available on the constructed type
        if (baseType.IsGenericType && !baseType.IsGenericTypeDefinition)
        {
            var genericTypeDef = baseType.GetGenericTypeDefinition();
            var methodsOnTypeDef = genericTypeDef.GetMethods(binding);

            foreach (var m in methodsOnTypeDef)
            {
                // Include ALL static methods from the generic type definition because:
                // 1. Methods with method-level generics (IsGenericMethod == true)
                // 2. Methods using class type parameters (IsGenericMethod == false but become generic in TS)
                // Both need to be added to derived classes to satisfy TypeScript's static side checking
                // The deduplication happens later in AddBaseClassOverloadsRecursive
                yield return m;
            }
        }
    }

    /// <summary>
    /// Recursively adds base class overloads from the entire inheritance chain.
    /// </summary>
    private static void AddBaseClassOverloadsRecursive(
        Type derivedType,
        Type baseType,
        List<TypeInfo.PropertyInfo> properties,
        List<TypeInfo.MethodInfo> methods,
        TypeMapper typeMapper,
        Func<MemberInfo, bool> shouldIncludeMember,
        Func<System.Reflection.ParameterInfo, TypeInfo.ParameterInfo> processParameter,
        Func<Type, HashSet<Type>, bool> typeReferencesAnyTypeParam,
        Action<Type> trackTypeDependency)
    {
        if (baseType == null
            || baseType.FullName == "System.Object"
            || baseType.FullName == "System.ValueType"
            || baseType.FullName == "System.MarshalByRefObject")
        {
            return;
        }

        try
        {
            // Process base class methods
            var baseMethods = baseType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Cast<MemberInfo>()
                .Where(shouldIncludeMember)
                .Cast<System.Reflection.MethodInfo>()
                .Where(m => !m.IsSpecialName)
                .Where(m => !m.Name.Contains('.')); // Skip explicit interface implementations

            foreach (var baseMethod in baseMethods)
            {
                try
                {
                    // Track dependency for base method return type and parameters
                    trackTypeDependency(baseMethod.ReturnType);
                    foreach (var param in baseMethod.GetParameters())
                    {
                        trackTypeDependency(param.ParameterType);
                    }

                    var baseReturnType = typeMapper.MapType(baseMethod.ReturnType);
                    var baseParams = baseMethod.GetParameters()
                        .Select(processParameter)
                        .ToList();

                    // Check if we already have this exact method signature
                    var hasExactMatch = methods.Any(m =>
                        m.Name == baseMethod.Name &&
                        m.ReturnType == baseReturnType &&
                        ParameterListsMatch(m.Parameters, baseParams));

                    if (!hasExactMatch)
                    {
                        // Add base class-compatible method signature
                        var genericParams = baseMethod.IsGenericMethod
                            ? baseMethod.GetGenericArguments().Select(t => t.Name).ToList()
                            : new List<string>();

                        methods.Add(new TypeInfo.MethodInfo(
                            baseMethod.Name,
                            baseReturnType,
                            baseParams,
                            false, // Instance method (base methods are not static in this context)
                            baseMethod.IsGenericMethod,
                            genericParams));
                    }
                }
                catch
                {
                    // Skip methods that can't be processed
                }
            }

            // Process base class static methods (for TS2417 static member conflicts)
            // Need to check both the constructed type AND the generic type definition
            // because method-level generics like Create<T>() live on the type definition
            var baseStaticMethods = EnumerateBaseStaticMethods(baseType)
                .Cast<MemberInfo>()
                .Where(shouldIncludeMember)
                .Cast<System.Reflection.MethodInfo>()
                .Where(m => !m.IsSpecialName)
                .Where(m => !m.Name.Contains('.')); // Skip explicit interface implementations

            foreach (var baseStaticMethod in baseStaticMethods)
            {
                try
                {
                    // Track dependency for base static method return type and parameters
                    trackTypeDependency(baseStaticMethod.ReturnType);
                    foreach (var param in baseStaticMethod.GetParameters())
                    {
                        trackTypeDependency(param.ParameterType);
                    }

                    // TS2302: Skip if base static method uses DERIVED class type parameters
                    // TypeScript doesn't allow static members to reference class type parameters
                    // Check at reflection level before mapping
                    if (derivedType.IsGenericType)
                    {
                        var derivedTypeParams = derivedType.GetGenericArguments().ToHashSet();

                        // Check if return type references any derived class type parameter
                        if (typeReferencesAnyTypeParam(baseStaticMethod.ReturnType, derivedTypeParams))
                        {
                            continue; // Skip - would cause TS2302
                        }

                        // Check if any parameter type references derived class type parameters
                        if (baseStaticMethod.GetParameters().Any(p => typeReferencesAnyTypeParam(p.ParameterType, derivedTypeParams)))
                        {
                            continue; // Skip - would cause TS2302
                        }
                    }

                    var baseReturnType = typeMapper.MapType(baseStaticMethod.ReturnType);
                    var baseParams = baseStaticMethod.GetParameters()
                        .Select(processParameter)
                        .ToList();

                    // Check if we already have this exact static method signature
                    var hasExactMatch = methods.Any(m =>
                        m.Name == baseStaticMethod.Name &&
                        m.IsStatic &&
                        m.ReturnType == baseReturnType &&
                        ParameterListsMatch(m.Parameters, baseParams));

                    if (!hasExactMatch)
                    {
                        // Add base class-compatible static method signature
                        // Use same logic as ProcessMethod for static methods in generic classes
                        var genericParams = new List<string>();
                        var isGeneric = baseStaticMethod.IsGenericMethod;

                        // If this is a static method in a generic base class, add the class's type parameters
                        // to make it generic in TypeScript (same as ProcessMethod lines 794-814)
                        if (baseType.IsGenericType)
                        {
                            // IMPORTANT: Get type parameters from the DEFINITION, not the constructed type!
                            // For EqualityComparer_1<byte>, we want "T", not "Byte"
                            var baseTypeDef = baseType.IsGenericTypeDefinition ? baseType : baseType.GetGenericTypeDefinition();
                            var classTypeParams = baseTypeDef.GetGenericArguments().Select(t => t.Name).ToList();

                            if (baseStaticMethod.IsGenericMethod)
                            {
                                // Method already has its own type parameters - prepend class params
                                var methodTypeParams = baseStaticMethod.GetGenericArguments().Select(t => t.Name).ToList();
                                genericParams = classTypeParams.Concat(methodTypeParams).ToList();
                            }
                            else
                            {
                                // Method has no generic params - use class params
                                genericParams = classTypeParams;
                            }

                            isGeneric = genericParams.Count > 0;
                        }
                        else if (baseStaticMethod.IsGenericMethod)
                        {
                            // Non-generic class, but method is generic
                            genericParams = baseStaticMethod.GetGenericArguments().Select(t => t.Name).ToList();
                        }

                        methods.Add(new TypeInfo.MethodInfo(
                            baseStaticMethod.Name,
                            baseReturnType,
                            baseParams,
                            true, // Static method
                            isGeneric,
                            genericParams));
                    }
                }
                catch
                {
                    // Skip methods that can't be processed
                }
            }

            // Process base class properties (for covariant property return types)
            var baseProperties = baseType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Cast<MemberInfo>()
                .Where(shouldIncludeMember)
                .Cast<System.Reflection.PropertyInfo>();

            foreach (var baseProp in baseProperties)
            {
                try
                {
                    // Skip indexers
                    if (baseProp.GetIndexParameters().Length > 0)
                        continue;

                    trackTypeDependency(baseProp.PropertyType);

                    var basePropertyType = typeMapper.MapType(baseProp.PropertyType);

                    // Check if we already have a property with this name
                    // (Properties cannot be overloaded in TypeScript, unlike methods)
                    var hasProperty = properties.Any(p => p.Name == baseProp.Name);

                    if (!hasProperty)
                    {
                        // Add base class-compatible property signature
                        var isStatic = baseProp.GetMethod?.IsStatic ?? baseProp.SetMethod?.IsStatic ?? false;

                        properties.Add(new TypeInfo.PropertyInfo(
                            baseProp.Name,
                            basePropertyType,
                            !baseProp.CanWrite,
                            isStatic));
                    }
                }
                catch
                {
                    // Skip properties that can't be processed
                }
            }

            // Recurse up the inheritance chain
            if (baseType.BaseType != null)
            {
                AddBaseClassOverloadsRecursive(derivedType, baseType.BaseType, properties, methods, typeMapper, shouldIncludeMember, processParameter, typeReferencesAnyTypeParam, trackTypeDependency);
            }
        }
        catch
        {
            // Base type may not be accessible
        }
    }

    /// <summary>
    /// Checks if two parameter lists have matching types.
    /// </summary>
    public static bool ParameterListsMatch(IReadOnlyList<TypeInfo.ParameterInfo> params1, IReadOnlyList<TypeInfo.ParameterInfo> params2)
    {
        if (params1.Count != params2.Count) return false;

        for (int i = 0; i < params1.Count; i++)
        {
            if (params1[i].Type != params2[i].Type) return false;
        }

        return true;
    }
}
