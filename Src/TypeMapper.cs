using System.Reflection;
using System.Text;

namespace GenerateDts;

public sealed class TypeMapper
{
    private readonly List<string> _warnings = new();
    private Assembly? _currentAssembly;
    private DependencyTracker? _dependencyTracker;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Sets the context for cross-assembly type reference rewriting.
    /// </summary>
    public void SetContext(Assembly currentAssembly, DependencyTracker? dependencyTracker)
    {
        _currentAssembly = currentAssembly;
        _dependencyTracker = dependencyTracker;
    }

    public string MapType(Type type)
    {
        // Handle ref/out parameters (ByRef types)
        if (type.IsByRef)
        {
            // TypeScript doesn't have ref/out, so just map the underlying type
            return MapType(type.GetElementType()!);
        }

        // Handle pointer types
        if (type.IsPointer)
        {
            // TypeScript doesn't have pointers, map to 'any' for unsafe code
            // The underlying type information is still available via GetElementType() if needed
            AddWarning($"Pointer type {type.Name} mapped to 'any' (TypeScript doesn't support pointers)");
            return "any";
        }

        // Handle nullable value types
        if (Nullable.GetUnderlyingType(type) is Type underlyingType)
        {
            return $"{MapType(underlyingType)} | null";
        }

        // Handle arrays
        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            return $"ReadonlyArray<{MapType(elementType)}>";
        }

        // Handle delegates - must come before generic type handling
        // Check if this type is a delegate (inherits from System.Delegate or System.MulticastDelegate)
        if (IsDelegate(type))
        {
            var delegateSignature = MapDelegateToFunctionType(type);
            if (delegateSignature != null)
            {
                return delegateSignature;
            }
            // Fallback if delegate mapping fails
        }

        // Handle generic types
        if (type.IsGenericType)
        {
            return MapGenericType(type);
        }

        // Handle primitive types
        // Use name-based check for string/void since type == typeof() fails with MetadataLoadContext
        var fullName = type.FullName ?? type.Name;
        if (type.IsPrimitive || fullName == "System.String" || fullName == "System.Void")
        {
            return MapPrimitiveType(type);
        }

        // Handle special types
        if (type.Namespace?.StartsWith("System") == true)
        {
            var mapped = MapSystemType(type);
            if (mapped != null)
            {
                return mapped;
            }
        }

