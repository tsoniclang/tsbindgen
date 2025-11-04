using System.Text;
using GenerateDts.Model;
using TypeInfo = GenerateDts.Model.TypeInfo;

namespace GenerateDts.Emit.Writers;

/// <summary>
/// Renders TypeScript type declarations (classes, interfaces, enums, static namespaces).
/// </summary>
public static class TypeWriter
{
    private const string Indent = "  ";

    /// <summary>
    /// Renders a class declaration with constructors, properties, methods, and optional companion namespace.
    /// </summary>
    public static void RenderClass(StringBuilder sb, ClassDeclaration classDecl, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);

        // Class declaration
        var classKeyword = classDecl.IsStatic ? "class" : "class";
        sb.Append($"{indent}{classKeyword} {classDecl.Name}");

        if (classDecl.IsGeneric)
        {
            sb.Append($"<{string.Join(", ", classDecl.GenericParameters)}>");
        }

        var extends = new List<string>();
        if (classDecl.BaseType != null)
        {
            extends.Add(classDecl.BaseType);
        }
        extends.AddRange(classDecl.Interfaces);

        if (extends.Count > 0)
        {
            if (classDecl.BaseType != null)
            {
                sb.Append($" extends {classDecl.BaseType}");
                if (classDecl.Interfaces.Count > 0)
                {
                    sb.Append($" implements {string.Join(", ", classDecl.Interfaces)}");
                }
            }
            else
            {
                sb.Append($" implements {string.Join(", ", classDecl.Interfaces)}");
            }
        }

        sb.AppendLine(" {");

        // Constructors
        foreach (var ctor in classDecl.Constructors)
        {
            MemberWriter.RenderConstructor(sb, ctor, indentLevel + 1);
        }

        // Properties
        foreach (var prop in classDecl.Properties)
        {
            MemberWriter.RenderProperty(sb, prop, indentLevel + 1);
        }

        // Methods
        foreach (var method in classDecl.Methods)
        {
            MemberWriter.RenderMethod(sb, method, indentLevel + 1);
        }

        sb.AppendLine($"{indent}}}");

        // Phase 2: Render companion namespace for static members (if conflicts detected)
        if (classDecl.Companion != null)
        {
            sb.AppendLine();
            RenderCompanionNamespace(sb, classDecl.Name, classDecl.Companion, classDecl.GenericParameters, indentLevel);
        }
    }

    /// <summary>
    /// Phase 2: Renders a companion namespace for static members.
    /// Used when static member names conflict with base class statics.
    /// </summary>
    public static void RenderCompanionNamespace(StringBuilder sb, string className, CompanionNamespace companion,
        IReadOnlyList<string> genericParams, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);
        var memberIndent = new string(' ', (indentLevel + 1) * 2);

        // Namespace with same name as class (no generics on namespaces in TS)
        sb.AppendLine($"{indent}namespace {className} {{");

        // Static properties as const exports
        foreach (var prop in companion.Properties)
        {
            // In namespaces, properties become exported constants
            sb.AppendLine($"{memberIndent}export const {prop.Name}: {prop.Type};");
        }

        // Static methods as exported functions
        foreach (var method in companion.Methods)
        {
            var genericParams2 = method.IsGeneric
                ? $"<{string.Join(", ", method.GenericParameters)}>"
                : "";

            var parameters = MemberWriter.RenderParameters(method.Parameters);

            sb.AppendLine($"{memberIndent}export function {method.Name}{genericParams2}({parameters}): {method.ReturnType};");
        }

        sb.AppendLine($"{indent}}}");
    }

    /// <summary>
    /// Renders an interface declaration with properties and methods.
    /// </summary>
    public static void RenderInterface(StringBuilder sb, InterfaceDeclaration interfaceDecl, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);

        sb.Append($"{indent}interface {interfaceDecl.Name}");

        if (interfaceDecl.IsGeneric)
        {
            sb.Append($"<{string.Join(", ", interfaceDecl.GenericParameters)}>");
        }

        if (interfaceDecl.Extends.Count > 0)
        {
            sb.Append($" extends {string.Join(", ", interfaceDecl.Extends)}");
        }

        sb.AppendLine(" {");

        // Properties
        foreach (var prop in interfaceDecl.Properties)
        {
            MemberWriter.RenderProperty(sb, prop, indentLevel + 1);
        }

        // Methods
        foreach (var method in interfaceDecl.Methods)
        {
            MemberWriter.RenderMethod(sb, method, indentLevel + 1);
        }

        sb.AppendLine($"{indent}}}");
    }

    /// <summary>
    /// Phase 1F: Render intersection type alias for diamond interfaces.
    ///
    /// Example output:
    /// type INumber_1<TSelf> = INumber_1_Base<TSelf> & IComparable & IComparable_1<TSelf> & ...;
    /// </summary>
    public static void RenderIntersectionAlias(StringBuilder sb, IntersectionTypeAlias alias, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);

        sb.Append($"{indent}type {alias.Name}");

        if (alias.IsGeneric)
        {
            sb.Append($"<{string.Join(", ", alias.GenericParameters)}>");
        }

        sb.Append(" = ");
        sb.Append(string.Join(" & ", alias.IntersectedTypes));
        sb.AppendLine(";");
    }

    /// <summary>
    /// Renders an enum declaration.
    /// </summary>
    public static void RenderEnum(StringBuilder sb, EnumDeclaration enumDecl, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);

        sb.AppendLine($"{indent}enum {enumDecl.Name} {{");

        for (int i = 0; i < enumDecl.Members.Count; i++)
        {
            var member = enumDecl.Members[i];
            var comma = i < enumDecl.Members.Count - 1 ? "," : "";
            sb.AppendLine($"{indent}{Indent}{member.Name} = {member.Value}{comma}");
        }

        sb.AppendLine($"{indent}}}");
    }

    /// <summary>
    /// Renders a static-only class as a TypeScript namespace.
    /// </summary>
    public static void RenderStaticNamespace(StringBuilder sb, StaticNamespaceDeclaration staticNs, int indentLevel)
    {
        var indent = new string(' ', indentLevel * 2);

        // Render as namespace with exported members
        sb.AppendLine($"{indent}namespace {staticNs.Name} {{");

        // Properties as exported constants
        foreach (var prop in staticNs.Properties)
        {
            if (prop.IsReadOnly)
            {
                sb.AppendLine($"{indent}{Indent}export const {prop.Name}: {prop.Type};");
            }
            else
            {
                sb.AppendLine($"{indent}{Indent}export let {prop.Name}: {prop.Type};");
            }
        }

        // Methods as exported functions
        foreach (var method in staticNs.Methods)
        {
            var genericParams = method.IsGeneric
                ? $"<{string.Join(", ", method.GenericParameters)}>"
                : "";

            var parameters = MemberWriter.RenderParameters(method.Parameters);

            sb.AppendLine($"{indent}{Indent}export function {method.Name}{genericParams}({parameters}): {method.ReturnType};");
        }

        sb.AppendLine($"{indent}}}");
    }
}
