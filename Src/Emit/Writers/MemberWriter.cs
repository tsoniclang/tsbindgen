using System.Text;
using GenerateDts.Model;
using TypeInfo = GenerateDts.Model.TypeInfo;

namespace GenerateDts.Emit.Writers;

/// <summary>
/// Renders class/interface members (constructors, properties, methods).
/// </summary>
public static class MemberWriter
{
    private const string Indent = "  ";

    /// <summary>
    /// Renders a constructor declaration.
    /// </summary>
    public static void RenderConstructor(StringBuilder sb, TypeInfo.ConstructorInfo ctor, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);
        var parameters = RenderParameters(ctor.Parameters);
        sb.AppendLine($"{indent}constructor({parameters});");
    }

    /// <summary>
    /// Renders a property declaration.
    /// </summary>
    public static void RenderProperty(StringBuilder sb, TypeInfo.PropertyInfo prop, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);
        var modifiers = new List<string>();

        if (prop.IsStatic)
        {
            modifiers.Add("static");
        }

        if (prop.IsReadOnly)
        {
            modifiers.Add("readonly");
        }

        var modifierStr = modifiers.Count > 0 ? string.Join(" ", modifiers) + " " : "";
        sb.AppendLine($"{indent}{modifierStr}{prop.Name}: {prop.Type};");
    }

    /// <summary>
    /// Renders a method declaration.
    /// </summary>
    public static void RenderMethod(StringBuilder sb, TypeInfo.MethodInfo method, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);
        var modifiers = method.IsStatic ? "static " : "";

        var genericParams = method.IsGeneric
            ? $"<{string.Join(", ", method.GenericParameters)}>"
            : "";

        var parameters = RenderParameters(method.Parameters);

        sb.AppendLine($"{indent}{modifiers}{method.Name}{genericParams}({parameters}): {method.ReturnType};");
    }

    /// <summary>
    /// Renders a parameter list for constructors and methods.
    /// </summary>
    public static string RenderParameters(IReadOnlyList<TypeInfo.ParameterInfo> parameters)
    {
        if (parameters.Count == 0)
        {
            return "";
        }

        var parts = new List<string>();

        foreach (var param in parameters)
        {
            var sb = new StringBuilder();

            if (param.IsParams)
            {
                sb.Append("...");
            }

            sb.Append(param.Name);

            if (param.IsOptional && !param.IsParams)
            {
                sb.Append("?");
            }

            sb.Append(": ");

            if (param.IsParams)
            {
                sb.Append($"ReadonlyArray<{param.Type}>");
            }
            else
            {
                sb.Append(param.Type);
            }

            parts.Add(sb.ToString());
        }

        return string.Join(", ", parts);
    }
}
