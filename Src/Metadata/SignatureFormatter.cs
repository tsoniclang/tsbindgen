using System.Reflection;
using System.Text;

namespace GenerateDts.Metadata;

/// <summary>
/// Formats C# member signatures for use as keys in metadata JSON.
/// </summary>
public sealed class SignatureFormatter
{
    /// <summary>
    /// Formats a method signature as MethodName(Type1,Type2,...).
    /// </summary>
    public string FormatMethod(MethodInfo method)
    {
        var sb = new StringBuilder();
        sb.Append(method.Name);
        sb.Append('(');

        var parameters = method.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(GetCSharpTypeName(parameters[i].ParameterType));
        }

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Formats a constructor signature as ctor(Type1,Type2,...).
    /// </summary>
    public string FormatConstructor(ConstructorInfo constructor)
    {
        var sb = new StringBuilder();
        sb.Append("ctor(");

        var parameters = constructor.GetParameters();
        for (int i = 0; i < parameters.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(GetCSharpTypeName(parameters[i].ParameterType));
        }

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Formats a property signature (just the property name).
    /// </summary>
    public string FormatProperty(PropertyInfo property)
    {
        return property.Name;
    }

    /// <summary>
    /// Gets the C# type name for use in signatures.
    /// Unlike TypeMapper, this uses C# type names, not TypeScript mapped names.
    /// </summary>
    private string GetCSharpTypeName(Type type)
    {
        // Handle ref/out parameters
        if (type.IsByRef)
        {
            return GetCSharpTypeName(type.GetElementType()!);
        }

        // Handle nullable value types
        if (Nullable.GetUnderlyingType(type) is Type underlyingType)
        {
            return $"{GetCSharpTypeName(underlyingType)}?";
        }

        // Handle arrays
        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            var rank = type.GetArrayRank();
            var brackets = rank == 1 ? "[]" : $"[{new string(',', rank - 1)}]";
            return $"{GetCSharpTypeName(elementType)}{brackets}";
        }

        // Handle generic types
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var genericTypeDef = type.GetGenericTypeDefinition();
            var name = genericTypeDef.Name;
            var backtickIndex = name.IndexOf('`');
            if (backtickIndex > 0)
            {
                name = name.Substring(0, backtickIndex);
            }

            var sb = new StringBuilder();
            if (genericTypeDef.Namespace != null)
            {
                sb.Append(genericTypeDef.Namespace);
                sb.Append('.');
            }
            sb.Append(name);
            sb.Append('<');

            var args = type.GetGenericArguments();
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(GetCSharpTypeName(args[i]));
            }

            sb.Append('>');
            return sb.ToString();
        }

        // Handle generic parameters (T, TKey, etc.)
        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        // Handle primitive and special types with short names
        var shortName = GetShortTypeName(type);
        if (shortName != null)
        {
            return shortName;
        }

        // Default: use fully qualified name
        var fullName = type.FullName ?? type.Name;
        return fullName.Replace('+', '.');
    }

    /// <summary>
    /// Gets the C# short name for common types (e.g., "string" instead of "System.String").
    /// </summary>
    private string? GetShortTypeName(Type type)
    {
        return type switch
        {
            _ when type == typeof(void) => "void",
            _ when type == typeof(string) => "string",
            _ when type == typeof(bool) => "bool",
            _ when type == typeof(object) => "object",
            _ when type == typeof(decimal) => "decimal",
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
            _ => null
        };
    }
}
