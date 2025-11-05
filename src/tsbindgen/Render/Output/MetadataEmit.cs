using System.Text.Json;
using tsbindgen.Render;

namespace tsbindgen.Render.Output;

/// <summary>
/// Emits metadata.json files containing CLR metadata.
/// TODO: Implement proper metadata schema
/// </summary>
public static class MetadataEmit
{
    public static string Emit(NamespaceModel model)
    {
        var metadata = new
        {
            namespace_ = model.ClrName,
            types = model.Types.Select(t => new
            {
                // TypeScript exported name (may be renamed for nested types: List_1_Enumerator)
                tsName = t.TsAlias,
                // Full CLR type name (e.g., System.Collections.Generic.List`1+Enumerator)
                clrType = t.Binding.Type,
                // Assembly containing this type
                assembly = t.Binding.Assembly,
                kind = t.Kind.ToString(),
                isStatic = t.IsStatic
            })
        };

        return JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}
