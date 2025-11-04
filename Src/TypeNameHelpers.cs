namespace GenerateDts;

/// <summary>
/// Static helpers for escaping and formatting type and parameter names.
/// </summary>
public static class TypeNameHelpers
{
    /// <summary>
    /// TypeScript/JavaScript reserved keywords and special identifiers.
    /// </summary>
    private static readonly HashSet<string> TypeScriptReservedKeywords = new(StringComparer.Ordinal)
    {
        // Keywords
        "break", "case", "catch", "class", "const", "continue", "debugger",
        "default", "delete", "do", "else", "enum", "export", "extends",
        "false", "finally", "for", "function", "if", "import", "in",
        "instanceof", "new", "null", "return", "super", "switch", "this",
        "throw", "true", "try", "typeof", "var", "void", "while", "with",

        // Strict / future reserved
        "implements", "interface", "let", "package", "private", "protected",
        "public", "static", "yield", "async", "await",

        // Problematic identifiers
        "arguments", "eval"
    };

    /// <summary>
    /// Prefixes parameter names that conflict with TypeScript keywords.
    /// </summary>
    public static string EscapeParameterName(string name)
    {
        return TypeScriptReservedKeywords.Contains(name)
            ? $"_{name}"
            : name;
    }

    public static string GetTypeName(Type type)
    {
        var baseName = type.Name;
        var arity = 0;

        // Handle generic types - extract arity and strip the `N suffix
        if (type.IsGenericType)
        {
            var backtickIndex = baseName.IndexOf('`');
            if (backtickIndex > 0)
            {
                // Extract arity (e.g., "Tuple`3" -> arity = 3)
                if (int.TryParse(baseName.Substring(backtickIndex + 1), out var parsedArity))
                {
                    arity = parsedArity;
                }
                baseName = baseName.Substring(0, backtickIndex);
            }
        }

        // Handle nested types - build full ancestry chain to avoid conflicts
        // For deeply nested types like Dictionary<K,V>.KeyCollection.Enumerator,
        // we need to include the top-level type's arity to distinguish from other variants
        if (type.IsNested && type.DeclaringType != null)
        {
            // Walk up the nesting chain to find the top-level type
            var ancestorChain = new List<(string name, int arity)>();
            var current = type.DeclaringType;

            while (current != null)
            {
                var ancestorName = current.Name;
                var ancestorArity = 0;

                var backtickIndex = ancestorName.IndexOf('`');
                if (backtickIndex > 0)
                {
                    if (int.TryParse(ancestorName.Substring(backtickIndex + 1), out var parsedArity))
                    {
                        ancestorArity = parsedArity;
                    }
                    ancestorName = ancestorName.Substring(0, backtickIndex);
                }

                ancestorChain.Insert(0, (ancestorName, ancestorArity));
                current = current.DeclaringType;
            }

            // Build name from ancestor chain
            var nameBuilder = new System.Text.StringBuilder();
            foreach (var (ancestorName, ancestorArity) in ancestorChain)
            {
                if (nameBuilder.Length > 0)
                {
                    nameBuilder.Append('_');
                }

                nameBuilder.Append(ancestorName);
                if (ancestorArity > 0)
                {
                    nameBuilder.Append('_');
                    nameBuilder.Append(ancestorArity);
                }
            }

            // Append the current type
            nameBuilder.Append('_');
            nameBuilder.Append(baseName);
            if (arity > 0)
            {
                nameBuilder.Append('_');
                nameBuilder.Append(arity);
            }

            return nameBuilder.ToString();
        }

        // For top-level generic types, include arity to distinguish Tuple<T1> from Tuple<T1,T2>
        // Example: Tuple`1 becomes Tuple_1, Tuple`2 becomes Tuple_2
        if (arity > 0)
        {
            return $"{baseName}_{arity}";
        }

        return baseName;
    }
}
