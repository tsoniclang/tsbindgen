using System.Text;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Output;

/// <summary>
/// Emits TypeScript declaration files (.d.ts) from NamespaceModel.
/// </summary>
public static class TypeScriptEmit
{
    public static string Emit(NamespaceModel model)
    {
        var builder = new StringBuilder();

        // Header comment
        builder.AppendLine($"// Module for {model.ClrName}");
        builder.AppendLine($"// Generated from {model.SourceAssemblies.Count} assembly(ies)");
        builder.AppendLine();

        // Imports - collect all unique namespaces from all assemblies
        if (model.Imports.Count > 0)
        {
            var allNamespaces = model.Imports
                .SelectMany(kvp => kvp.Value)
                .Where(ns => ns != model.ClrName) // Skip self-references
                .Distinct()
                .OrderBy(ns => ns);

            foreach (var ns in allNamespaces)
            {
                var nsAlias = ns.Replace(".", "$");
                builder.AppendLine($"import type * as {nsAlias} from \"../{ns}/index.js\";");
            }
            builder.AppendLine();
        }

        // Helper declarations first - export them directly
        foreach (var type in model.Types)
        {
            foreach (var helper in type.Helpers)
            {
                builder.AppendLine($"export {helper.TsDefinition}");
                builder.AppendLine();
            }
        }

        // Types - export each type directly
        foreach (var type in model.Types)
        {
            EmitType(builder, type, "");
        }

        return builder.ToString();
    }

    private static void EmitType(StringBuilder builder, TypeModel type, string indent)
    {
        switch (type.Kind)
        {
            case TypeKind.Enum:
                EmitEnum(builder, type, indent);
                break;
            case TypeKind.Interface:
                EmitInterface(builder, type, indent);
                break;
            case TypeKind.Class:
            case TypeKind.Struct:
                EmitClass(builder, type, indent);
                break;
            case TypeKind.Delegate:
                EmitDelegate(builder, type, indent);
                break;
            case TypeKind.StaticNamespace:
                EmitStaticNamespace(builder, type, indent);
                break;
        }

        builder.AppendLine();
    }

