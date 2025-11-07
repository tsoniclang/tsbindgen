using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Transform;

/// <summary>
/// Phase 3: Converts NamespaceBundle (from Phase 2) to NamespaceModel.
/// No longer creates TsAlias strings - names computed on-demand via AnalysisContext.
/// </summary>
public static class ModelTransform
{
    public static NamespaceModel Build(
        NamespaceBundle bundle,
        GeneratorConfig config)
    {
        var tsAlias = NameTransformation.Apply(bundle.ClrName, config.NamespaceNames);

        var types = bundle.Types
            .Select(t => BuildType(t))
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

    private static TypeModel BuildType(TypeSnapshot snapshot)
    {
        var genericParams = snapshot.GenericParameters
            .Select(gp => new GenericParameterModel(
                gp.Name,
                gp.Constraints.ToList(),
                gp.Variance))
            .ToList();

        var members = BuildMembers(snapshot.Members);

        // Set implicit base types to match emission behavior (Phase 4)
        // This ensures Phase 3 analysis sees exactly what Phase 4 emits
        var effectiveBaseType = snapshot.BaseType;
        if (effectiveBaseType == null)
        {
            effectiveBaseType = snapshot.Kind switch
            {
                TypeKind.Struct => new TypeReference(
                    Kind: TypeReferenceKind.NamedType,
                    Namespace: "System",
                    TypeName: "ValueType",
                    GenericArgs: new List<TypeReference>(),
                    ArrayRank: 0,
                    PointerDepth: 0,
                    DeclaringType: null,
                    GenericParameter: null,
                    Assembly: null),
                TypeKind.Enum => new TypeReference(
                    Kind: TypeReferenceKind.NamedType,
                    Namespace: "System",
                    TypeName: "Enum",
                    GenericArgs: new List<TypeReference>(),
                    ArrayRank: 0,
                    PointerDepth: 0,
                    DeclaringType: null,
                    GenericParameter: null,
                    Assembly: null),
                TypeKind.Delegate => new TypeReference(
                    Kind: TypeReferenceKind.NamedType,
                    Namespace: "System",
                    TypeName: "MulticastDelegate",
                    GenericArgs: new List<TypeReference>(),
                    ArrayRank: 0,
                    PointerDepth: 0,
                    DeclaringType: null,
                    GenericParameter: null,
                    Assembly: null),
                _ => null
            };
        }

        return new TypeModel(
            snapshot.ClrName,
            snapshot.Kind,
            snapshot.IsStatic,
            snapshot.IsSealed,
            snapshot.IsAbstract,
            snapshot.Visibility,
            genericParams,
            effectiveBaseType,
            snapshot.Implements.ToList(),
            members,
            snapshot.Binding,
            Array.Empty<Diagnostic>(), // Type-level diagnostics added by analysis passes
            Array.Empty<HelperDeclaration>(), // Helpers added by analysis passes
            null, // ConflictingInterfaces - populated by CovarianceConflictPartitioner pass
            false, // HasBaseClassConflicts - populated by CovarianceConflictPartitioner pass
            null, // ConflictingMemberNames - populated by CovarianceConflictPartitioner pass
            null, // ExplicitViews - populated by StructuralConformance pass
            snapshot.UnderlyingType,
            snapshot.EnumMembers,
            snapshot.DelegateParameters?.Select(p => BuildParameter(p)).ToList(),
            snapshot.DelegateReturnType);
    }

    private static MemberCollectionModel BuildMembers(MemberCollection members)
    {
        var constructors = members.Constructors
            .Select(c => new ConstructorModel(
                c.Visibility,
                c.Parameters.Select(p => BuildParameter(p)).ToList()))
            .ToList();

        var methods = members.Methods
            .Select(m => BuildMethod(m))
            .ToList();

        var properties = members.Properties
            .Select(p => BuildProperty(p))
            .ToList();

        var fields = members.Fields
            .Select(f => BuildField(f))
            .ToList();

        var events = members.Events
            .Select(e => BuildEvent(e))
            .ToList();

        return new MemberCollectionModel(constructors, methods, properties, fields, events);
    }

    private static MethodModel BuildMethod(MethodSnapshot snapshot)
    {
        var genericParams = snapshot.GenericParameters
            .Select(gp => new GenericParameterModel(
                gp.Name,
                gp.Constraints.ToList(),
                gp.Variance))
            .ToList();

        return new MethodModel(
            snapshot.ClrName,
            snapshot.IsStatic,
            snapshot.IsVirtual,
            snapshot.IsOverride,
            snapshot.IsAbstract,
            snapshot.Visibility,
            genericParams,
            snapshot.Parameters.Select(p => BuildParameter(p)).ToList(),
            snapshot.ReturnType,
            snapshot.Binding);
    }

    private static PropertyModel BuildProperty(PropertySnapshot snapshot)
    {
        return new PropertyModel(
            snapshot.ClrName,
            snapshot.Type,
            snapshot.IsReadOnly,
            snapshot.IsStatic,
            snapshot.IsVirtual,
            snapshot.IsOverride,
            snapshot.Visibility,
            snapshot.Binding,
            snapshot.ContractType);
    }

    private static FieldModel BuildField(FieldSnapshot snapshot)
    {
        return new FieldModel(
            snapshot.ClrName,
            snapshot.Type,
            snapshot.IsReadOnly,
            snapshot.IsStatic,
            snapshot.Visibility,
            snapshot.Binding);
    }

    private static EventModel BuildEvent(EventSnapshot snapshot)
    {
        return new EventModel(
            snapshot.ClrName,
            snapshot.Type,
            snapshot.IsStatic,
            snapshot.Visibility,
            snapshot.Binding);
    }

    private static ParameterModel BuildParameter(ParameterSnapshot snapshot)
    {
        return new ParameterModel(
            snapshot.Name,
            snapshot.Type,
            snapshot.Kind,
            snapshot.IsOptional,
            snapshot.DefaultValue,
            snapshot.IsParams);
    }

}
