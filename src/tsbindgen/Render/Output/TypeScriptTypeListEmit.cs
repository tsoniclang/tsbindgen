using System.Text.Json;
using tsbindgen.Config;
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
    /// Returns JSON string with list of types AND members that will be emitted.
    /// Matches snapshot.json structure (flat list with tsEmitName).
    /// </summary>
    public static string Emit(NamespaceModel model, AnalysisContext ctx)
    {
        var types = new List<TypeScriptTypeEntry>();

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

            var members = CollectMembers(type, ctx);

            types.Add(new TypeScriptTypeEntry(
                type.TsEmitName,  // Use TsEmitName directly (includes $ for nested types)
                kind,
                members));
        }

        var typeList = new TypeScriptTypeList(model.TsAlias, types);

        return JsonSerializer.Serialize(typeList, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Collects all members (methods, properties, fields, events) that will be emitted for a type.
    /// </summary>
    private static List<TypeScriptMemberEntry> CollectMembers(TypeModel type, AnalysisContext ctx)
    {
        var members = new List<TypeScriptMemberEntry>();

        // Methods
        foreach (var method in type.Members.Methods)
        {
            var methodName = ctx.GetMethodIdentifier(method);
            members.Add(new TypeScriptMemberEntry(
                methodName,
                "method",
                method.IsStatic,
                method.EmitScope.ToString()));
        }

        // Properties
        foreach (var property in type.Members.Properties)
        {
            var propertyName = ctx.GetPropertyIdentifier(property);
            members.Add(new TypeScriptMemberEntry(
                propertyName,
                "property",
                property.IsStatic,
                "ClassSurface")); // Properties always on class surface
        }

        // Fields
        foreach (var field in type.Members.Fields)
        {
            var fieldName = ctx.GetFieldIdentifier(field);
            members.Add(new TypeScriptMemberEntry(
                fieldName,
                "field",
                field.IsStatic,
                "ClassSurface")); // Fields always on class surface
        }

        // Events
        foreach (var evt in type.Members.Events)
        {
            var eventName = ctx.GetEventIdentifier(evt);
            members.Add(new TypeScriptMemberEntry(
                eventName,
                "event",
                evt.IsStatic,
                "ClassSurface")); // Events always on class surface
        }

        return members;
    }
}
