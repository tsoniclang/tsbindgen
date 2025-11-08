using System.Text.Json;
using tsbindgen.Config;
using tsbindgen.Render;

namespace tsbindgen.Render.Output;

/// <summary>
/// Emits metadata.json files containing CLR metadata.
/// TODO: Implement proper metadata schema
/// </summary>
public static class MetadataEmit
{
    public static string Emit(NamespaceModel model, AnalysisContext ctx)
    {
        var metadata = new
        {
            namespace_ = model.ClrName,
            types = model.Types.Select(t => new
            {
                // TypeScript exported name (may be renamed for nested types: List_1_Enumerator)
                tsName = ctx.GetTypeIdentifier(t),
                // Full CLR type name (e.g., System.Collections.Generic.List`1+Enumerator)
                clrType = t.Binding.Type,
                // Assembly containing this type
                assembly = t.Binding.Assembly,
                kind = t.Kind.ToString(),
                isStatic = t.IsStatic,
                // Explicit interface views (methods that don't fit in class surface)
                explicitViews = t.ExplicitViews != null && t.ExplicitViews.Count > 0
                    ? t.ExplicitViews.Select(v => new
                    {
                        viewName = v.ViewName + (v.Disambiguator ?? ""), // Apply disambiguator
                        interface_ = $"{v.Interface.Namespace}.{v.Interface.TypeName}",
                        reason = "StructuralConformance", // Why this view exists
                        methods = v.ViewOnlyMethods.Select(m => new
                        {
                            tsName = ctx.GetMethodIdentifier(m),
                            clrName = m.ClrName,
                            normalizedSignature = SignatureNormalization.GetNormalizedSignature(m, ctx)
                        }).ToList()
                    }).ToList()
                    : null
            })
        };

        return JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
}
