using System.Reflection;
using GenerateDts.Mapping;
using GenerateDts.Metadata;
using GenerateDts.Model;
using TypeInfo = GenerateDts.Model.TypeInfo;

namespace GenerateDts.Emit;

public static class MethodEmitter
{
    public static TypeInfo.MethodInfo? ProcessMethod(
        System.Reflection.MethodInfo method,
        Type declaringType,
        TypeMapper typeMapper,
        Func<System.Reflection.ParameterInfo, TypeInfo.ParameterInfo> processParameter,
        Action<Type> trackTypeDependency)
    {
        // Skip explicit interface implementations
        if (method.Name.Contains('.'))
        {
            typeMapper.AddWarning($"Skipped explicit interface implementation {declaringType.Name}.{method.Name} - " +
                $"method name contains dot (TS1434: Unexpected keyword or identifier)");
            return null;
        }

        trackTypeDependency(method.ReturnType);

        foreach (var param in method.GetParameters())
        {
            trackTypeDependency(param.ParameterType);
        }

        var parameters = method.GetParameters()
            .Select(processParameter)
            .ToList();

        var genericParams = new List<string>();
        var isGeneric = method.IsGenericMethod;

        // TypeScript: static methods cannot reference class type parameters
        if (method.IsStatic && declaringType.IsGenericType)
        {
            var classTypeParams = declaringType.GetGenericArguments().Select(t => t.Name).ToList();

            if (method.IsGenericMethod)
            {
                var methodTypeParams = method.GetGenericArguments().Select(t => t.Name).ToList();
                genericParams = classTypeParams.Concat(methodTypeParams).ToList();
            }
            else
            {
                genericParams = classTypeParams;
            }

            isGeneric = genericParams.Count > 0;
        }
        else if (method.IsGenericMethod)
        {
            genericParams = method.GetGenericArguments().Select(t => t.Name).ToList();
        }

        return new TypeInfo.MethodInfo(
            method.Name,
            typeMapper.MapType(method.ReturnType),
            parameters,
            method.IsStatic,
            isGeneric,
            genericParams);
    }

    public static MemberMetadata ProcessMethodMetadata(
        System.Reflection.MethodInfo method,
        Func<MethodInfo?, bool> isOverrideMethod,
        Func<MethodBase?, string> getAccessibility)
    {
        bool isVirtual = method.IsVirtual && !method.IsFinal;
        bool isAbstract = method.IsAbstract;
        bool isSealed = method.IsFinal && method.IsVirtual;
        bool isOverride = isOverrideMethod(method);
        bool isStatic = method.IsStatic;

        return new MemberMetadata(
            "method",
            isVirtual,
            isAbstract,
            isSealed,
            isOverride,
            isStatic,
            getAccessibility(method));
    }

    public static bool IsOverrideMethod(MethodInfo? method)
    {
        if (method == null || !method.IsVirtual)
        {
            return false;
        }

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

    public static TypeInfo.ParameterInfo ProcessParameter(System.Reflection.ParameterInfo param, TypeMapper typeMapper)
    {
        var isParams = param.GetCustomAttributesData()
            .Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute");

        var paramType = isParams && param.ParameterType.IsArray
            ? param.ParameterType.GetElementType()!
            : param.ParameterType;

        var originalName = param.Name ?? $"arg{param.Position}";
        var safeName = TypeNameHelpers.EscapeParameterName(originalName);

        return new TypeInfo.ParameterInfo(
            safeName,
            typeMapper.MapType(paramType),
            param.IsOptional || param.HasDefaultValue,
            isParams);
    }

    public static bool TypeReferencesAnyTypeParam(Type type, HashSet<Type> typeParams)
    {
        if (type.IsGenericParameter && typeParams.Contains(type))
        {
            return true;
        }

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

        if (type.IsArray)
        {
            return TypeReferencesAnyTypeParam(type.GetElementType()!, typeParams);
        }

        return false;
    }
}
