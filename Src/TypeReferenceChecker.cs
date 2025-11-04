namespace GenerateDts;

public static class TypeReferenceChecker
{
    public static bool PropertyTypeReferencesTypeParams(Type propertyType, HashSet<string> classTypeParams)
    {
        // Check if this type is a generic parameter
        if (propertyType.IsGenericParameter && classTypeParams.Contains(propertyType.Name))
        {
            return true;
        }

        // Check if this is a generic type that uses the class's type parameters
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

        // Check arrays
        if (propertyType.IsArray)
        {
            return PropertyTypeReferencesTypeParams(propertyType.GetElementType()!, classTypeParams);
        }

        return false;
    }

    public static bool TypeReferencesAnyTypeParam(Type type, HashSet<Type> typeParams)
    {
        // Check if this type IS a type parameter
        if (type.IsGenericParameter && typeParams.Contains(type))
        {
            return true;
        }

        // Check if this is a generic type that uses any of the type parameters
        if (type.IsGenericType)
        {
            var typeArgs = type.GetGenericArguments();
            foreach (var arg in typeArgs)
            {
                if (TypeReferencesAnyTypeParam(arg, typeParams))
                {
                    return true;
                }
            }
        }

        // Check arrays
        if (type.IsArray)
        {
            return TypeReferencesAnyTypeParam(type.GetElementType()!, typeParams);
        }

        return false;
    }
}
