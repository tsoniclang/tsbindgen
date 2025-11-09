using System.Collections.Generic;

namespace tsbindgen.Core;

/// <summary>
/// TypeScript reserved word handling and sanitization.
/// Provides pure functions for detecting and escaping TypeScript keywords.
/// </summary>
public static class TypeScriptReservedWords
{
    private static readonly HashSet<string> ReservedWords = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "break", "case", "catch", "class", "const", "continue", "debugger", "default",
        "delete", "do", "else", "enum", "export", "extends", "false", "finally",
        "for", "function", "if", "import", "in", "instanceof", "new", "null",
        "return", "super", "switch", "this", "throw", "true", "try", "typeof",
        "var", "void", "while", "with", "yield",
        "let", "static", "implements", "interface", "package", "private", "protected",
        "public", "as", "async", "await", "constructor", "get", "set",
        "from", "of", "namespace", "module", "declare", "abstract", "any", "boolean",
        "never", "number", "object", "string", "symbol", "unknown", "type", "readonly"
    };

    /// <summary>
    /// Check if a name is a TypeScript reserved word.
    /// Case-insensitive comparison.
    /// </summary>
    public static bool IsReservedWord(string name)
    {
        return ReservedWords.Contains(name);
    }

    /// <summary>
    /// Sanitize parameter name by appending underscore suffix if it's a reserved word.
    /// Used for method/constructor parameters.
    /// Example: "switch" → "switch_", "type" → "type_"
    /// </summary>
    public static string SanitizeParameterName(string name)
    {
        return IsReservedWord(name) ? $"{name}_" : name;
    }

    /// <summary>
    /// Escape identifier using $$name$$ format for Tsonic.
    /// Used for type/member names in TypeScript declarations.
    /// Example: "switch" → "$$switch$$"
    /// </summary>
    public static string EscapeIdentifier(string name)
    {
        return IsReservedWord(name) ? $"$${name}$$" : name;
    }
}
