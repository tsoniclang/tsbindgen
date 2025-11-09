using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;
using tsbindgen.SinglePhase.Normalize;
using tsbindgen.SinglePhase.Plan;

namespace tsbindgen.SinglePhase.Emit;

/// <summary>
/// Emits metadata.json files with provenance and CLR-specific information.
/// Includes member provenance, emit scopes, and transformation decisions.
/// </summary>
public static class MetadataEmitter
{
    public static void Emit(BuildContext ctx, EmissionPlan plan, string outputDirectory)
    {
        ctx.Log("MetadataEmitter", "Generating metadata.json files...");

        var emittedCount = 0;

        // Process each namespace in order
        foreach (var nsOrder in plan.EmissionOrder.Namespaces)
        {
            var ns = nsOrder.Namespace;
            ctx.Log("MetadataEmitter", $"  Emitting metadata for: {ns.Name}");

            // Generate metadata
            var metadata = GenerateMetadata(ctx, nsOrder);

            // Write to file: output/Namespace.Name/internal/metadata.json
            var namespacePath = Path.Combine(outputDirectory, ns.Name);
            var internalPath = Path.Combine(namespacePath, "internal");
            Directory.CreateDirectory(internalPath);

            var outputFile = Path.Combine(internalPath, "metadata.json");
            var jsonOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            var json = JsonSerializer.Serialize(metadata, jsonOptions);
            File.WriteAllText(outputFile, json);

            ctx.Log("MetadataEmitter", $"    → {outputFile}");
            emittedCount++;
        }

        ctx.Log("MetadataEmitter", $"Generated {emittedCount} metadata files");
    }

    private static NamespaceMetadata GenerateMetadata(BuildContext ctx, NamespaceEmitOrder nsOrder)
    {
        var typeMetadata = new List<TypeMetadata>();

        foreach (var typeOrder in nsOrder.OrderedTypes)
        {
            typeMetadata.Add(GenerateTypeMetadata(typeOrder.Type, ctx));
        }

        return new NamespaceMetadata
        {
            Namespace = nsOrder.Namespace.Name,
            ContributingAssemblies = nsOrder.Namespace.ContributingAssemblies.ToList(),
            Types = typeMetadata
        };
    }

    private static TypeMetadata GenerateTypeMetadata(TypeSymbol type, BuildContext ctx)
    {
        // Get final TypeScript name from Renamer
        var nsScope = new Core.Renaming.NamespaceScope
        {
            Namespace = type.Namespace,
            IsInternal = true,
            ScopeKey = $"ns:{type.Namespace}:internal"
        };
        var tsEmitName = ctx.Renamer.GetFinalTypeName(type.StableId, nsScope);

        return new TypeMetadata
        {
            ClrName = type.ClrFullName,
            TsEmitName = tsEmitName,
            Kind = type.Kind.ToString(),
            Accessibility = type.Accessibility.ToString(),
            IsAbstract = type.IsAbstract,
            IsSealed = type.IsSealed,
            IsStatic = type.IsStatic,
            Arity = type.Arity,
            Methods = type.Members.Methods.Select(m => GenerateMethodMetadata(m, type, ctx)).ToList(),
            Properties = type.Members.Properties.Select(p => GeneratePropertyMetadata(p, type, ctx)).ToList(),
            Fields = type.Members.Fields.Select(f => GenerateFieldMetadata(f, type, ctx)).ToList(),
            Events = type.Members.Events.Select(e => GenerateEventMetadata(e, type, ctx)).ToList(),
            Constructors = type.Members.Constructors.Select(c => GenerateConstructorMetadata(c, type, ctx)).ToList()
        };
    }

    private static MethodMetadata GenerateMethodMetadata(MethodSymbol method, TypeSymbol declaringType, BuildContext ctx)
    {
        // Get final TS name from Renamer
        var typeScope = new Core.Renaming.TypeScope
        {
            TypeFullName = declaringType.ClrFullName,
            IsStatic = method.IsStatic,
            ScopeKey = $"type:{declaringType.ClrFullName}#{(method.IsStatic ? "static" : "instance")}"
        };
        var tsEmitName = ctx.Renamer.GetFinalMemberName(method.StableId, typeScope, method.IsStatic);

        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeMethod(method);

        return new MethodMetadata
        {
            ClrName = method.ClrName,
            TsEmitName = tsEmitName,
            NormalizedSignature = normalizedSignature,
            Provenance = method.Provenance.ToString(),
            EmitScope = method.EmitScope.ToString(),
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract,
            IsVirtual = method.IsVirtual,
            IsOverride = method.IsOverride,
            IsSealed = method.IsSealed,
            Arity = method.Arity,
            ParameterCount = method.Parameters.Length,
            SourceInterface = method.SourceInterface != null ? GetTypeRefName(method.SourceInterface) : null
        };
    }

    private static PropertyMetadata GeneratePropertyMetadata(PropertySymbol property, TypeSymbol declaringType, BuildContext ctx)
    {
        // Get final TS name from Renamer
        var typeScope = new Core.Renaming.TypeScope
        {
            TypeFullName = declaringType.ClrFullName,
            IsStatic = property.IsStatic,
            ScopeKey = $"type:{declaringType.ClrFullName}#{(property.IsStatic ? "static" : "instance")}"
        };
        var tsEmitName = ctx.Renamer.GetFinalMemberName(property.StableId, typeScope, property.IsStatic);

        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeProperty(property);

        return new PropertyMetadata
        {
            ClrName = property.ClrName,
            TsEmitName = tsEmitName,
            NormalizedSignature = normalizedSignature,
            Provenance = property.Provenance.ToString(),
            EmitScope = property.EmitScope.ToString(),
            IsStatic = property.IsStatic,
            IsAbstract = property.IsAbstract,
            IsVirtual = property.IsVirtual,
            IsOverride = property.IsOverride,
            IsIndexer = property.IsIndexer,
            HasGetter = property.HasGetter,
            HasSetter = property.HasSetter,
            SourceInterface = property.SourceInterface != null ? GetTypeRefName(property.SourceInterface) : null
        };
    }

