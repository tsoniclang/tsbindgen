namespace GenerateDts.Mapping;

public static class PrimitiveMapping
{
    public static string MapPrimitiveType(Type type)
    {
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

    public static string? MapSystemType(Type type)
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
}
