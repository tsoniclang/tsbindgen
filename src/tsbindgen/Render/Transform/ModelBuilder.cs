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

        // Build type name lookup for nested type resolution
        // Maps short CLR type names to their qualified TypeScript names within this namespace
        var typeNameLookup = new Dictionary<string, string>();
        foreach (var type in bundle.Types)
        {
            var tsName = BuildTypeScriptName(type.FullName, type.ClrName);
            var shortName = type.ClrName.Replace('`', '_');

            // For nested types, also register by their simple name (e.g., "Enumerator")
            // This handles cases where return types use just the simple name
            if (type.FullName.Contains('+'))
            {
                var parts = type.ClrName.Split('+');
                var simpleName = parts[parts.Length - 1].Replace('`', '_');

                if (!typeNameLookup.ContainsKey(simpleName))
                {
                    typeNameLookup[simpleName] = tsName;
                }
            }

            typeNameLookup[shortName] = tsName;
        }

        var types = bundle.Types
            .Select(t => BuildType(t, config, bundle.ClrName, importAliases, typeNameLookup))
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

    private static TypeModel BuildType(TypeSnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases, Dictionary<string, string> typeNameLookup)
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
                gp.Constraints.ToList(),
                gp.Variance))
            .ToList();

        var members = BuildMembers(snapshot.Members, config, currentNamespace, importAliases, typeNameLookup);

        return new TypeModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.Kind,
            snapshot.IsStatic,
            snapshot.IsSealed,
            snapshot.IsAbstract,
            snapshot.Visibility,
            genericParams,
            snapshot.BaseType,
            snapshot.Implements.ToList(),
            members,
            snapshot.Binding,
            Array.Empty<Diagnostic>(), // Type-level diagnostics added by analysis passes
            Array.Empty<HelperDeclaration>(), // Helpers added by analysis passes
            snapshot.UnderlyingType,
            snapshot.EnumMembers,
            snapshot.DelegateParameters?.Select(p => BuildParameter(p, currentNamespace, importAliases, typeNameLookup)).ToList(),
            snapshot.DelegateReturnType);
    }

    private static MemberCollectionModel BuildMembers(
        MemberCollection members,
        GeneratorConfig config,
        string currentNamespace,
        HashSet<string> importAliases,
        Dictionary<string, string> typeNameLookup)
    {
        var constructors = members.Constructors
            .Select(c => new ConstructorModel(
                c.Visibility,
                c.Parameters.Select(p => BuildParameter(p, currentNamespace, importAliases, typeNameLookup)).ToList()))
            .ToList();

        var methods = members.Methods
            .Select(m => BuildMethod(m, config, currentNamespace, importAliases, typeNameLookup))
            .ToList();

        var properties = members.Properties
            .Select(p => BuildProperty(p, config, currentNamespace, importAliases, typeNameLookup))
            .ToList();

        var fields = members.Fields
            .Select(f => BuildField(f, config, currentNamespace, importAliases, typeNameLookup))
            .ToList();

        var events = members.Events
            .Select(e => BuildEvent(e, config, currentNamespace, importAliases, typeNameLookup))
            .ToList();

        return new MemberCollectionModel(constructors, methods, properties, fields, events);
    }

    private static MethodModel BuildMethod(MethodSnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases, Dictionary<string, string> typeNameLookup)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.MethodNames);

        var genericParams = snapshot.GenericParameters
            .Select(gp => new GenericParameterModel(
                gp.Name,
                gp.Name,
                gp.Constraints.ToList(),
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
            snapshot.Parameters.Select(p => BuildParameter(p, currentNamespace, importAliases, typeNameLookup)).ToList(),
            snapshot.ReturnType,
            snapshot.Binding);
    }

    private static PropertyModel BuildProperty(PropertySnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases, Dictionary<string, string> typeNameLookup)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.PropertyNames);

        return new PropertyModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.Type,
            snapshot.IsReadOnly,
            snapshot.IsStatic,
            snapshot.IsVirtual,
            snapshot.IsOverride,
            snapshot.Visibility,
            snapshot.Binding,
            snapshot.ContractType);
    }

    private static FieldModel BuildField(FieldSnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases, Dictionary<string, string> typeNameLookup)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.PropertyNames);

        return new FieldModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.Type,
            snapshot.IsReadOnly,
            snapshot.IsStatic,
            snapshot.Visibility,
            snapshot.Binding);
    }

    private static EventModel BuildEvent(EventSnapshot snapshot, GeneratorConfig config, string currentNamespace, HashSet<string> importAliases, Dictionary<string, string> typeNameLookup)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.PropertyNames);

        return new EventModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.Type,
            snapshot.IsStatic,
            snapshot.Visibility,
            snapshot.Binding);
    }

    private static ParameterModel BuildParameter(ParameterSnapshot snapshot, string currentNamespace, HashSet<string> importAliases, Dictionary<string, string> typeNameLookup)
    {
        return new ParameterModel(
            snapshot.Name,
            snapshot.Type,
            snapshot.Kind,
            snapshot.IsOptional,
            snapshot.DefaultValue,
            snapshot.IsParams);
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
