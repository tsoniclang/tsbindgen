using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Transform;

/// <summary>
/// Converts NamespaceBundle (from Phase 2) to NamespaceModel (for Phase 3).
/// Applies naming transforms and normalizes structure.
/// </summary>
public static class ModelBuilder
{
    public static NamespaceModel Build(
        NamespaceBundle bundle,
        GeneratorConfig config)
    {
        var tsAlias = NameTransformation.Apply(bundle.ClrName, config.NamespaceNames);

        // Build import aliases for type reference rewriting
        var importAliases = bundle.Imports
            .SelectMany(kvp => kvp.Value.Select(ns => ns))
            .Where(ns => ns != bundle.ClrName) // Exclude self-references
            .ToHashSet();

        var types = bundle.Types
            .Select(t => BuildType(t, config, bundle.ClrName, importAliases))
            .ToList();

        var imports = bundle.Imports
            .ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlySet<string>)kvp.Value.ToHashSet());

        return new NamespaceModel(
            bundle.ClrName,
            tsAlias,
            types,
            imports,
            bundle.Diagnostics,
            bundle.SourceAssemblies.ToList());
    }

    private static TypeModel BuildType(TypeSnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases)
    {
        // Build unique TypeScript name for nested types to avoid collisions
        // E.g., "Dictionary`2+KeyCollection+Enumerator" becomes "Dictionary_2_KeyCollection_Enumerator"
        var cleanedName = BuildTypeScriptName(snapshot.FullName, snapshot.ClrName);

        var tsAlias = snapshot.Kind switch
        {
            TypeKind.Interface => NameTransformation.Apply(cleanedName, config.InterfaceNames),
            TypeKind.Class => NameTransformation.Apply(cleanedName, config.ClassNames),
            _ => cleanedName
        };

        var genericParams = snapshot.GenericParameters
            .Select(gp => new GenericParameterModel(
                gp.Name,
                gp.Name, // Generic parameters don't get transformed
                gp.Constraints.Select(c => RewriteTypeReference(c, currentNamespace, importAliases)).ToList(),
                gp.Variance))
            .ToList();

        var baseType = snapshot.BaseType != null
            ? new TypeReferenceModel(
                snapshot.BaseType.ClrType,
                RewriteTypeReference(snapshot.BaseType.ClrType, currentNamespace, importAliases),
                snapshot.BaseType.Assembly)
            : null;

        var implements = snapshot.Implements
            .Select(i => new TypeReferenceModel(
                i.ClrType,
                RewriteTypeReference(i.ClrType, currentNamespace, importAliases),
                i.Assembly))
            .ToList();

        var members = BuildMembers(snapshot.Members, config, currentNamespace, importAliases);

        return new TypeModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.Kind,
            snapshot.IsStatic,
            snapshot.IsSealed,
            snapshot.IsAbstract,
            snapshot.Visibility,
            genericParams,
            baseType,
            implements,
            members,
            snapshot.Binding,
            Array.Empty<Diagnostic>(), // Type-level diagnostics added by analysis passes
            Array.Empty<HelperDeclaration>(), // Helpers added by analysis passes
            snapshot.UnderlyingType,
            snapshot.EnumMembers,
            snapshot.DelegateParameters?.Select(p => BuildParameter(p, currentNamespace, importAliases)).ToList(),
            snapshot.DelegateReturnType != null
                ? new TypeReferenceModel(
                    snapshot.DelegateReturnType.ClrType,
                    RewriteTypeReference(snapshot.DelegateReturnType.ClrType, currentNamespace, importAliases),
                    snapshot.DelegateReturnType.Assembly)
                : null);
    }

    private static MemberCollectionModel BuildMembers(
        MemberCollection members,
        GeneratorConfig config,
        string currentNamespace,
        HashSet<string> importAliases)
    {
        var constructors = members.Constructors
            .Select(c => new ConstructorModel(
                c.Visibility,
                c.Parameters.Select(p => BuildParameter(p, currentNamespace, importAliases)).ToList()))
            .ToList();

        var methods = members.Methods
            .Select(m => BuildMethod(m, config, currentNamespace, importAliases))
            .ToList();

        var properties = members.Properties
            .Select(p => BuildProperty(p, config, currentNamespace, importAliases))
            .ToList();

        var fields = members.Fields
            .Select(f => BuildField(f, config, currentNamespace, importAliases))
            .ToList();

        var events = members.Events
            .Select(e => BuildEvent(e, config, currentNamespace, importAliases))
            .ToList();

        return new MemberCollectionModel(constructors, methods, properties, fields, events);
    }

    private static MethodModel BuildMethod(MethodSnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.MethodNames);

        var genericParams = snapshot.GenericParameters
            .Select(gp => new GenericParameterModel(
                gp.Name,
                gp.Name,
                gp.Constraints.Select(c => RewriteTypeReference(c, currentNamespace, importAliases)).ToList(),
                gp.Variance))
            .ToList();

        return new MethodModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.IsStatic,
            snapshot.IsVirtual,
            snapshot.IsOverride,
            snapshot.IsAbstract,
            snapshot.Visibility,
            genericParams,
            snapshot.Parameters.Select(p => BuildParameter(p, currentNamespace, importAliases)).ToList(),
            new TypeReferenceModel(
                snapshot.ReturnType.ClrType,
                RewriteTypeReference(snapshot.ReturnType.ClrType, currentNamespace, importAliases),
                snapshot.ReturnType.Assembly),
            snapshot.Binding);
    }

    private static PropertyModel BuildProperty(PropertySnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.PropertyNames);

        var contractTsType = snapshot.ContractType != null
            ? RewriteTypeReference(snapshot.ContractType.ClrType, currentNamespace, importAliases)
            : null;

        return new PropertyModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.Type.ClrType,
            RewriteTypeReference(snapshot.Type.ClrType, currentNamespace, importAliases),
            snapshot.IsReadOnly,
            snapshot.IsStatic,
            snapshot.IsVirtual,
            snapshot.IsOverride,
            snapshot.Visibility,
            snapshot.Binding,
            contractTsType);
    }

    private static FieldModel BuildField(FieldSnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.PropertyNames);

        return new FieldModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.Type.ClrType,
            RewriteTypeReference(snapshot.Type.ClrType, currentNamespace, importAliases),
            snapshot.IsReadOnly,
            snapshot.IsStatic,
            snapshot.Visibility,
            snapshot.Binding);
    }

    private static EventModel BuildEvent(EventSnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.PropertyNames);

        return new EventModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.ClrType,
            RewriteTypeReference(snapshot.ClrType, currentNamespace, importAliases),
            snapshot.IsStatic,
            snapshot.Visibility,
            snapshot.Binding);
    }

    private static ParameterModel BuildParameter(ParameterSnapshot snapshot, string currentNamespace, HashSet<string> importAliases)
    {
        return new ParameterModel(
            snapshot.Name,
            snapshot.Type.ClrType,
            RewriteTypeReference(snapshot.Type.ClrType, currentNamespace, importAliases),
            snapshot.Kind,
            snapshot.IsOptional,
            snapshot.DefaultValue,
            snapshot.IsParams);
    }

    /// <summary>
    /// Rewrites type references based on namespace context.
    /// - Same namespace: "Microsoft.CSharp.RuntimeBinder.CSharpBinderFlags" -> "CSharpBinderFlags"
    /// - Imported namespace: "Microsoft.VisualBasic.CompareMethod" -> "Microsoft$VisualBasic.CompareMethod"
    /// - Other: leave as-is
    /// Handles generic types recursively.
    /// </summary>
    private static string RewriteTypeReference(string tsType, string currentNamespace, HashSet<string> importAliases)
    {
        if (string.IsNullOrEmpty(tsType))
            return tsType;

        // Phase 3 transformations - convert CLR markers to TypeScript types
        if (tsType == "__FunctionPointer" || tsType == "__UnknownType")
            return "any";

        // Handle generic types - split base name and type arguments
        if (tsType.Contains('<'))
        {
            var genericStart = tsType.IndexOf('<');
            var baseName = tsType.Substring(0, genericStart);

            // Find matching closing bracket
            var depth = 0;
            var genericEnd = -1;
            for (var i = genericStart; i < tsType.Length; i++)
            {
                if (tsType[i] == '<') depth++;
                else if (tsType[i] == '>') depth--;

                if (depth == 0)
                {
                    genericEnd = i;
                    break;
                }
            }

            if (genericEnd == -1)
                return tsType; // Malformed generic, return as-is

            var typeArgsText = tsType.Substring(genericStart + 1, genericEnd - genericStart - 1);
            var suffix = genericEnd + 1 < tsType.Length ? tsType.Substring(genericEnd + 1) : "";

            // Rewrite base name
            var rewrittenBase = RewriteSimpleType(baseName, currentNamespace, importAliases);

            // Parse and rewrite type arguments
            var typeArgs = ParseTypeArguments(typeArgsText);
            var rewrittenArgs = typeArgs.Select(arg => RewriteTypeReference(arg, currentNamespace, importAliases));

            return $"{rewrittenBase}<{string.Join(", ", rewrittenArgs)}>{suffix}";
        }

        // Non-generic type - rewrite based on namespace
        return RewriteSimpleType(tsType, currentNamespace, importAliases);
    }

    private static string RewriteSimpleType(string tsType, string currentNamespace, HashSet<string> importAliases)
    {
        // Extract array suffixes ([], [][], etc.)
        var arraySuffix = "";
        var baseType = tsType;
        while (baseType.EndsWith("[]"))
        {
            arraySuffix += "[]";
            baseType = baseType.Substring(0, baseType.Length - 2);
        }

        // Handle pointer types - TypeScript doesn't have pointer syntax, map to 'any'
        if (baseType.EndsWith("*"))
        {
            return "any" + arraySuffix;
        }

        // Special case: primitive types (no dots) - no rewriting needed
        if (!baseType.Contains("."))
        {
            return baseType + arraySuffix;
        }

        // Extract namespace and type name
        var lastDot = baseType.LastIndexOf('.');
        if (lastDot > 0)
        {
            var namespacePart = baseType.Substring(0, lastDot);
            var typePart = baseType.Substring(lastDot + 1);

            // If type is from the current namespace, use unqualified name
            if (namespacePart == currentNamespace)
            {
                return typePart + arraySuffix;
            }

            // Cross-namespace reference: use $ separator and namespace alias
            return namespacePart.Replace(".", "$") + "." + typePart + arraySuffix;
        }

        // No namespace prefix - just return as-is
        return baseType + arraySuffix;
    }

    private static List<string> ParseTypeArguments(string typeArgsText)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var depth = 0;

        for (var i = 0; i < typeArgsText.Length; i++)
        {
            var ch = typeArgsText[i];

            if (ch == '<')
            {
                depth++;
                current.Append(ch);
            }
            else if (ch == '>')
            {
                depth--;
                current.Append(ch);
            }
            else if (ch == ',' && depth == 0)
            {
                // Top-level comma - this is an argument separator
                args.Add(current.ToString().Trim());
                current.Clear();
                // Skip space after comma
                if (i + 1 < typeArgsText.Length && typeArgsText[i + 1] == ' ')
                    i++;
            }
            else
            {
                current.Append(ch);
            }
        }

        if (current.Length > 0)
            args.Add(current.ToString().Trim());

        return args;
    }

    /// <summary>
    /// Builds a unique TypeScript name for a type, handling nested types to avoid collisions.
    /// For nested types, includes parent type names in the hierarchy.
    /// E.g., "Dictionary`2+KeyCollection+Enumerator" becomes "Dictionary_2_KeyCollection_Enumerator"
    /// </summary>
    private static string BuildTypeScriptName(string fullName, string clrName)
    {
        // Extract just the type part (remove namespace)
        var lastDotIndex = fullName.LastIndexOf('.');
        var typeFullName = lastDotIndex >= 0 ? fullName.Substring(lastDotIndex + 1) : fullName;

        // Check if this is a nested type (contains '+' separator)
        if (!typeFullName.Contains('+'))
        {
            // Not nested - just clean the backticks
            return clrName.Replace('`', '_');
        }

        // Split by '+' to get parent hierarchy
        var parts = typeFullName.Split('+');

        // Build qualified name: ParentType_NestedType
        var nameBuilder = new System.Text.StringBuilder();

        foreach (var part in parts)
        {
            if (nameBuilder.Length > 0)
            {
                nameBuilder.Append('_');
            }

            // Clean backticks from each part (e.g., "Dictionary`2" -> "Dictionary_2")
            nameBuilder.Append(part.Replace('`', '_'));
        }

        return nameBuilder.ToString();
    }
}
