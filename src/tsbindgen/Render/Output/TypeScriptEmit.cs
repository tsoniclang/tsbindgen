using System.Text;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Output;

/// <summary>
/// Emits TypeScript declaration files (.d.ts) from NamespaceModel.
/// </summary>
public static class TypeScriptEmit
{
    // Track bindings during emit for later generation
    private static Dictionary<string, TypeBindings> _bindingsMap = new();

    public static string Emit(NamespaceModel model)
    {
        // Reset bindings for this namespace
        _bindingsMap = new Dictionary<string, TypeBindings>();

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
            EmitType(builder, type, "", model.ClrName, model);
        }

        return builder.ToString();
    }

    private static void EmitType(StringBuilder builder, TypeModel type, string indent, string currentNamespace, NamespaceModel namespaceModel)
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
                EmitClass(builder, type, indent, currentNamespace, namespaceModel);
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
        EmitMembers(builder, type.Members, typeBindings: null, indent + "    ", skipStatic: true, currentNamespace: currentNamespace, isInterface: true);

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitClass(StringBuilder builder, TypeModel type, string indent, string currentNamespace, NamespaceModel namespaceModel)
    {
        var typeName = ToTypeScriptType(type.Binding.Type, currentNamespace, includeNamespacePrefix: false, includeGenericArgs: false);
        var genericParams = FormatGenericParameters(type.GenericParameters, currentNamespace);
        var extends = type.BaseType != null ? $" extends {ToTypeScriptType(type.BaseType, currentNamespace)}" : "";
        var implements = type.Implements.Count > 0
            ? " implements " + string.Join(", ", type.Implements.Select(i => ToTypeScriptType(i, currentNamespace)))
            : "";

        var modifiers = type.IsAbstract ? "abstract " : "";
        builder.AppendLine($"{indent}export {modifiers}class {typeName}{genericParams}{extends}{implements} {{");

        // Initialize bindings for this type
        var typeBindings = new TypeBindings(
            type.Binding.Type.TypeName,
            typeName,
            new Dictionary<string, MemberBindingInfo>());
        _bindingsMap[typeName] = typeBindings;

        // Members - emit methods (properties become methods too)
        EmitMembers(builder, type.Members, typeBindings, indent + "    ", currentNamespace: currentNamespace);

        // Emit getter/setter methods for properties to satisfy interface contracts
        // Pass the namespace model so we can look up interface types
        EmitPropertyMethods(builder, type.Members, type.Implements, namespaceModel, typeBindings, indent + "    ", currentNamespace);

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
        EmitMembers(builder, type.Members, typeBindings: null, indent + "    ", staticOnly: true, currentNamespace: currentNamespace);

        builder.AppendLine($"{indent}}}");
    }

    private static void EmitMembers(StringBuilder builder, MemberCollectionModel members, TypeBindings? typeBindings, string indent, bool staticOnly = false, bool skipStatic = false, string currentNamespace = "", bool isInterface = false)
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

            // Track binding
            if (typeBindings != null)
            {
                typeBindings.Members[method.TsAlias] = new MemberBindingInfo(
                    Kind: "method",
                    ClrName: method.ClrName,
                    ClrMemberType: "method");
            }
        }

        // Properties
        // For interfaces: emit as getter/setter methods
        // For classes: skip (will be emitted as methods by EmitPropertyMethods)
        if (isInterface)
        {
            foreach (var prop in members.Properties)
            {
                if (staticOnly && !prop.IsStatic) continue;
                if (skipStatic && prop.IsStatic) continue;

                var modifiers = prop.IsStatic ? "static " : "";
                var propertyType = ToTypeScriptType(prop.Type, currentNamespace);

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
    /// Generates overloads based on implemented interfaces.
    /// Handles name conflicts with existing methods by adding numeric suffixes.
    /// </summary>
    private static void EmitPropertyMethods(StringBuilder builder, MemberCollectionModel members, IReadOnlyList<TypeReference> implements, NamespaceModel namespaceModel, TypeBindings typeBindings, string indent, string currentNamespace)
    {
        // Collect existing method names to detect conflicts
        var existingMethodNames = new HashSet<string>(
            members.Methods.Select(m => ToCamelCase(m.TsAlias)),
            StringComparer.Ordinal);

        foreach (var prop in members.Properties)
        {
            // Skip static properties for now (interfaces don't have static members)
            if (prop.IsStatic) continue;

            var propertyType = ToTypeScriptType(prop.Type, currentNamespace);
            var baseGetterName = ToCamelCase("get" + prop.TsAlias);
            var baseSetterName = ToCamelCase("set" + prop.TsAlias);

            // Resolve name conflicts
            var getterName = ResolveConflict(baseGetterName, existingMethodNames);
            var setterName = prop.IsReadonly ? null : ResolveConflict(baseSetterName, existingMethodNames);

            // Collect all return types for this property from implemented interfaces (deduplicated)
            var interfaceTypes = new HashSet<string>();

            foreach (var interfaceRef in implements)
            {
                // Look up the interface type in the namespace model
                var interfaceType = FindInterfaceType(interfaceRef, namespaceModel, currentNamespace);
                if (interfaceType != null)
                {
                    // Find matching property in interface
                    var interfaceProp = interfaceType.Members.Properties
                        .FirstOrDefault(p => p.TsAlias == prop.TsAlias);

                    if (interfaceProp != null)
                    {
                        var interfacePropertyType = ToTypeScriptType(interfaceProp.Type, currentNamespace);
                        // Only add if different from the property's own type
                        if (interfacePropertyType != propertyType)
                        {
                            interfaceTypes.Add(interfacePropertyType);
                        }
                    }
                }
            }

            // Emit overloads for each unique interface type
            foreach (var interfaceType in interfaceTypes.OrderBy(t => t))
            {
                builder.AppendLine($"{indent}{getterName}(): {interfaceType};");
            }

            // Emit the implementation signature (most specific type)
            builder.AppendLine($"{indent}{getterName}(): {propertyType};");

            // Track getter binding
            typeBindings.Members[getterName] = new MemberBindingInfo(
                Kind: "method",
                ClrName: prop.ClrName,
                ClrMemberType: "property",
                Access: "get",
                Overloads: interfaceTypes.Count > 0 ? interfaceTypes.Concat(new[] { propertyType }).ToList() : null);

            // If not readonly, emit setter methods
            if (!prop.IsReadonly && setterName != null)
            {
                // Emit overloads for setters
                foreach (var interfaceType in interfaceTypes.OrderBy(t => t))
                {
                    builder.AppendLine($"{indent}{setterName}(value: {interfaceType}): System.Void;");
                }

                // Implementation signature
                builder.AppendLine($"{indent}{setterName}(value: {propertyType}): System.Void;");

                // Track setter binding
                typeBindings.Members[setterName] = new MemberBindingInfo(
                    Kind: "method",
                    ClrName: prop.ClrName,
                    ClrMemberType: "property",
                    Access: "set");
            }
        }
    }

    /// <summary>
    /// Resolves name conflicts by appending numeric suffix.
    /// Also adds the resolved name to the existing names set.
    /// </summary>
    private static string ResolveConflict(string baseName, HashSet<string> existingNames)
    {
        var name = baseName;
        var counter = 1;

        while (existingNames.Contains(name))
        {
            name = baseName + counter;
            counter++;
        }

        existingNames.Add(name);
        return name;
    }

    /// <summary>
    /// Finds an interface type by TypeReference in the current namespace.
    /// Returns null if the interface is from another namespace or not found.
    /// </summary>
    private static TypeModel? FindInterfaceType(TypeReference typeRef, NamespaceModel namespaceModel, string currentNamespace)
    {
        // Skip if interface is from different namespace (cross-namespace lookup not implemented yet)
        if (typeRef.Namespace != null && typeRef.Namespace != currentNamespace)
            return null;

        // Find type by name in the namespace
        var typeName = typeRef.TypeName;
        return namespaceModel.Types.FirstOrDefault(t =>
            t.Kind == TypeKind.Interface &&
            t.Binding.Type.TypeName == typeName);
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

    /// <summary>
    /// Get the bindings map generated during emit.
    /// </summary>
    public static IReadOnlyDictionary<string, TypeBindings> GetBindings() => _bindingsMap;
}

/// <summary>
/// Bindings for a single type.
/// </summary>
public sealed record TypeBindings(
    string ClrName,
    string TsAlias,
    Dictionary<string, MemberBindingInfo> Members);

/// <summary>
/// Binding information for a member (maps TS method name to CLR member).
/// </summary>
public sealed record MemberBindingInfo(
    string Kind,           // "method" in TS (all are methods now)
    string ClrName,        // CLR member name
    string ClrMemberType,  // "method", "property", "field", "event"
    string? Access = null, // "get" or "set" for properties
    List<string>? Overloads = null); // Return types for overloaded methods
