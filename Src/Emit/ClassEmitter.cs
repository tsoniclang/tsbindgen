using System.Reflection;
using GenerateDts.Mapping;
using GenerateDts.Model;
using TypeInfo = GenerateDts.Model.TypeInfo;

namespace GenerateDts.Emit;

public static class ClassEmitter
{
    public static ClassDeclaration ProcessClass(
        Type type,
        Func<System.Reflection.ConstructorInfo, TypeInfo.ConstructorInfo> processConstructor,
        Func<System.Reflection.PropertyInfo, TypeInfo.PropertyInfo?> processProperty,
        Func<System.Reflection.MethodInfo, Type, TypeInfo.MethodInfo?> processMethod,
        Func<MemberInfo, bool> shouldIncludeMember,
        Func<Type, List<(Type interfaceType, System.Reflection.MethodInfo interfaceMethod, System.Reflection.MethodInfo implementation)>> getExplicitInterfaceImplementations,
        Func<Type, Type, bool> hasAnyExplicitImplementation,
        Func<Type, (bool hasDiamond, List<Type> ancestors)> hasDiamondInheritance,
        Action<Type, List<TypeInfo.PropertyInfo>, List<TypeInfo.MethodInfo>> addInterfaceCompatibleOverloads,
        Action<Type, List<TypeInfo.PropertyInfo>, List<TypeInfo.MethodInfo>> addBaseClassCompatibleOverloads,
        Action<Type> trackTypeDependency,
        TypeMapper typeMapper,
        Func<Type, string> getTypeName)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Select(processConstructor)
            .ToList();

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MemberInfo>()
            .Where(shouldIncludeMember)
            .Cast<System.Reflection.PropertyInfo>()
            .Select(processProperty)
            .Where(p => p != null)
            .ToList();

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MemberInfo>()
            .Where(shouldIncludeMember)
            .Cast<System.Reflection.MethodInfo>()
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.Name.Contains('.')) // Skip explicit interface implementations early
            .Select(m => processMethod(m, type))
            .OfType<TypeInfo.MethodInfo>() // Filter nulls and cast to non-nullable
            .ToList();

        // Add public wrappers for explicit interface implementations
        // These won't appear in TypeScript implements clause but are needed for metadata
        var explicitImplementations = getExplicitInterfaceImplementations(type);
        foreach (var (interfaceType, interfaceMethod, implementation) in explicitImplementations)
        {
            // All explicit implementations are kept for metadata, no filtering needed here

            // Check if this is a property getter/setter (special name)
            if (interfaceMethod.IsSpecialName && interfaceMethod.Name.StartsWith("get_"))
            {
                // Property getter - emit as readonly property
                var propName = interfaceMethod.Name.Substring(4); // Remove "get_"
                var propType = typeMapper.MapType(interfaceMethod.ReturnType);

                // Check if we already have this property (from public implementation)
                if (!properties.Any(p => p.Name == propName))
                {
                    // Check if base class has this property with different type - apply Covariant wrapper
                    var currentBase = type.BaseType;
                    string finalPropType = propType;

                    while (currentBase != null &&
                           currentBase.FullName != "System.Object" &&
                           currentBase.FullName != "System.ValueType" &&
                           currentBase.FullName != "System.MarshalByRefObject")
                    {
                        try
                        {
                            var baseProp = currentBase.GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                            if (baseProp != null)
                            {
                                var baseTypeName = typeMapper.MapType(baseProp.PropertyType);
                                if (baseTypeName != propType)
                                {
                                    // Base class has different type - wrap with Covariant
                                    // Covariant<TSpecific, TContract> where TSpecific is more specific than TContract
                                    // baseTypeName is from base class (more specific), propType is from interface (contract)
                                    trackTypeDependency(interfaceMethod.ReturnType);
                                    trackTypeDependency(baseProp.PropertyType);
                                    finalPropType = $"Covariant<{baseTypeName}, {propType}>";
                                }
                                break;
                            }
                        }
                        catch (System.Reflection.AmbiguousMatchException)
                        {
                            // Multiple properties with same name (indexers) - skip Covariant wrapper check
                            // The property will be added with its interface type
                            break;
                        }
                        currentBase = currentBase.BaseType;
                    }

                    properties.Add(new TypeInfo.PropertyInfo(propName, finalPropType, true, false));
                }
            }
            else if (interfaceMethod.IsSpecialName && interfaceMethod.Name.StartsWith("set_"))
            {
                // Property setter - usually handled with getter, skip
                continue;
            }
            else if (!interfaceMethod.IsSpecialName)
            {
                // Regular method - emit as public method
                // Check if we already have this method (from public implementation)
                if (!methods.Any(m => m.Name == interfaceMethod.Name))
                {
                    var processedMethod = processMethod(interfaceMethod, type);
                    if (processedMethod != null)
                    {
                        methods.Add(processedMethod);
                    }
                }
            }
        }

        // Add interface-compatible overloads for all interface members
        // This handles TS2416 (covariant return types) and remaining TS2420 (interface implementation)
        addInterfaceCompatibleOverloads(type, properties, methods);

        // Add base class-compatible overloads for all base class members
        // This handles TS2416 (method covariance) when derived classes override with more specific types
        addBaseClassCompatibleOverloads(type, properties, methods);

        // Use name-based comparison for MetadataLoadContext compatibility
        // (typeof(object) returns runtime type, but type.BaseType returns MetadataLoadContext type)
        var baseType = type.BaseType != null
            && type.BaseType.FullName != "System.Object"
            && type.BaseType.FullName != "System.ValueType"
            ? typeMapper.MapType(type.BaseType)
            : null;

        // Track base type dependency
        if (type.BaseType != null
            && type.BaseType.FullName != "System.Object"
            && type.BaseType.FullName != "System.ValueType")
        {
            trackTypeDependency(type.BaseType);
        }

        // Filter interfaces for TypeScript implements clause
        // General rule: only include interfaces where ALL members are publicly implemented
        // Phase 1E: If interface has diamond, use _Base variant in implements clause
        var interfaces = type.GetInterfaces()
            .Where(i => i.IsPublic)
            .Where(i => !hasAnyExplicitImplementation(type, i)) // Skip explicitly implemented interfaces
            .Select(i =>
            {
                // Phase 1E: Use _Base variant for diamond interfaces
                var (hasDiamond, _) = hasDiamondInheritance(i);
                if (i.IsInterface && hasDiamond)
                {
                    var mapped = typeMapper.MapType(i);

                    // Insert "_Base" before generic parameters or at end
                    // Preserves full module path: Module.IFoo_1<T> -> Module.IFoo_1_Base<T>
                    if (i.IsGenericType)
                    {
                        var genericStart = mapped.IndexOf('<');
                        if (genericStart > 0)
                        {
                            return mapped.Substring(0, genericStart) + "_Base" + mapped.Substring(genericStart);
                        }
                    }
                    return mapped + "_Base";
                }
                return typeMapper.MapType(i);
            })
            .Distinct() // Remove duplicates
            .ToList();

        // Track interface dependencies
        foreach (var iface in type.GetInterfaces().Where(i => i.IsPublic))
        {
            trackTypeDependency(iface);
        }

        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(t => t.Name).ToList()
            : new List<string>();

        // Phase 2: Detect but don't split statics yet (companion namespace approach makes TS2417 worse)
        // TODO: Need different approach for classes in inheritance hierarchies
        CompanionNamespace? companion = null;

        return new ClassDeclaration(
            getTypeName(type),
            type.FullName!,
            type.IsGenericType,
            genericParams,
            baseType,
            interfaces,
            constructors,
            properties,
            methods,
            type.IsAbstract && type.IsSealed, // Static class
            companion);
    }
}
