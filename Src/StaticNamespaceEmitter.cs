using System.Reflection;

namespace GenerateDts;

/// <summary>
/// Processes static-only types (static classes and types with only static members)
/// and converts them to TypeScript namespace declarations.
/// </summary>
public static class StaticNamespaceEmitter
{
    public static StaticNamespaceDeclaration ProcessStaticNamespace(
        Type type,
        Func<Type, string> getTypeName,
        Func<MemberInfo, bool> shouldIncludeMember,
        Func<System.Reflection.PropertyInfo, TypeInfo.PropertyInfo> processProperty,
        Func<System.Reflection.MethodInfo, Type, TypeInfo.MethodInfo?> processMethod)
    {
        // For static-only types, only process static members
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MemberInfo>()
            .Where(shouldIncludeMember)
            .Cast<System.Reflection.PropertyInfo>()
            .Select(processProperty)
            .Where(p => p != null)
            .ToList();

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MemberInfo>()
            .Where(shouldIncludeMember)
            .Cast<System.Reflection.MethodInfo>()
            .Where(m => m.IsSpecialName == false)
            .Where(m => !m.Name.Contains('.')) // Skip explicit interface implementations early
            .Select(m => processMethod(m, type))
            .OfType<TypeInfo.MethodInfo>() // Filter nulls and cast to non-nullable
            .ToList();

        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(t => t.Name).ToList()
            : new List<string>();

        return new StaticNamespaceDeclaration(
            getTypeName(type),
            type.FullName!,
            type.IsGenericType,
            genericParams,
            properties,
            methods);
    }
}
