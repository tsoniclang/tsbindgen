using System.Text.Json;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Output;

/// <summary>
/// Emits a simplified list of TypeScript types for debugging and comparison.
/// This captures what actually gets written to .d.ts files.
/// </summary>
public static class TypeScriptTypeListEmit
{
    /// <summary>
    /// Extracts TypeScript type information from a NamespaceModel.
    /// Returns JSON string with list of types that will be emitted.
    /// </summary>
    public static string Emit(NamespaceModel model)
    {
        var entries = new List<TypeScriptTypeEntry>();

        foreach (var type in model.Types)
        {
            var kind = type.Kind switch
            {
                TypeKind.Class => "class",
                TypeKind.Struct => "class",
                TypeKind.Interface => "interface",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                TypeKind.StaticNamespace => "namespace",
                _ => "unknown"
            };

            // Check if nested by looking for $ in TsAlias
            var isNested = type.TsAlias.Contains('$');

            // Extract declaring type if nested
            string? declaringType = null;
            if (isNested)
            {
                var dollarIndex = type.TsAlias.IndexOf('$');
                if (dollarIndex > 0)
                {
                    declaringType = type.TsAlias.Substring(0, dollarIndex);
                }
            }

            entries.Add(new TypeScriptTypeEntry(
                type.TsAlias,
                kind,
                isNested,
                declaringType));
        }

        var typeList = new TypeScriptTypeList(model.TsAlias, entries);

        return JsonSerializer.Serialize(typeList, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}
