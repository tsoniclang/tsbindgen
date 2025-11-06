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
            EmitType(builder, type, "", model.ClrName);
        }

        return builder.ToString();
    }

    private static void EmitType(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        switch (type.Kind)
        {
            case TypeKind.Enum:
                EmitEnum(builder, type, indent, currentNamespace);
                break;
            case TypeKind.Interface:
                EmitInterface(builder, type, indent, currentNamespace);
                break;
            case TypeKind.Class:
            case TypeKind.Struct:
                EmitClass(builder, type, indent, currentNamespace);
                break;
            case TypeKind.Delegate:
                EmitDelegate(builder, type, indent, currentNamespace);
                break;
            case TypeKind.StaticNamespace:
                EmitStaticNamespace(builder, type, indent, currentNamespace);
                break;
        }

        builder.AppendLine();
    }

    private static void EmitEnum(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        builder.AppendLine($"{indent}export enum {typeName} {{");

        if (type.EnumMembers != null)
        {
            foreach (var member in type.EnumMembers)
            {
                builder.AppendLine($"{indent}    {member.Name} = {member.Value},");
            }
        }

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitInterface(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        var genericParams = FormatGenericParameters(type.GenericParameters, currentNamespace);
        var extends = type.Implements.Count > 0
            ? " extends " + string.Join(", ", type.Implements.Select(i => ToTypeScriptType(i, currentNamespace)))
            : "";

        builder.AppendLine($"{indent}export interface {typeName}{genericParams}{extends} {{");

        // Members - skip static members (TypeScript doesn't support static interface members)
        // For interfaces, emit properties as getter/setter methods
        EmitMembers(builder, type.Members, indent + "    ", skipStatic: true, currentNamespace: currentNamespace, isInterface: true);

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitClass(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        var genericParams = FormatGenericParameters(type.GenericParameters, currentNamespace);
        var extends = type.BaseType != null ? $" extends {ToTypeScriptType(type.BaseType, currentNamespace)}" : "";
        var implements = type.Implements.Count > 0
            ? " implements " + string.Join(", ", type.Implements.Select(i => ToTypeScriptType(i, currentNamespace)))
            : "";

        var modifiers = type.IsAbstract ? "abstract " : "";
        builder.AppendLine($"{indent}export {modifiers}class {typeName}{genericParams}{extends}{implements} {{");

        // Members - emit properties and methods
        EmitMembers(builder, type.Members, indent + "    ", currentNamespace: currentNamespace);

        // Emit getter/setter methods for properties to satisfy interface contracts
        // Pass the implemented interfaces so we can create overloads
        EmitPropertyMethods(builder, type.Members, type.Implements, indent + "    ", currentNamespace: currentNamespace);

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitDelegate(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        var genericParams = FormatGenericParameters(type.GenericParameters, currentNamespace);
        var parameters = type.DelegateParameters != null
            ? string.Join(", ", type.DelegateParameters.Select(p => $"{EscapeIdentifier(p.Name)}: {ToTypeScriptType(p.Type, currentNamespace)}"))
            : "";
        var returnType = type.DelegateReturnType != null ? ToTypeScriptType(type.DelegateReturnType, currentNamespace) : "void";

        builder.AppendLine($"{indent}export type {typeName}{genericParams} = ({parameters}) => {returnType};");
    }

    private static void EmitStaticNamespace(StringBuilder builder, TypeModel type, string indent, string currentNamespace)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        builder.AppendLine($"{indent}export class {typeName} {{");

        // Only static members
        EmitMembers(builder, type.Members, indent + "    ", staticOnly: true, currentNamespace: currentNamespace);

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitMembers(StringBuilder builder, MemberCollectionModel members, string indent, bool staticOnly = false, bool skipStatic = false, string currentNamespace = "", bool isInterface = false)
    {
        // Constructors (if not staticOnly and not skipStatic)
        if (!staticOnly && !skipStatic)
        {
            foreach (var ctor in members.Constructors)
            {
                var parameters = string.Join(", ", ctor.Parameters.Select(p => $"{EscapeIdentifier(p.Name)}: {ToTypeScriptType(p.Type, currentNamespace)}"));
                builder.AppendLine($"{indent}constructor({parameters});");
            }
        }

        // Methods
        foreach (var method in members.Methods)
        {
            if (staticOnly && !method.IsStatic) continue;
            if (skipStatic && method.IsStatic) continue;

            var modifiers = method.IsStatic ? "static " : "";
            var genericParams = FormatGenericParameters(method.GenericParameters, currentNamespace);
            var parameters = string.Join(", ", method.Parameters.Select(p => $"{EscapeIdentifier(p.Name)}: {ToTypeScriptType(p.Type, currentNamespace)}"));

            builder.AppendLine($"{indent}{modifiers}{method.TsAlias}{genericParams}({parameters}): {ToTypeScriptType(method.ReturnType, currentNamespace)};");
        }

        // Properties
        foreach (var prop in members.Properties)
        {
            if (staticOnly && !prop.IsStatic) continue;
            if (skipStatic && prop.IsStatic) continue;

            var modifiers = prop.IsStatic ? "static " : "";
            var propertyType = ToTypeScriptType(prop.Type, currentNamespace);

            if (isInterface)
            {
                // For interfaces: emit getter/setter methods instead of properties
                // Convert PascalCase property name to camelCase method name
                var getterName = ToCamelCase("get" + prop.TsAlias);
                builder.AppendLine($"{indent}{modifiers}{getterName}(): {propertyType};");

                // If not readonly, also emit setter
                if (!prop.IsReadonly)
                {
                    var setterName = ToCamelCase("set" + prop.TsAlias);
                    builder.AppendLine($"{indent}{modifiers}{setterName}(value: {propertyType}): System.Void;");
                }
            }
            else
            {
                // For classes: emit property as-is
                var readonlyModifier = prop.IsReadonly ? "readonly " : "";
                builder.AppendLine($"{indent}{modifiers}{readonlyModifier}{prop.TsAlias}: {propertyType};");
            }
        }

        // Fields
        foreach (var field in members.Fields)
        {
            if (staticOnly && !field.IsStatic) continue;
            if (skipStatic && field.IsStatic) continue;

            var modifiers = field.IsStatic ? "static " : "";
            var readonlyModifier = field.IsReadonly ? "readonly " : "";

            builder.AppendLine($"{indent}{modifiers}{readonlyModifier}{field.TsAlias}: {ToTypeScriptType(field.Type, currentNamespace)};");
        }

        // Events
        foreach (var evt in members.Events)
        {
            if (staticOnly && !evt.IsStatic) continue;
            if (skipStatic && evt.IsStatic) continue;

            var modifiers = evt.IsStatic ? "static " : "";

            builder.AppendLine($"{indent}{modifiers}readonly {evt.TsAlias}: {ToTypeScriptType(evt.Type, currentNamespace)};");
        }
    }

    /// <summary>
    /// Emits getter/setter methods for properties to satisfy interface contracts.
    /// Classes need these methods to implement interfaces (which have methods, not properties).
    /// </summary>
    private static void EmitPropertyMethods(StringBuilder builder, MemberCollectionModel members, string indent, string currentNamespace)
    {
        foreach (var prop in members.Properties)
        {
            // Skip static properties for now (interfaces don't have static members)
            if (prop.IsStatic) continue;

            var propertyType = ToTypeScriptType(prop.Type, currentNamespace);
            var getterName = ToCamelCase("get" + prop.TsAlias);

            // Emit getter method
            builder.AppendLine($"{indent}{getterName}(): {propertyType};");

            // If not readonly, emit setter method
            if (!prop.IsReadonly)
            {
                var setterName = ToCamelCase("set" + prop.TsAlias);
                builder.AppendLine($"{indent}{setterName}(value: {propertyType}): System.Void;");
            }
        }
    }

    private static string FormatGenericParameters(IReadOnlyList<GenericParameterModel> parameters, string currentNamespace)
    {
        if (parameters.Count == 0)
            return "";

        var formatted = parameters.Select(p =>
        {
            var constraints = p.Constraints.Count > 0
                ? " extends " + string.Join(" & ", p.Constraints.Select(c => ToTypeScriptType(c, currentNamespace)))
                : "";
            return $"{p.TsAlias}{constraints}";
        });

        return $"<{string.Join(", ", formatted)}>";
    }

    /// <summary>
    /// Converts PascalCase identifier to camelCase.
    /// </summary>
    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        // If first character is already lowercase, return as-is
        if (char.IsLower(name[0]))
            return name;

        // Convert first character to lowercase
        return char.ToLowerInvariant(name[0]) + name.Substring(1);
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

    /// <summary>
    /// Converts a TypeReference to TypeScript type syntax.
    /// Handles nested types, generics, arrays, pointers, and namespace prefixes.
    /// </summary>
    /// <param name="typeRef">The type reference to convert</param>
    /// <param name="currentNamespace">Current namespace context (for determining if cross-namespace prefix needed)</param>
    /// <param name="includeNamespacePrefix">If true, includes namespace prefix for cross-namespace types. If false, only includes type name.</param>
    /// <param name="includeGenericArgs">If true, includes generic type arguments. If false, omits them (for type declarations).</param>
    private static string ToTypeScriptType(TypeReference typeRef, string currentNamespace, bool includeNamespacePrefix = true, bool includeGenericArgs = true)
    {
        var sb = new StringBuilder();

        // Build the base type name with declaring type hierarchy
        if (typeRef.DeclaringType != null)
        {
            // Nested type: build qualified name with $ separator
            var parts = new List<string>();
            var current = typeRef;
            while (current != null)
            {
                parts.Insert(0, current.TypeName);
                current = current.DeclaringType;
            }

            // Get namespace from root declaring type
            current = typeRef;
            while (current.DeclaringType != null)
                current = current.DeclaringType;

            var ns = current.Namespace;

            // Namespace prefix if cross-namespace and requested
            if (includeNamespacePrefix && ns != null && ns != currentNamespace)
            {
                sb.Append(ns.Replace(".", "$"));
                sb.Append(".");
            }

            // Join nested type names with $ separator
            sb.Append(string.Join("$", parts));
        }
        else
        {
            // Top-level type
            if (includeNamespacePrefix && typeRef.Namespace != null && typeRef.Namespace != currentNamespace)
            {
                sb.Append(typeRef.Namespace.Replace(".", "$"));
                sb.Append(".");
            }
            sb.Append(typeRef.TypeName);
        }

        // Generic arguments
        if (includeGenericArgs && typeRef.GenericArgs.Count > 0)
        {
            sb.Append('<');
            for (int i = 0; i < typeRef.GenericArgs.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(ToTypeScriptType(typeRef.GenericArgs[i], currentNamespace, includeNamespacePrefix, includeGenericArgs));
            }
            sb.Append('>');
        }

        // Pointers become 'any'
        if (typeRef.PointerDepth > 0)
        {
            return "any";
        }

        // Arrays
        for (int i = 0; i < typeRef.ArrayRank; i++)
        {
            sb.Append("[]");
        }

        return sb.ToString();
    }
}
