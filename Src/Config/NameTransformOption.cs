namespace GenerateDts.Config;

/// <summary>
/// Defines the naming convention to apply when transforming CLR identifiers to TypeScript.
/// </summary>
public enum NameTransformOption
{
    /// <summary>
    /// No transformation - use original CLR names.
    /// </summary>
    None = 0,

    /// <summary>
    /// Transform to camelCase (first letter lowercase, subsequent words capitalized).
    /// Example: "SelectMany" → "selectMany"
    /// </summary>
    CamelCase = 1,

    // Future: PascalCase, snake_case, custom maps
}
