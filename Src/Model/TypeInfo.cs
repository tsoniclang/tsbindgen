namespace GenerateDts.Model;

/// <summary>
/// Contains record types for class/interface/enum members (constructors, properties, methods, parameters).
/// </summary>
public static class TypeInfo
{
    public sealed record ConstructorInfo(
        IReadOnlyList<ParameterInfo> Parameters);

    public sealed record PropertyInfo(
        string Name,
        string Type,
        bool IsReadOnly,
        bool IsStatic);

    public sealed record MethodInfo(
        string Name,
        string ReturnType,
        IReadOnlyList<ParameterInfo> Parameters,
        bool IsStatic,
        bool IsGeneric,
        IReadOnlyList<string> GenericParameters);

    public sealed record ParameterInfo(
        string Name,
        string Type,
        bool IsOptional,
        bool IsParams);
}
