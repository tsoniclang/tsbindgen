using System.Text;
using GenerateDts.Config;

namespace GenerateDts.Analysis;

/// <summary>
/// Applies naming transformations to CLR identifiers for TypeScript output.
/// </summary>
public static class NameTransform
{
    /// <summary>
    /// Applies the specified naming transform to an identifier.
    /// </summary>
    /// <param name="original">The original CLR identifier</param>
    /// <param name="option">The transformation to apply</param>
    /// <returns>The transformed identifier</returns>
    public static string Apply(string original, NameTransformOption option)
    {
        if (string.IsNullOrEmpty(original))
        {
            return original;
        }

        return option switch
        {
            NameTransformOption.None => original,
            NameTransformOption.CamelCase => ToCamelCase(original),
            _ => original
        };
    }

    /// <summary>
    /// Determines if a transform requires a binding manifest entry.
    /// </summary>
    /// <param name="option">The transformation option</param>
    /// <returns>True if binding entries should be created</returns>
    public static bool NeedsBindingEntry(NameTransformOption option)
    {
        return option != NameTransformOption.None;
    }

    /// <summary>
    /// Converts an identifier to camelCase.
    /// </summary>
    /// <param name="identifier">The identifier to convert</param>
    /// <returns>The camelCase version</returns>
    private static string ToCamelCase(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
        {
            return identifier;
        }

        // If first character is already lowercase, return as-is
        if (char.IsLower(identifier[0]))
        {
            return identifier;
        }

        // Handle single character
        if (identifier.Length == 1)
        {
            return identifier.ToLowerInvariant();
        }

        // Handle acronyms: "XMLParser" → "xmlParser", "IO" → "io"
        // Find the first lowercase letter or end of string
        int firstLowerIndex = -1;
        for (int i = 1; i < identifier.Length; i++)
        {
            if (char.IsLower(identifier[i]))
            {
                firstLowerIndex = i;
                break;
            }
        }

        if (firstLowerIndex == -1)
        {
            // All uppercase (acronym): "XML" → "xml"
            return identifier.ToLowerInvariant();
        }

        if (firstLowerIndex == 1)
        {
            // Normal PascalCase: "SelectMany" → "selectMany"
            return char.ToLowerInvariant(identifier[0]) + identifier.Substring(1);
        }

        // Acronym followed by PascalCase: "XMLParser" → "xmlParser"
        // Lowercase everything except the last capital before the lowercase
        var sb = new StringBuilder();
        for (int i = 0; i < firstLowerIndex - 1; i++)
        {
            sb.Append(char.ToLowerInvariant(identifier[i]));
        }
        sb.Append(identifier.Substring(firstLowerIndex - 1));
        return sb.ToString();
    }
}