    private static void EmitEnum(StringBuilder builder, TypeModel type, string indent)
    {
        builder.AppendLine($"{indent}export enum {type.TsAlias} {{");

        if (type.EnumMembers != null)
        {
            foreach (var member in type.EnumMembers)
            {
                builder.AppendLine($"{indent}    {member.Name} = {member.Value},");
            }
        }

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitInterface(StringBuilder builder, TypeModel type, string indent)
    {
        var genericParams = FormatGenericParameters(type.GenericParameters);
        var extends = type.Implements.Count > 0
            ? " extends " + string.Join(", ", type.Implements.Select(i => i.TsType))
            : "";

        builder.AppendLine($"{indent}export interface {type.TsAlias}{genericParams}{extends} {{");

        // Members - skip static members (TypeScript doesn't support static interface members)
        EmitMembers(builder, type.Members, indent + "    ", skipStatic: true);

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitClass(StringBuilder builder, TypeModel type, string indent)
    {
        var genericParams = FormatGenericParameters(type.GenericParameters);
        var extends = type.BaseType != null ? $" extends {type.BaseType.TsType}" : "";
        var implements = type.Implements.Count > 0
            ? " implements " + string.Join(", ", type.Implements.Select(i => i.TsType))
            : "";

        var modifiers = type.IsAbstract ? "abstract " : "";
        builder.AppendLine($"{indent}export {modifiers}class {type.TsAlias}{genericParams}{extends}{implements} {{");

        // Members
        EmitMembers(builder, type.Members, indent + "    ");

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitDelegate(StringBuilder builder, TypeModel type, string indent)
    {
        var genericParams = FormatGenericParameters(type.GenericParameters);
        var parameters = type.DelegateParameters != null
            ? string.Join(", ", type.DelegateParameters.Select(p => $"{EscapeIdentifier(p.Name)}: {p.TsType}"))
            : "";
        var returnType = type.DelegateReturnType?.TsType ?? "void";

        builder.AppendLine($"{indent}export type {type.TsAlias}{genericParams} = ({parameters}) => {returnType};");
    }

    private static void EmitStaticNamespace(StringBuilder builder, TypeModel type, string indent)
    {
        builder.AppendLine($"{indent}export class {type.TsAlias} {{");

        // Only static members
        EmitMembers(builder, type.Members, indent + "    ", staticOnly: true);

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitMembers(StringBuilder builder, MemberCollectionModel members, string indent, bool staticOnly = false, bool skipStatic = false)
    {
        // Constructors (if not staticOnly and not skipStatic)
        if (!staticOnly && !skipStatic)
        {
            foreach (var ctor in members.Constructors)
            {
                var parameters = string.Join(", ", ctor.Parameters.Select(p => $"{EscapeIdentifier(p.Name)}: {p.TsType}"));
                builder.AppendLine($"{indent}constructor({parameters});");
            }
        }

        // Methods
        foreach (var method in members.Methods)
        {
            if (staticOnly && !method.IsStatic) continue;
            if (skipStatic && method.IsStatic) continue;

            var modifiers = method.IsStatic ? "static " : "";
            var genericParams = FormatGenericParameters(method.GenericParameters);
            var parameters = string.Join(", ", method.Parameters.Select(p => $"{EscapeIdentifier(p.Name)}: {p.TsType}"));

            builder.AppendLine($"{indent}{modifiers}{method.TsAlias}{genericParams}({parameters}): {method.ReturnType.TsType};");
        }

        // Properties
        foreach (var prop in members.Properties)
        {
            if (staticOnly && !prop.IsStatic) continue;
            if (skipStatic && prop.IsStatic) continue;

            var modifiers = prop.IsStatic ? "static " : "";
            var readonlyModifier = prop.IsReadonly ? "readonly " : "";

            // Use contract type directly for covariant properties (TypeScript doesn't support property covariance)
            // This trades type precision for TypeScript compatibility
            var propertyType = prop.ContractTsType ?? prop.TsType;

            builder.AppendLine($"{indent}{modifiers}{readonlyModifier}{prop.TsAlias}: {propertyType};");
        }

        // Fields
        foreach (var field in members.Fields)
        {
            if (staticOnly && !field.IsStatic) continue;
            if (skipStatic && field.IsStatic) continue;

            var modifiers = field.IsStatic ? "static " : "";
            var readonlyModifier = field.IsReadonly ? "readonly " : "";

            builder.AppendLine($"{indent}{modifiers}{readonlyModifier}{field.TsAlias}: {field.TsType};");
        }

        // Events
        foreach (var evt in members.Events)
        {
            if (staticOnly && !evt.IsStatic) continue;
            if (skipStatic && evt.IsStatic) continue;

            var modifiers = evt.IsStatic ? "static " : "";

            builder.AppendLine($"{indent}{modifiers}readonly {evt.TsAlias}: {evt.TsType};");
        }
    }

    private static string FormatGenericParameters(IReadOnlyList<GenericParameterModel> parameters)
    {
        if (parameters.Count == 0)
            return "";

        var formatted = parameters.Select(p =>
        {
            var constraints = p.Constraints.Count > 0
                ? " extends " + string.Join(" & ", p.Constraints)
                : "";
            return $"{p.TsAlias}{constraints}";
        });

        return $"<{string.Join(", ", formatted)}>";
    }

    /// <summary>
    /// Escapes TypeScript/JavaScript reserved keywords using $$name$$ format.
    /// This is the standard Tsonic escaping format for reserved identifiers.
    /// </summary>
    private static string EscapeIdentifier(string name)
    {
        // List of TypeScript/JavaScript reserved keywords
        var reservedKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "break", "case", "catch", "class", "const", "continue", "debugger", "default",
            "delete", "do", "else", "enum", "export", "extends", "false", "finally",
            "for", "function", "if", "import", "in", "instanceof", "new", "null",
            "return", "super", "switch", "this", "throw", "true", "try", "typeof",
            "var", "void", "while", "with", "yield",
            "let", "static", "implements", "interface", "package", "private", "protected",
            "public", "as", "async", "await", "constructor", "get", "set",
            "from", "of", "namespace", "module", "declare", "abstract", "any", "boolean",
            "never", "number", "object", "string", "symbol", "unknown", "type", "readonly"
        };

        return reservedKeywords.Contains(name) ? $"$${name}$$" : name;
    }
}
