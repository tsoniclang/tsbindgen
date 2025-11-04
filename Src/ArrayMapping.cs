namespace GenerateDts;

public static class ArrayMapping
{
    public static string? TryMapArrayOrNullable(Type type, Func<Type, string> mapType)
    {
        // Handle nullable value types
        if (Nullable.GetUnderlyingType(type) is Type underlyingType)
        {
            return $"{mapType(underlyingType)} | null";
        }

        // Handle arrays
        if (type.IsArray)
        {
            var elementType = type.GetElementType()!;
            return $"ReadonlyArray<{mapType(elementType)}>";
        }

        return null;
    }
}
