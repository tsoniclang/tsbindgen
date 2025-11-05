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

        var types = bundle.Types
            .Select(t => BuildType(t, config))
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

    private static TypeModel BuildType(TypeSnapshot snapshot, GeneratorConfig config)
    {
        // Clean the CLR name (replace backticks with underscores for generic arity)
        var cleanedName = snapshot.ClrName.Replace('`', '_');

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
                gp.Constraints,
                gp.Variance))
            .ToList();

        var baseType = snapshot.BaseType != null
            ? new TypeReferenceModel(
                snapshot.BaseType.ClrType,
                snapshot.BaseType.TsType,
                snapshot.BaseType.Assembly)
            : null;

        var implements = snapshot.Implements
            .Select(i => new TypeReferenceModel(i.ClrType, i.TsType, i.Assembly))
            .ToList();

        var members = BuildMembers(snapshot.Members, config);

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
            snapshot.DelegateParameters?.Select(p => BuildParameter(p)).ToList(),
            snapshot.DelegateReturnType != null
                ? new TypeReferenceModel(
                    snapshot.DelegateReturnType.ClrType,
                    snapshot.DelegateReturnType.TsType,
                    snapshot.DelegateReturnType.Assembly)
                : null);
    }

    private static MemberCollectionModel BuildMembers(
        MemberCollection members,
        GeneratorConfig config)
    {
        var constructors = members.Constructors
            .Select(c => new ConstructorModel(
                c.Visibility,
                c.Parameters.Select(BuildParameter).ToList()))
            .ToList();

        var methods = members.Methods
            .Select(m => BuildMethod(m, config))
            .ToList();

        var properties = members.Properties
            .Select(p => BuildProperty(p, config))
            .ToList();

        var fields = members.Fields
            .Select(f => BuildField(f, config))
            .ToList();

        var events = members.Events
            .Select(e => BuildEvent(e, config))
            .ToList();

        return new MemberCollectionModel(constructors, methods, properties, fields, events);
    }

    private static MethodModel BuildMethod(MethodSnapshot snapshot, GeneratorConfig config)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.MethodNames);

        var genericParams = snapshot.GenericParameters
            .Select(gp => new GenericParameterModel(
                gp.Name,
                gp.Name,
                gp.Constraints,
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
            snapshot.Parameters.Select(BuildParameter).ToList(),
            new TypeReferenceModel(
                snapshot.ReturnType.ClrType,
                snapshot.ReturnType.TsType,
                snapshot.ReturnType.Assembly),
            snapshot.Binding);
    }

    private static PropertyModel BuildProperty(PropertySnapshot snapshot, GeneratorConfig config)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.PropertyNames);

        return new PropertyModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.ClrType,
            snapshot.TsType,
            snapshot.IsReadOnly,
            snapshot.IsStatic,
            snapshot.IsVirtual,
            snapshot.IsOverride,
            snapshot.Visibility,
            snapshot.Binding);
    }

    private static FieldModel BuildField(FieldSnapshot snapshot, GeneratorConfig config)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.PropertyNames);

        return new FieldModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.ClrType,
            snapshot.TsType,
            snapshot.IsReadOnly,
            snapshot.IsStatic,
            snapshot.Visibility,
            snapshot.Binding);
    }

    private static EventModel BuildEvent(EventSnapshot snapshot, GeneratorConfig config)
    {
        var tsAlias = NameTransformation.Apply(snapshot.ClrName, config.PropertyNames);

        return new EventModel(
            snapshot.ClrName,
            tsAlias,
            snapshot.ClrType,
            snapshot.TsType,
            snapshot.IsStatic,
            snapshot.Visibility,
            snapshot.Binding);
    }

    private static ParameterModel BuildParameter(ParameterSnapshot snapshot)
    {
        return new ParameterModel(
            snapshot.Name,
            snapshot.ClrType,
            snapshot.TsType,
            snapshot.Kind,
            snapshot.IsOptional,
            snapshot.DefaultValue,
            snapshot.IsParams);
    }
}
