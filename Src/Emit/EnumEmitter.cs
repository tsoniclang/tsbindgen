using System.Reflection;
using GenerateDts.Model;

namespace GenerateDts.Emit;

/// <summary>
/// Processes enum types and converts them to TypeScript declarations.
/// </summary>
public static class EnumEmitter
{
    public static EnumDeclaration ProcessEnum(Type type, Func<Type, string> getTypeName)
    {
        // Use GetFields() for MetadataLoadContext compatibility instead of Enum.GetValues()
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
        var members = fields
            .Select(f => new EnumMember(
                f.Name,
                Convert.ToInt64(f.GetRawConstantValue())))
            .ToList();

        return new EnumDeclaration(
            getTypeName(type), // Use GetTypeName() for nested types (e.g., Environment_SpecialFolder)
            type.FullName!,
            false,
            Array.Empty<string>(),
            members);
    }
}
