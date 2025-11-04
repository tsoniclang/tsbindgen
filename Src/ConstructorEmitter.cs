using System.Reflection;

namespace GenerateDts;

/// <summary>
/// Processes constructor members and converts them to TypeScript declarations.
/// </summary>
public static class ConstructorEmitter
{
    public static TypeInfo.ConstructorInfo ProcessConstructor(
        System.Reflection.ConstructorInfo ctor,
        Func<System.Reflection.ParameterInfo, TypeInfo.ParameterInfo> processParameter,
        Action<Type> trackTypeDependency)
    {
        // Track parameter type dependencies
        foreach (var param in ctor.GetParameters())
        {
            trackTypeDependency(param.ParameterType);
        }

        var parameters = ctor.GetParameters()
            .Select(processParameter)
            .ToList();

        return new TypeInfo.ConstructorInfo(parameters);
    }

    public static MemberMetadata ProcessConstructorMetadata(
        System.Reflection.ConstructorInfo ctor,
        Func<MethodBase?, string> getAccessibility)
    {
        return new MemberMetadata(
            "constructor",
            IsVirtual: false,
            IsAbstract: false,
            IsSealed: false,
            IsOverride: false,
            IsStatic: false,
            Accessibility: getAccessibility(ctor));
    }
}
