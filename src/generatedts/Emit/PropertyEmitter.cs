using System.Reflection;
using GenerateDts.Mapping;
using GenerateDts.Metadata;
using GenerateDts.Model;
using TypeInfo = GenerateDts.Model.TypeInfo;

namespace GenerateDts.Emit;

public static class PropertyEmitter
{
    public static TypeInfo.PropertyInfo? ProcessProperty(
        System.Reflection.PropertyInfo prop,
        TypeMapper typeMapper,
        Func<Type, string> applyCovariantWrapper,
        Action<Type> trackTypeDependency,
        Func<System.Reflection.PropertyInfo, bool> isRedundantPropertyRedeclaration)
    {
        // Skip indexers
        var indexParams = prop.GetIndexParameters();
        if (indexParams.Length > 0)
        {
            var location = prop.DeclaringType?.FullName ?? prop.DeclaringType?.Name ?? "Unknown";
            typeMapper.AddWarning($"[{location}.{prop.Name}] Skipped indexer - " +
                $"indexers with parameters cannot be represented as TypeScript properties (TS2300)");
            return null;
        }

        var isStatic = prop.GetMethod?.IsStatic ?? prop.SetMethod?.IsStatic ?? false;

        // Skip redundant property redeclarations
        if (!isStatic && isRedundantPropertyRedeclaration(prop))
        {
            return null;
        }

        // Skip static properties in generic classes that reference type parameters
        if (isStatic && prop.DeclaringType != null && prop.DeclaringType.IsGenericType)
        {
            var classTypeParams = prop.DeclaringType.GetGenericArguments().Select(t => t.Name).ToHashSet();
            if (PropertyTypeReferencesTypeParams(prop.PropertyType, classTypeParams))
            {
                var location = prop.DeclaringType.FullName ?? prop.DeclaringType.Name;
                typeMapper.AddWarning($"[{location}.{prop.Name}] Skipped static property - " +
                    $"references class type parameters (TS2302: Static members cannot reference class type parameters)");
                return null;
            }
        }

        trackTypeDependency(prop.PropertyType);

        var mappedType = typeMapper.MapType(prop.PropertyType);
        var propertyType = applyCovariantWrapper(prop.PropertyType);

        return new TypeInfo.PropertyInfo(
            prop.Name,
            propertyType,
            !prop.CanWrite,
            isStatic);
    }

    public static MemberMetadata ProcessPropertyMetadata(
        System.Reflection.PropertyInfo prop,
        Func<MethodInfo?, bool> isOverrideMethod,
        Func<MethodBase?, string> getAccessibility)
    {
        var getter = prop.GetMethod;
        var setter = prop.SetMethod;
        var accessMethod = getter ?? setter;

        bool isVirtual = accessMethod?.IsVirtual == true && !accessMethod.IsFinal;
        bool isAbstract = accessMethod?.IsAbstract == true;
        bool isSealed = accessMethod?.IsFinal == true && accessMethod.IsVirtual;
        bool isOverride = isOverrideMethod(accessMethod);
        bool isStatic = accessMethod?.IsStatic ?? false;

        bool isIndexer = prop.GetIndexParameters().Length > 0;

        return new MemberMetadata(
            "property",
            isVirtual,
            isAbstract,
            isSealed,
            isOverride,
            isStatic,
            getAccessibility(accessMethod),
            IsIndexer: isIndexer ? true : null);
    }

    private static bool PropertyTypeReferencesTypeParams(Type propertyType, HashSet<string> classTypeParams)
    {
        if (propertyType.IsGenericParameter && classTypeParams.Contains(propertyType.Name))
        {
            return true;
        }

        if (propertyType.IsGenericType)
        {
            var typeArgs = propertyType.GetGenericArguments();
            foreach (var arg in typeArgs)
            {
                if (PropertyTypeReferencesTypeParams(arg, classTypeParams))
                {
                    return true;
                }
            }
        }

        if (propertyType.IsArray)
        {
            return PropertyTypeReferencesTypeParams(propertyType.GetElementType()!, classTypeParams);
        }

        return false;
    }

