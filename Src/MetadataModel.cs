using System.Text.Json.Serialization;

namespace GenerateDts;

/// <summary>
/// Root metadata for an entire assembly.
/// </summary>
public sealed record AssemblyMetadata(
    [property: JsonPropertyName("assemblyName")] string AssemblyName,
    [property: JsonPropertyName("assemblyVersion")] string AssemblyVersion,
    [property: JsonPropertyName("types")] IReadOnlyDictionary<string, TypeMetadata> Types);

/// <summary>
/// Metadata for a type (class, struct, interface, or enum).
/// </summary>
public sealed record TypeMetadata(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("isAbstract")] bool IsAbstract,
    [property: JsonPropertyName("isSealed")] bool IsSealed,
    [property: JsonPropertyName("isStatic")] bool IsStatic,
    [property: JsonPropertyName("baseType")] string? BaseType,
    [property: JsonPropertyName("interfaces")] IReadOnlyList<string> Interfaces,
    [property: JsonPropertyName("members")] IReadOnlyDictionary<string, MemberMetadata> Members);

/// <summary>
/// Metadata for a type member (method, property, or constructor).
/// </summary>
public sealed record MemberMetadata(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("isVirtual")] bool IsVirtual,
    [property: JsonPropertyName("isAbstract")] bool IsAbstract,
    [property: JsonPropertyName("isSealed")] bool IsSealed,
    [property: JsonPropertyName("isOverride")] bool IsOverride,
    [property: JsonPropertyName("isStatic")] bool IsStatic,
    [property: JsonPropertyName("accessibility")] string Accessibility,
    [property: JsonPropertyName("isIndexer")] bool? IsIndexer = null);
