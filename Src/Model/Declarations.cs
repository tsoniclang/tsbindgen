namespace GenerateDts.Model;

/// <summary>
/// Represents the processed output of an assembly with all namespaces and types.
/// </summary>
public sealed record ProcessedAssembly(
    IReadOnlyList<NamespaceInfo> Namespaces,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Represents a namespace containing type declarations.
/// </summary>
public sealed record NamespaceInfo(
    string Name,
    IReadOnlyList<TypeDeclaration> Types);

/// <summary>
/// Base record for all type declarations (classes, interfaces, enums, etc.).
/// </summary>
public abstract record TypeDeclaration(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters);

/// <summary>
/// Represents a class declaration with constructors, properties, methods, and optional companion namespace.
/// </summary>
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
    bool IsStatic,
    CompanionNamespace? Companion = null) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);

/// <summary>
/// Represents a companion namespace for static members on a class.
/// </summary>
public sealed record CompanionNamespace(
    IReadOnlyList<TypeInfo.PropertyInfo> Properties,
    IReadOnlyList<TypeInfo.MethodInfo> Methods);

/// <summary>
/// Represents an interface declaration with properties and methods.
/// </summary>
public sealed record InterfaceDeclaration(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> Extends,
    IReadOnlyList<TypeInfo.PropertyInfo> Properties,
    IReadOnlyList<TypeInfo.MethodInfo> Methods,
    bool IsDiamondBase = false) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);

/// <summary>
/// Represents an intersection type alias used for diamond inheritance scenarios.
/// </summary>
public sealed record IntersectionTypeAlias(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<string> IntersectedTypes) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);

/// <summary>
/// Represents an enum declaration with its members.
/// </summary>
public sealed record EnumDeclaration(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<EnumMember> Members) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);

/// <summary>
/// Represents a single enum member with its name and value.
/// </summary>
public sealed record EnumMember(
    string Name,
    object Value);

/// <summary>
/// Represents a static-only class emitted as a TypeScript namespace.
/// </summary>
public sealed record StaticNamespaceDeclaration(
    string Name,
    string FullName,
    bool IsGeneric,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<TypeInfo.PropertyInfo> Properties,
    IReadOnlyList<TypeInfo.MethodInfo> Methods) : TypeDeclaration(Name, FullName, IsGeneric, GenericParameters);