    public static string ApplyCovariantWrapperIfNeeded(
        System.Reflection.PropertyInfo prop,
        string mappedType,
        TypeMapper typeMapper,
        Action<Type> trackTypeDependency,
        Func<Type, Type, bool> hasAnyExplicitImplementation)
    {
        var declaringType = prop.DeclaringType;
        if (declaringType == null)
            return mappedType;

        // Check base class for property hiding
        var baseType = declaringType.BaseType;
        while (baseType != null &&
               baseType.FullName != "System.Object" &&
               baseType.FullName != "System.ValueType" &&
               baseType.FullName != "System.MarshalByRefObject")
        {
            var baseProp = baseType.GetProperty(prop.Name, BindingFlags.Public | BindingFlags.Instance);
            if (baseProp != null)
            {
                var baseTypeName = typeMapper.MapType(baseProp.PropertyType);
                var derivedTypeName = typeMapper.MapType(prop.PropertyType);

                if (baseTypeName != derivedTypeName)
                {
                    trackTypeDependency(prop.PropertyType);
                    trackTypeDependency(baseProp.PropertyType);

                    // Skip Covariant wrapper for enum-to-enum covariance (TypeScript doesn't handle it well)
                    // Use base type instead to avoid TS2416 errors
                    if (prop.PropertyType.IsEnum && baseProp.PropertyType.IsEnum)
                    {
                        var location = declaringType.FullName ?? declaringType.Name;
                        typeMapper.AddWarning($"[{location}.{prop.Name}] Enum covariance detected - " +
                            $"returns {prop.PropertyType.FullName ?? prop.PropertyType.Name} " +
                            $"(base expects {baseProp.PropertyType.FullName ?? baseProp.PropertyType.Name}). " +
                            $"Using base type to avoid TypeScript error (TS2416)");
                        return baseTypeName;
                    }

                    return $"Covariant<{derivedTypeName}, {baseTypeName}>";
                }
                break;
            }
            baseType = baseType.BaseType;
        }

        // Only apply interface covariance checks to readonly properties
        if (prop.CanWrite)
            return mappedType;

        foreach (var interfaceType in declaringType.GetInterfaces())
        {
            if (!interfaceType.IsPublic)
                continue;

            if (hasAnyExplicitImplementation(declaringType, interfaceType))
                continue;

            var interfaceProperty = interfaceType.GetProperty(prop.Name);
            if (interfaceProperty == null)
                continue;

            var contractType = typeMapper.MapType(interfaceProperty.PropertyType);
            var specificType = typeMapper.MapType(prop.PropertyType);

            if (contractType != specificType)
            {
                trackTypeDependency(prop.PropertyType);
                trackTypeDependency(interfaceProperty.PropertyType);
                return $"Covariant<{specificType}, {contractType}>";
            }
        }

        return mappedType;
    }

    public static bool IsRedundantPropertyRedeclaration(
        System.Reflection.PropertyInfo prop,
        TypeMapper typeMapper)
    {
        var declaringType = prop.DeclaringType;
        if (declaringType == null)
            return false;

        try
        {
            var derivedMapped = typeMapper.MapType(prop.PropertyType);

            var currentBase = declaringType.BaseType;
            while (currentBase != null &&
                   currentBase.FullName != "System.Object" &&
                   currentBase.FullName != "System.ValueType" &&
                   currentBase.FullName != "System.MarshalByRefObject")
            {
                var ancestorProperty = currentBase.GetProperty(prop.Name,
                    BindingFlags.Public | BindingFlags.Instance);
                if (ancestorProperty != null)
                {
                    var ancestorMapped = typeMapper.MapType(ancestorProperty.PropertyType);

                    if (derivedMapped == ancestorMapped)
                    {
                        return true;
                    }

                    return false;
                }

                currentBase = currentBase.BaseType;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
