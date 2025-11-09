namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Plans module specifiers for TypeScript imports.
/// Generates relative paths based on source/target namespaces and emission area.
/// Handles root namespace (_root) and nested namespace directories.
/// </summary>
public static class PathPlanner
{
    /// <summary>
    /// Gets the module specifier for importing from targetNamespace into sourceNamespace.
    /// Returns a relative path string suitable for TypeScript import statements.
    /// </summary>
    /// <param name="sourceNamespace">The namespace doing the importing (empty string for root)</param>
    /// <param name="targetNamespace">The namespace being imported from (empty string for root)</param>
    /// <returns>Relative module path (e.g., "../System/internal/index")</returns>
    public static string GetSpecifier(string sourceNamespace, string targetNamespace)
    {
        var isSourceRoot = string.IsNullOrEmpty(sourceNamespace);
        var isTargetRoot = string.IsNullOrEmpty(targetNamespace);

        // Root namespace uses _root directory
        var targetDir = isTargetRoot ? "_root" : targetNamespace;
        var targetFile = isTargetRoot ? "index" : "internal/index";

        if (isSourceRoot)
        {
            // Root → Non-root: ./{targetNs}/internal/index
            // Root → Root: ./_root/index
            return isTargetRoot
                ? "./_root/index"
                : $"./{targetNamespace}/internal/index";
        }
        else
        {
            // Non-root → Non-root: ../{targetNs}/internal/index
            // Non-root → Root: ../_root/index
            return isTargetRoot
                ? "../_root/index"
                : $"../{targetNamespace}/internal/index";
        }
    }

    /// <summary>
    /// Gets the directory name for a namespace (handles root namespace).
    /// </summary>
    public static string GetNamespaceDirectory(string namespaceName)
    {
        return string.IsNullOrEmpty(namespaceName) ? "_root" : namespaceName;
    }

    /// <summary>
    /// Gets the subdirectory name for internal declarations (handles root namespace).
    /// </summary>
    public static string GetInternalSubdirectory(string namespaceName)
    {
        return string.IsNullOrEmpty(namespaceName) ? "_root" : "internal";
    }
}
