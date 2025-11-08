using tsbindgen.Snapshot;

namespace tsbindgen.Config;

/// <summary>
/// Generates consistent interface lookup keys for StructuralConformance.
/// Ensures the same key is produced whether we're building the lookup from TypeModel
/// or querying it from TypeReference.
/// </summary>
public static class InterfaceKey
{
    /// <summary>
    /// Creates a canonical key from a TypeReference.
    /// Format: "{Namespace}.{TypeName}"
    /// </summary>
    public static string FromTypeReference(TypeReference typeRef)
    {
        return $"{typeRef.Namespace}.{typeRef.TypeName}";
    }

    /// <summary>
    /// Creates a canonical key from namespace name and type CLR name.
    /// Format: "{Namespace}.{TypeName}"
    /// Used when building the global interface index.
    /// </summary>
    public static string FromNames(string namespaceName, string typeName)
    {
        return $"{namespaceName}.{typeName}";
    }
}
