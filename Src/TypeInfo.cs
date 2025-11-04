namespace GenerateDts;

public sealed record ProcessedAssembly(
    IReadOnlyList<NamespaceInfo> Namespaces,
    IReadOnlyList<string> Warnings);

public sealed record NamespaceInfo(
    string Name,
    IReadOnlyList<TypeDeclaration> Types);

public abstract record TypeDeclaration(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters);

public sealed record ClassDeclaration(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters,
    string? BaseType,
    IReadOnlyList<string> Interfaces,
    IReadOnlyList<TypeInfo.ConstructorInfo> Constructors,
    IReadOnlyList<TypeInfo.PropertyInfo> Properties,
    IReadOnlyList<TypeInfo.MethodInfo> Methods,
    bool IsStatic) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);

public sealed record InterfaceDeclaration(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> Extends,
    IReadOnlyList<TypeInfo.PropertyInfo> Properties,
    IReadOnlyList<TypeInfo.MethodInfo> Methods,
    bool IsDiamondBase = false) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);

public sealed record IntersectionTypeAlias(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> IntersectedTypes) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);

public sealed record EnumDeclaration(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<EnumMember> Members) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);

public sealed record EnumMember(
    string Name,
    object Value);

public sealed record StaticNamespaceDeclaration(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<TypeInfo.PropertyInfo> Properties,
    IReadOnlyList<TypeInfo.MethodInfo> Methods) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);

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
