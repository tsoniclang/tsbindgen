using System.Text;

namespace GenerateDts.Mapping;

public static class GenericMapping
{
    public static string MapGenericType(Type type, Func<Type, string> mapType, Func<Type, string> getFullTypeName)
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
                var resultType = mapType(type.GenericTypeArguments[0]);
                return $"Promise<{resultType}>";
            }
        }

        // Generic type with parameters
        var sb = new StringBuilder();
        sb.Append(getFullTypeName(genericTypeDef));

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
                sb.Append(mapType(type.GenericTypeArguments[i]));
            }
            sb.Append('>');
        }

        return sb.ToString();
    }
}