        // Default: use fully qualified name, fallback to "any" if empty
        var fullTypeName = GetFullTypeName(type);
        if (string.IsNullOrWhiteSpace(fullTypeName))
        {
            AddWarning($"Type {type} has no name - mapped to 'any'");
            return "any";
        }
        return fullTypeName;
    }

    private string MapPrimitiveType(Type type)
    {
        // Use name-based comparisons for MetadataLoadContext compatibility
        // type == typeof(bool) fails when type is from MetadataLoadContext
        var fullName = type.FullName ?? type.Name;

        return fullName switch
        {
            "System.Void" => "void",
            "System.String" => "string",
            "System.Boolean" => "boolean",
            "System.Double" => "double",
            "System.Single" => "float",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Decimal" => "decimal",
            _ => "number"
        };
    }

    private string? MapSystemType(Type type)
    {
        var fullName = type.FullName ?? type.Name;

        return fullName switch
        {
            "System.String" => "string",
            "System.Boolean" => "boolean",
            "System.Void" => "void",
            "System.Double" => "double",
            "System.Single" => "float",
            "System.Int32" => "int",
            "System.UInt32" => "uint",
            "System.Int64" => "long",
            "System.UInt64" => "ulong",
            "System.Int16" => "short",
            "System.UInt16" => "ushort",
            "System.Byte" => "byte",
            "System.SByte" => "sbyte",
            "System.Decimal" => "decimal",
            "System.Object" => "any",
            _ => null
        };
    }

    private string MapGenericType(Type type)
    {
        var genericTypeDef = type.GetGenericTypeDefinition();
        var fullName = genericTypeDef.FullName ?? genericTypeDef.Name;

        // Handle Task and Task<T>
        if (fullName.StartsWith("System.Threading.Tasks.Task"))
        {
            if (type.GenericTypeArguments.Length == 0)
            {
                return "Promise<void>";
            }
            else
            {
                var resultType = MapType(type.GenericTypeArguments[0]);
                return $"Promise<{resultType}>";
            }
        }

        // Note: List<T>, Dictionary<K,V>, HashSet<T> are handled by the generic logic below
        // We use fully qualified names for .d.ts files to avoid TS2304 errors

        // Generic type with parameters
        var sb = new StringBuilder();
        sb.Append(GetFullTypeName(genericTypeDef));

        // Handle open generic types (no type arguments filled in)
        if (type.GenericTypeArguments.Length == 0)
        {
            // Use the type parameter names from the definition
            var typeParams = genericTypeDef.GetGenericArguments();
            if (typeParams.Length > 0)
            {
                sb.Append('<');
                for (int i = 0; i < typeParams.Length; i++)
                {
                    if (i > 0) sb.Append(", ");
                    sb.Append(typeParams[i].Name);
                }
                sb.Append('>');
            }
        }
        else
        {
            // Closed generic type with type arguments
            sb.Append('<');
            for (int i = 0; i < type.GenericTypeArguments.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(MapType(type.GenericTypeArguments[i]));
            }
            sb.Append('>');
        }

        return sb.ToString();
    }

    public string GetFullTypeName(Type type)
    {
        if (type.IsGenericParameter)
        {
            return type.Name ?? "T";
        }

        // Build the type name with arity included
        var typeName = GetTypeNameWithArity(type);

        // Add namespace if present
        var fullName = type.Namespace != null ? $"{type.Namespace}.{typeName}" : typeName;

        // Fallback to "any" if we somehow got an empty name
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return "any";
        }

        // Rewrite cross-assembly references with aliases (ESM Step 3)
        if (_currentAssembly != null && _dependencyTracker != null)
        {
            // Check if this type is from a different assembly
            if (type.Assembly != _currentAssembly)
            {
                var assemblyName = type.Assembly.GetName().Name;
                if (assemblyName != null)
                {
                    // Get the alias for this assembly
                    var alias = DependencyTracker.GetModuleAlias(assemblyName);

                    // Rewrite: System.Collections.IEnumerable → System_Private_CoreLib.System.Collections.IEnumerable
                    return $"{alias}.{fullName}";
                }
            }
        }

        return fullName;
    }

    private string GetTypeNameWithArity(Type type)
    {
        var baseName = type.Name;
        var arity = 0;

        // Extract arity from generic types
        if (type.IsGenericType || baseName.Contains('`'))
        {
            var backtickIndex = baseName.IndexOf('`');
            if (backtickIndex > 0)
            {
                if (int.TryParse(baseName.Substring(backtickIndex + 1), out var parsedArity))
                {
                    arity = parsedArity;
                }
                baseName = baseName.Substring(0, backtickIndex);
            }
        }

        // Handle nested types - build full ancestry chain
        if (type.IsNested && type.DeclaringType != null)
        {
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
            var nameBuilder = new StringBuilder();
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

        // For top-level types, include arity if generic
        if (arity > 0)
        {
            return $"{baseName}_{arity}";
        }

        return baseName;
    }

    public void AddWarning(string message)
    {
        _warnings.Add(message);
    }

    private bool IsDelegate(Type type)
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

    private string? MapDelegateToFunctionType(Type delegateType)
    {
        try
        {
            // Find the Invoke method on the delegate
            var invokeMethod = delegateType.GetMethod("Invoke");
            if (invokeMethod == null)
            {
                AddWarning($"Delegate {delegateType.Name} has no Invoke method - mapped to 'any'");
                return "any";
            }

            // Get parameters
            var parameters = invokeMethod.GetParameters();
            var paramStrings = new List<string>();

            for (int i = 0; i < parameters.Length; i++)
            {
                var param = parameters[i];
                var paramName = string.IsNullOrEmpty(param.Name) ? $"arg{i}" : param.Name;
                var paramType = MapType(param.ParameterType);
                paramStrings.Add($"{paramName}: {paramType}");
            }

            // Get return type
            var returnType = MapType(invokeMethod.ReturnType);

            // Build function signature
            var paramList = string.Join(", ", paramStrings);
            return $"({paramList}) => {returnType}";
        }
        catch (Exception ex)
        {
            AddWarning($"Failed to map delegate {delegateType.Name}: {ex.Message}");
            return null;
        }
    }
}
