using System.Reflection;

namespace GenerateDts;

/// <summary>
/// Static helpers for filtering types and members during assembly processing.
/// </summary>
public static class TypeFilters
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

    public static bool ShouldIncludeType(Type type, GeneratorConfig config, HashSet<string>? namespaceWhitelist)
    {
        // Skip if not public
        if (!type.IsPublic && !type.IsNestedPublic)
        {
            return false;
        }

        // Skip compiler-generated types
        if (type.Name.Contains('<') || type.Name.Contains('>'))
        {
            return false;
        }

        // Skip if namespace is in skip list
        if (config.SkipNamespaces.Contains(type.Namespace ?? ""))
        {
            return false;
        }

        // Apply whitelist if provided
        if (namespaceWhitelist != null)
        {
            if (type.Namespace == null)
            {
                return false;
            }

            // Check if namespace or any parent namespace is in whitelist
            var ns = type.Namespace;
            while (!string.IsNullOrEmpty(ns))
            {
                if (namespaceWhitelist.Contains(ns))
                {
                    return true;
                }

                var lastDot = ns.LastIndexOf('.');
                if (lastDot < 0) break;
                ns = ns.Substring(0, lastDot);
            }

            return false;
        }

        return true;
    }

    public static bool ShouldIncludeMember(MemberInfo member, GeneratorConfig config)
    {
        var fullMemberName = $"{member.DeclaringType?.FullName}::{member.Name}";

        if (config.SkipMembers.Contains(fullMemberName))
        {
            return false;
        }

        // Skip common Object methods unless explicitly needed
        if (member.Name is "Equals" or "GetHashCode" or "GetType" or "ToString" or "ReferenceEquals")
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if an interface should be excluded from implements clause.
    /// Interfaces that map to ReadonlyArray&lt;T&gt; cause TS2420 errors because
    /// they require array methods that .NET collections don't have.
    /// </summary>
    public static bool ShouldSkipInterfaceInImplementsClause(Type iface)
    {
        var fullName = iface.FullName;
        if (fullName == null) return false;

        // Skip interfaces that map to ReadonlyArray<T>
        // These require array methods (length, concat, join, slice, etc.) that .NET collections don't have
        return fullName.StartsWith("System.Collections.Generic.IEnumerable`") ||
               fullName.StartsWith("System.Collections.Generic.IReadOnlyList`") ||
               fullName.StartsWith("System.Collections.Generic.IReadOnlyCollection`");
    }

    public static bool IsStaticOnly(Type type)
    {
        // Static class in C# (abstract sealed)
        if (type.IsAbstract && type.IsSealed)
        {
            return true;
        }

        // ValueType cannot be static-only
        if (type.IsValueType)
        {
            return false;
        }

        // Check for public instance members
        var instanceMembers = type.GetMembers(BindingFlags.Public | BindingFlags.Instance);
        if (instanceMembers.Length > 0)
        {
            return false;
        }

        // Check for public instance constructors
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Any(c => !c.IsPrivate))
        {
            return false;
        }

        // Must have static members to be considered static-only
        var staticMembers = type.GetMembers(BindingFlags.Public | BindingFlags.Static);
        return staticMembers.Length > 0;
    }

    public static bool IsDelegate(Type type)
    {
        // Check if type inherits from System.Delegate or System.MulticastDelegate
        // Use name-based comparison for MetadataLoadContext compatibility
        var baseType = type.BaseType;
        while (baseType != null)
        {
            var baseName = baseType.FullName;
            if (baseName == "System.Delegate" || baseName == "System.MulticastDelegate")
            {
                return true;
            }
            baseType = baseType.BaseType;
        }
        return false;
    }
}
