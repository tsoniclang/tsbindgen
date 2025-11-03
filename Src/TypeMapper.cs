using System.Reflection;
using System.Text;

namespace GenerateDts;

public sealed class TypeMapper
{
    private readonly List<string> _warnings = new();

    public IReadOnlyList<string> Warnings => _warnings;

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
        if (type.IsPrimitive || type == typeof(string) || type == typeof(void))
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
        return type switch
        {
            _ when type == typeof(void) => "void",
            _ when type == typeof(string) => "string",
            _ when type == typeof(bool) => "boolean",
            _ when type == typeof(double) => "double",
            _ when type == typeof(float) => "float",
            _ when type == typeof(int) => "int",
            _ when type == typeof(uint) => "uint",
            _ when type == typeof(long) => "long",
            _ when type == typeof(ulong) => "ulong",
            _ when type == typeof(short) => "short",
            _ when type == typeof(ushort) => "ushort",
            _ when type == typeof(byte) => "byte",
            _ when type == typeof(sbyte) => "sbyte",
            _ when type == typeof(decimal) => "decimal",
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

        // Handle List<T>
        if (fullName.StartsWith("System.Collections.Generic.List"))
        {
            var elementType = MapType(type.GenericTypeArguments[0]);
            return $"List<{elementType}>";
        }

        // Handle Dictionary<K,V>
        if (fullName.StartsWith("System.Collections.Generic.Dictionary"))
        {
            var keyType = MapType(type.GenericTypeArguments[0]);
            var valueType = MapType(type.GenericTypeArguments[1]);
            return $"Dictionary<{keyType}, {valueType}>";
        }

        // Handle HashSet<T>
        if (fullName.StartsWith("System.Collections.Generic.HashSet"))
        {
            var elementType = MapType(type.GenericTypeArguments[0]);
            return $"HashSet<{elementType}>";
        }

        // Handle IEnumerable<T> and similar
        if (fullName.StartsWith("System.Collections.Generic.IEnumerable") ||
            fullName.StartsWith("System.Collections.Generic.IReadOnlyList") ||
            fullName.StartsWith("System.Collections.Generic.IReadOnlyCollection"))
        {
            var elementType = MapType(type.GenericTypeArguments[0]);
            return $"ReadonlyArray<{elementType}>";
        }

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

        if (type.IsGenericType)
        {
            var name = type.Name;
            var backtickIndex = name.IndexOf('`');
            if (backtickIndex > 0)
            {
                name = name.Substring(0, backtickIndex);
            }
            var result = type.Namespace != null ? $"{type.Namespace}.{name}" : name;
            return string.IsNullOrWhiteSpace(result) ? "any" : result;
        }

        // Replace + with . for nested types (C# uses + but TypeScript uses .)
        var fullName = type.FullName ?? type.Name ?? "";
        var finalName = fullName.Replace('+', '.');

        // Fallback to "any" if we somehow got an empty name (can happen with function pointers, etc.)
        return string.IsNullOrWhiteSpace(finalName) ? "any" : finalName;
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
