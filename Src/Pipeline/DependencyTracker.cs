using System.Reflection;

namespace GenerateDts.Pipeline;

/// <summary>
/// Tracks cross-assembly type dependencies for ESM import generation.
/// Records which external types are referenced so we can generate explicit imports.
/// </summary>
public sealed class DependencyTracker
{
    private readonly Assembly _currentAssembly;
    private readonly Dictionary<string, HashSet<string>> _assemblyToTypes = new();
    private readonly Dictionary<string, string> _typeToAssembly = new();

    public DependencyTracker(Assembly currentAssembly)
    {
        _currentAssembly = currentAssembly;
    }

    /// <summary>
    /// Records that a type from an external assembly is referenced.
    /// </summary>
    public void RecordTypeReference(Type type)
    {
        // Skip if type is from current assembly
        if (type.Assembly == _currentAssembly)
            return;

        // Skip primitive types and built-in TypeScript types
        if (type.IsPrimitive || type == typeof(string) || type == typeof(object) || type == typeof(void))
            return;

        // Get the defining assembly name
        var assemblyName = type.Assembly.GetName().Name;
        if (assemblyName == null)
            return;

        // Get full type name (namespace + name)
        var fullTypeName = GetFullTypeName(type);
        if (fullTypeName == null)
            return;

        // Record the dependency
        if (!_assemblyToTypes.ContainsKey(assemblyName))
        {
            _assemblyToTypes[assemblyName] = new HashSet<string>();
        }

        _assemblyToTypes[assemblyName].Add(fullTypeName);
        _typeToAssembly[fullTypeName] = assemblyName;
    }

    /// <summary>
    /// Gets all external assemblies that this assembly depends on.
    /// </summary>
    public IReadOnlyList<string> GetDependentAssemblies()
    {
        return _assemblyToTypes.Keys.OrderBy(x => x).ToList();
    }

    /// <summary>
    /// Gets all types referenced from a specific assembly.
    /// </summary>
    public IReadOnlySet<string> GetTypesFromAssembly(string assemblyName)
    {
        return _assemblyToTypes.TryGetValue(assemblyName, out var types)
            ? types
            : new HashSet<string>();
    }

    /// <summary>
    /// Gets the assembly that defines a given type.
    /// </summary>
    public string? GetDefiningAssembly(string fullTypeName)
    {
        return _typeToAssembly.TryGetValue(fullTypeName, out var assembly)
            ? assembly
            : null;
    }

    /// <summary>
    /// Generates a consistent module alias for an assembly name.
    /// Example: "System.Private.CoreLib" → "System_Private_CoreLib"
    /// </summary>
    public static string GetModuleAlias(string assemblyName)
    {
        // Replace dots with underscores to create valid identifier
        return assemblyName.Replace(".", "_");
    }

    /// <summary>
    /// Gets the full qualified name for a type (namespace + name).
    /// </summary>
    private static string? GetFullTypeName(Type type)
    {
        // Handle generic types
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            type = type.GetGenericTypeDefinition();
        }

        // Handle arrays
        if (type.IsArray)
        {
            var elementType = type.GetElementType();
            if (elementType != null)
            {
                return GetFullTypeName(elementType);
            }
        }

        // Handle by-ref types (ref/out parameters)
        if (type.IsByRef)
        {
            var elementType = type.GetElementType();
            if (elementType != null)
            {
                return GetFullTypeName(elementType);
            }
        }

        // Handle pointers
        if (type.IsPointer)
        {
            var elementType = type.GetElementType();
            if (elementType != null)
            {
                return GetFullTypeName(elementType);
            }
        }

        // Get full name
        var fullName = type.FullName;
        if (fullName == null)
            return null;

        // Clean up generic backtick notation (e.g., "List`1" → "List")
        var backtickIndex = fullName.IndexOf('`');
        if (backtickIndex >= 0)
        {
            fullName = fullName.Substring(0, backtickIndex);
        }

        // Replace nested type separator + with .
        fullName = fullName.Replace('+', '.');

        return fullName;
    }

    /// <summary>
    /// Exports dependency information to JSON format for debugging.
    /// </summary>
    public Dictionary<string, object> ToJson()
    {
        var result = new Dictionary<string, object>();

        foreach (var (assembly, types) in _assemblyToTypes.OrderBy(x => x.Key))
        {
            result[assembly] = new Dictionary<string, object>
            {
                ["alias"] = GetModuleAlias(assembly),
                ["types"] = types.OrderBy(x => x).ToList()
            };
        }

        return result;
    }
}
