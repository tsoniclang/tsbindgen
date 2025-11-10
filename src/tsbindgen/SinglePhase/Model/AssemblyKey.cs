using System.Reflection;

namespace tsbindgen.SinglePhase.Model;

/// <summary>
/// Normalized assembly identity key for disambiguation.
/// Ensures consistent assembly identity across different contexts.
/// </summary>
public readonly record struct AssemblyKey(
    string Name,
    string PublicKeyToken,
    string Culture,
    string Version)
{
    /// <summary>
    /// Create AssemblyKey from AssemblyName with proper normalization.
    /// </summary>
    public static AssemblyKey From(AssemblyName asm) => new(
        asm.Name ?? "",
        asm.GetPublicKeyToken()?.ToHexString() ?? "null",
        asm.CultureName ?? "neutral",
        asm.Version?.ToString() ?? "0.0.0.0"
    );

    /// <summary>
    /// Full identity string (GAC format).
    /// Example: "System.Private.CoreLib, PublicKeyToken=7cec85d7bea7798e, Culture=neutral, Version=10.0.0.0"
    /// </summary>
    public override string ToString() =>
        $"{Name}, PublicKeyToken={PublicKeyToken}, Culture={Culture}, Version={Version}";

    /// <summary>
    /// Simple name without version/culture/token (for display purposes).
    /// </summary>
    public string SimpleName => Name;
}

/// <summary>
/// Extension methods for assembly identity operations.
/// </summary>
public static class AssemblyKeyExtensions
{
    /// <summary>
    /// Convert PublicKeyToken byte array to lowercase hex string.
    /// </summary>
    public static string ToHexString(this byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return "null";

        return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    }
}