    private static FieldMetadata GenerateFieldMetadata(FieldSymbol field, TypeSymbol declaringType, BuildContext ctx)
    {
        // Get final TS name from Renamer
        var typeScope = new Core.Renaming.TypeScope
        {
            TypeFullName = declaringType.ClrFullName,
            IsStatic = field.IsStatic,
            ScopeKey = $"type:{declaringType.ClrFullName}#{(field.IsStatic ? "static" : "instance")}"
        };
        var tsEmitName = ctx.Renamer.GetFinalMemberName(field.StableId, typeScope, field.IsStatic);

        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeField(field);

        return new FieldMetadata
        {
            ClrName = field.ClrName,
            TsEmitName = tsEmitName,
            NormalizedSignature = normalizedSignature,
            IsStatic = field.IsStatic,
            IsReadOnly = field.IsReadOnly,
            IsLiteral = field.IsConst
        };
    }

    private static EventMetadata GenerateEventMetadata(EventSymbol evt, TypeSymbol declaringType, BuildContext ctx)
    {
        // Get final TS name from Renamer
        var typeScope = new Core.Renaming.TypeScope
        {
            TypeFullName = declaringType.ClrFullName,
            IsStatic = evt.IsStatic,
            ScopeKey = $"type:{declaringType.ClrFullName}#{(evt.IsStatic ? "static" : "instance")}"
        };
        var tsEmitName = ctx.Renamer.GetFinalMemberName(evt.StableId, typeScope, evt.IsStatic);

        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeEvent(evt);

        return new EventMetadata
        {
            ClrName = evt.ClrName,
            TsEmitName = tsEmitName,
            NormalizedSignature = normalizedSignature,
            IsStatic = evt.IsStatic
        };
    }

    private static ConstructorMetadata GenerateConstructorMetadata(ConstructorSymbol ctor, TypeSymbol declaringType, BuildContext ctx)
    {
        // Constructors always have name "constructor" in TypeScript, but still get it from Renamer for consistency
        // Generate normalized signature for universal matching
        var normalizedSignature = SignatureNormalization.NormalizeConstructor(ctor);

        return new ConstructorMetadata
        {
            NormalizedSignature = normalizedSignature,
            IsStatic = ctor.IsStatic,
            ParameterCount = ctor.Parameters.Length
        };
    }

    private static string GetTypeRefName(Model.Types.TypeReference typeRef)
    {
        return typeRef switch
        {
            Model.Types.NamedTypeReference named => named.FullName,
            Model.Types.NestedTypeReference nested => nested.FullReference.FullName,
            Model.Types.GenericParameterReference gp => gp.Name,
            _ => typeRef.ToString() ?? "Unknown"
        };
    }
}

/// <summary>
/// Metadata for a namespace.
/// </summary>
public sealed record NamespaceMetadata
{
    public required string Namespace { get; init; }
    public required List<string> ContributingAssemblies { get; init; }
    public required List<TypeMetadata> Types { get; init; }
}

/// <summary>
/// Metadata for a type.
/// </summary>
public sealed record TypeMetadata
{
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string Kind { get; init; }
    public required string Accessibility { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsSealed { get; init; }
    public required bool IsStatic { get; init; }
    public required int Arity { get; init; }
    public required List<MethodMetadata> Methods { get; init; }
    public required List<PropertyMetadata> Properties { get; init; }
    public required List<FieldMetadata> Fields { get; init; }
    public required List<EventMetadata> Events { get; init; }
    public required List<ConstructorMetadata> Constructors { get; init; }
}

/// <summary>
/// Metadata for a method.
/// </summary>
public sealed record MethodMetadata
{
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string NormalizedSignature { get; init; }
    public required string Provenance { get; init; }
    public required string EmitScope { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsOverride { get; init; }
    public required bool IsSealed { get; init; }
    public required int Arity { get; init; }
    public required int ParameterCount { get; init; }
    public string? SourceInterface { get; init; }
}

/// <summary>
/// Metadata for a property.
/// </summary>
public sealed record PropertyMetadata
{
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string NormalizedSignature { get; init; }
    public required string Provenance { get; init; }
    public required string EmitScope { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsAbstract { get; init; }
    public required bool IsVirtual { get; init; }
    public required bool IsOverride { get; init; }
    public required bool IsIndexer { get; init; }
    public required bool HasGetter { get; init; }
    public required bool HasSetter { get; init; }
    public string? SourceInterface { get; init; }
}

/// <summary>
/// Metadata for a field.
/// </summary>
public sealed record FieldMetadata
{
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required bool IsReadOnly { get; init; }
    public required bool IsLiteral { get; init; }
}

/// <summary>
/// Metadata for an event.
/// </summary>
public sealed record EventMetadata
{
    public required string ClrName { get; init; }
    public required string TsEmitName { get; init; }
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
}

/// <summary>
/// Metadata for a constructor.
/// </summary>
public sealed record ConstructorMetadata
{
    public required string NormalizedSignature { get; init; }
    public required bool IsStatic { get; init; }
    public required int ParameterCount { get; init; }
}
