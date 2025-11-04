using GenerateDts.Model;
using GenerateDts.Analysis;

namespace GenerateDts.Reflection;

/// <summary>
/// Dispatches type processing to appropriate emitter based on type kind.
/// </summary>
public static class TypeProcessing
{
    public static TypeDeclaration? ProcessType(
        Type type,
        Func<Type, EnumDeclaration> processEnum,
        Func<Type, InterfaceDeclaration> processInterface,
        Func<Type, StaticNamespaceDeclaration> processStaticNamespace,
        Func<Type, ClassDeclaration> processClass)
    {
        if (type.IsEnum)
        {
            return processEnum(type);
        }
        else if (type.IsInterface)
        {
            return processInterface(type);
        }
        else if (type.IsClass || type.IsValueType)
        {
            // Skip delegate types - they're mapped to function types in TypeMapper
            if (TypeFilters.IsDelegate(type))
            {
                return null;
            }

            // Check if this is a static-only type
            if (TypeFilters.IsStaticOnly(type))
            {
                return processStaticNamespace(type);
            }
            return processClass(type);
        }

        return null;
    }
}
