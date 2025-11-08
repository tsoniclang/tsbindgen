using System.Text.Json;
using tsbindgen.Config;
using tsbindgen.Render;

namespace tsbindgen.Render.Output;

/// <summary>
/// Emits bindings.json files mapping TS names to CLR names.
/// Only generated if any names differ.
/// </summary>
public static class BindingEmit
{
    public static string? Emit(NamespaceModel model, AnalysisContext ctx)
    {
        var hasBindings = model.ClrName != model.TsAlias ||
                         model.Types.Any(t => t.ClrName != ctx.GetTypeIdentifier(t) || HasMemberBindings(t, ctx));

        if (!hasBindings)
            return null;

        var bindings = new
        {
            namespace_ = new
            {
                name = model.ClrName,
                alias = model.TsAlias
            },
            types = model.Types
                .Where(t => t.ClrName != ctx.GetTypeIdentifier(t) || HasMemberBindings(t, ctx) || HasExplicitViews(t))
                .Select(t => new
                {
                    name = t.ClrName,
                    alias = ctx.GetTypeIdentifier(t),
                    explicitViews = t.ExplicitViews != null && t.ExplicitViews.Count > 0
                        ? t.ExplicitViews.ToDictionary(
                            v => v.ViewName + (v.Disambiguator ?? ""), // Apply disambiguator to key
                            v => new
                            {
                                interface_ = $"{v.Interface.Namespace}.{v.Interface.TypeName}",
                                members = v.ViewOnlyMethods.ToDictionary(
                                    m => SignatureNormalization.GetNormalizedSignature(m, ctx),
                                    m => m.ClrName
                                )
                            })
                        : null
                })
        };

        return JsonSerializer.Serialize(bindings, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static bool HasMemberBindings(TypeModel type, AnalysisContext ctx)
    {
        return type.Members.Methods.Any(m => m.ClrName != ctx.GetMethodIdentifier(m)) ||
               type.Members.Properties.Any(p => p.ClrName != ctx.GetPropertyIdentifier(p)) ||
               type.Members.Fields.Any(f => f.ClrName != ctx.GetFieldIdentifier(f)) ||
               type.Members.Events.Any(e => e.ClrName != ctx.GetEventIdentifier(e));
    }

    private static bool HasExplicitViews(TypeModel type)
    {
        return type.ExplicitViews != null && type.ExplicitViews.Count > 0;
    }
}
