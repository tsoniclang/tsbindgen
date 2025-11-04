using System.Reflection;
using GenerateDts.Model;

namespace GenerateDts.Metadata;

public static class MetadataProcessor
{
    public static TypeMetadata? ProcessTypeMetadata(
        Type type,
        SignatureFormatter signatureFormatter,
        Func<MemberInfo, bool> shouldIncludeMember)
    {
        // Determine the kind of type
        string kind;
        if (type.IsEnum)
        {
            kind = "enum";
        }
        else if (type.IsInterface)
        {
            kind = "interface";
        }
        else if (type.IsValueType)
        {
            kind = "struct";
        }
        else
        {
            kind = "class";
        }

        // Get type-level flags
        bool isAbstract = type.IsAbstract && !type.IsInterface && !type.IsSealed;
        bool isSealed = type.IsSealed && !type.IsValueType && !type.IsEnum;
        bool isStatic = type.IsAbstract && type.IsSealed && type.IsClass;

        // Get base type (if any)
        string? baseType = null;
        if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
        {
            baseType = type.BaseType.FullName?.Replace('+', '.');
        }

        // Get interfaces
        var interfaces = type.GetInterfaces()
            .Where(i => i.IsPublic)
            .Select(i => i.FullName?.Replace('+', '.') ?? i.Name)
            .ToList();

        // Process members
        var memberMetadataDict = new Dictionary<string, MemberMetadata>();

        // Process constructors (skip for interfaces and enums)
        if (!type.IsInterface && !type.IsEnum)
        {
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            foreach (var ctor in constructors)
            {
                var signature = signatureFormatter.FormatConstructor(ctor);
                var metadata = ProcessConstructorMetadata(ctor);
                memberMetadataDict[signature] = metadata;
            }
        }

        // Process properties
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MemberInfo>()
            .Where(shouldIncludeMember)
            .Cast<System.Reflection.PropertyInfo>();

        foreach (var prop in properties)
        {
            var signature = signatureFormatter.FormatProperty(prop);
            var metadata = ProcessPropertyMetadata(prop);
            memberMetadataDict[signature] = metadata;
        }

        // Process methods (skip special methods like property getters/setters)
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Cast<MemberInfo>()
            .Where(shouldIncludeMember)
            .Cast<System.Reflection.MethodInfo>()
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.Name.Contains('.')); // Skip explicit interface implementations

        foreach (var method in methods)
        {
            var signature = signatureFormatter.FormatMethod(method);
            var metadata = ProcessMethodMetadata(method);
            memberMetadataDict[signature] = metadata;
        }

        return new TypeMetadata(
            kind,
            isAbstract,
            isSealed,
            isStatic,
            baseType,
            interfaces,
            memberMetadataDict);
    }

    public static MemberMetadata ProcessConstructorMetadata(System.Reflection.ConstructorInfo ctor)
    {
        return new MemberMetadata(
            "constructor",
            IsVirtual: false,
            IsAbstract: false,
            IsSealed: false,
            IsOverride: false,
            IsStatic: false,
            Accessibility: GetAccessibility(ctor));
    }

    public static MemberMetadata ProcessPropertyMetadata(System.Reflection.PropertyInfo prop)
    {
        // For properties, check the getter method for virtual/override information
        var getter = prop.GetMethod;
        var setter = prop.SetMethod;
        var accessMethod = getter ?? setter;

        bool isVirtual = accessMethod?.IsVirtual == true && !accessMethod.IsFinal;
        bool isAbstract = accessMethod?.IsAbstract == true;
        bool isSealed = accessMethod?.IsFinal == true && accessMethod.IsVirtual;
        bool isOverride = IsOverrideMethod(accessMethod);
        bool isStatic = accessMethod?.IsStatic ?? false;

        // Check if this is an indexer (has index parameters)
        bool isIndexer = prop.GetIndexParameters().Length > 0;

        return new MemberMetadata(
            "property",
            isVirtual,
            isAbstract,
            isSealed,
            isOverride,
            isStatic,
            GetAccessibility(accessMethod),
            IsIndexer: isIndexer ? true : null);
    }

    public static MemberMetadata ProcessMethodMetadata(System.Reflection.MethodInfo method)
    {
        bool isVirtual = method.IsVirtual && !method.IsFinal;
        bool isAbstract = method.IsAbstract;
        bool isSealed = method.IsFinal && method.IsVirtual;
        bool isOverride = IsOverrideMethod(method);
        bool isStatic = method.IsStatic;

        return new MemberMetadata(
            "method",
            isVirtual,
            isAbstract,
            isSealed,
            isOverride,
            isStatic,
            GetAccessibility(method));
    }

    public static bool IsOverrideMethod(MethodInfo? method)
    {
        if (method == null || !method.IsVirtual)
        {
            return false;
        }

        // A method is an override if its base definition is different from itself
        var baseDefinition = method.GetBaseDefinition();
        return baseDefinition != method;
    }

    public static string GetAccessibility(MethodBase? method)
    {
        if (method == null) return "public";

        if (method.IsPublic) return "public";
        if (method.IsFamily) return "protected";
        if (method.IsFamilyOrAssembly) return "protected internal";
        if (method.IsFamilyAndAssembly) return "private protected";
        if (method.IsAssembly) return "internal";
        if (method.IsPrivate) return "private";

        return "public";
    }
}
