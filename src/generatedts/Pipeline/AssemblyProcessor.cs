using System.Reflection;
using GenerateDts.Config;
using GenerateDts.Mapping;
using GenerateDts.Metadata;
using GenerateDts.Reflection;
using GenerateDts.Analysis;
using GenerateDts.Emit;
using GenerateDts.Model;
using TypeInfo = GenerateDts.Model.TypeInfo;

namespace GenerateDts.Pipeline;

public sealed class AssemblyProcessor
{
    private readonly GeneratorConfig _config;
    private readonly HashSet<string>? _namespaceWhitelist;
    private readonly TypeMapper _typeMapper;
    private readonly SignatureFormatter _signatureFormatter = new();
    private DependencyTracker? _dependencyTracker;

    // Phase 1: Track intersection type aliases for diamond interfaces
    // Key: namespace name, Value: list of intersection aliases to add to that namespace
    private Dictionary<string, List<IntersectionTypeAlias>> _intersectionAliases = new();

    public AssemblyProcessor(GeneratorConfig config, string[] namespaces, bool verbose = false)
    {
        _config = config;
        _namespaceWhitelist = namespaces.Length > 0
            ? new HashSet<string>(namespaces)
            : null;
        _typeMapper = new TypeMapper(verbose);
    }

    public ProcessedAssembly ProcessAssembly(Assembly assembly)
    {
        // Initialize dependency tracker for this assembly
        _dependencyTracker = new DependencyTracker(assembly);

        // Set context for TypeMapper to enable cross-assembly reference rewriting
        _typeMapper.SetContext(assembly, _dependencyTracker);

        // Phase 1: Clear intersection aliases for this assembly
        _intersectionAliases.Clear();

        // Get both exported types (assembly's own types) and forwarded types
        var exportedTypes = assembly.GetExportedTypes();

        Type[] forwardedTypes;
        try
        {
            forwardedTypes = assembly.GetForwardedTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            // Some forwarded types couldn't be loaded due to missing dependencies
            // (e.g., System.Security.Permissions not in ref pack)
            // Use the types that DID load successfully
            forwardedTypes = ex.Types.Where(t => t != null).ToArray()!;
        }

        var allTypes = exportedTypes.Concat(forwardedTypes).Distinct();

        var types = allTypes
            .Where(ShouldIncludeType)
            .OrderBy(t => t.Namespace)
            .ThenBy(t => t.Name)
            .ToList();

        var namespaceGroups = types
            .GroupBy(t => t.Namespace ?? "")
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .OrderBy(g => g.Key);

        var namespaces = new List<NamespaceInfo>();

        foreach (var group in namespaceGroups)
        {
            var typeDeclarations = new List<TypeDeclaration>();

            foreach (var type in group)
            {
                try
                {
                    var declaration = ProcessType(type);
                    if (declaration != null)
                    {
                        typeDeclarations.Add(declaration);
                    }
                }
                catch (Exception ex)
                {
                    var location = $"{assembly.GetName().Name}::{type.FullName}";
                    _typeMapper.AddWarning($"[{location}] Failed to process type: {ex.Message}");
                }
            }

            // Phase 1: Add intersection aliases for this namespace (if any)
            if (_intersectionAliases.TryGetValue(group.Key, out var aliases))
            {
                typeDeclarations.AddRange(aliases);
            }

            if (typeDeclarations.Count > 0)
            {
                namespaces.Add(new NamespaceInfo(group.Key, typeDeclarations));
            }
        }

        return new ProcessedAssembly(namespaces, _typeMapper.Warnings.ToList());
    }

    private bool ShouldIncludeType(Type type)
    {
        return MemberFilters.ShouldIncludeType(type, _config, _namespaceWhitelist);
    }

    private TypeDeclaration? ProcessType(Type type)
    {
        return TypeDispatcher.ProcessType(
            type,
            ProcessEnum,
            ProcessInterface,
            ProcessStaticNamespace,
            ProcessClass);
    }

    private EnumDeclaration ProcessEnum(Type type)
    {
        return EnumEmitter.ProcessEnum(type, TypeNameHelpers.GetTypeName);
    }

    private InterfaceDeclaration ProcessInterface(Type type)
    {
        return InterfaceEmitter.ProcessInterface(
            type,
            _typeMapper,
            ShouldIncludeMember,
            ProcessProperty,
            ProcessMethod,
            TrackTypeDependency,
            _intersectionAliases,
            TypeNameHelpers.GetTypeName);
    }

    private StaticNamespaceDeclaration ProcessStaticNamespace(Type type)
    {
        return StaticNamespaceEmitter.ProcessStaticNamespace(
            type,
            TypeNameHelpers.GetTypeName,
            ShouldIncludeMember,
            ProcessProperty,
            ProcessMethod);
    }

    private ClassDeclaration ProcessClass(Type type)
    {
        return ClassEmitter.ProcessClass(
            type,
            ProcessConstructor,
            ProcessProperty,
            ProcessMethod,
            ShouldIncludeMember,
            GetExplicitInterfaceImplementations,
            HasAnyExplicitImplementation,
            interfaceType =>
            {
                var hasDiamond = InterfaceAnalysis.HasDiamondInheritance(interfaceType, out var ancestors);
                return (hasDiamond, ancestors);
            },
            AddInterfaceCompatibleOverloads,
            AddBaseClassCompatibleOverloads,
            TrackTypeDependency,
            _typeMapper,
            TypeNameHelpers.GetTypeName);
    }

    private TypeInfo.ConstructorInfo ProcessConstructor(System.Reflection.ConstructorInfo ctor)
    {
        return ConstructorEmitter.ProcessConstructor(ctor, ProcessParameter, TrackTypeDependency);
    }

    private TypeInfo.PropertyInfo ProcessProperty(System.Reflection.PropertyInfo prop)
    {
        return PropertyEmitter.ProcessProperty(
            prop,
            _typeMapper,
            propertyType => PropertyEmitter.ApplyCovariantWrapperIfNeeded(
                prop,
                _typeMapper.MapType(propertyType),
                _typeMapper,
                TrackTypeDependency,
                HasAnyExplicitImplementation),
            TrackTypeDependency,
            p => PropertyEmitter.IsRedundantPropertyRedeclaration(p, _typeMapper))!;
    }

    private bool PropertyTypeReferencesTypeParams(Type propertyType, HashSet<string> classTypeParams)
    {
        return TypeReferenceChecker.PropertyTypeReferencesTypeParams(propertyType, classTypeParams);
    }

    private bool TypeReferencesAnyTypeParam(Type type, HashSet<Type> typeParams)
    {
        return TypeReferenceChecker.TypeReferencesAnyTypeParam(type, typeParams);
    }

    private TypeInfo.MethodInfo? ProcessMethod(System.Reflection.MethodInfo method, Type declaringType)
    {
        return MethodEmitter.ProcessMethod(
            method,
            declaringType,
            _typeMapper,
            ProcessParameter,
            TrackTypeDependency);
    }

    private TypeInfo.ParameterInfo ProcessParameter(System.Reflection.ParameterInfo param)
    {
        return MethodEmitter.ProcessParameter(param, _typeMapper);
    }

    private bool ShouldIncludeMember(MemberInfo member)
    {
        return MemberFilters.ShouldIncludeMember(member, _config);
    }

    /// <summary>
    /// Processes assembly and extracts metadata for all types and members.
    /// </summary>
    public AssemblyMetadata ProcessAssemblyMetadata(Assembly assembly)
    {
        // Get both exported types (assembly's own types) and forwarded types
        var exportedTypes = assembly.GetExportedTypes();

        Type[] forwardedTypes;
        try
        {
            forwardedTypes = assembly.GetForwardedTypes();
        }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            // Some forwarded types couldn't be loaded due to missing dependencies
            // (e.g., System.Security.Permissions not in ref pack)
            // Use the types that DID load successfully
            forwardedTypes = ex.Types.Where(t => t != null).ToArray()!;
        }

        var allTypes = exportedTypes.Concat(forwardedTypes).Distinct();

        var types = allTypes
            .Where(ShouldIncludeType)
            .OrderBy(t => t.Namespace)
            .ThenBy(t => t.Name)
            .ToList();

        var typeMetadataDict = new Dictionary<string, TypeMetadata>();

        foreach (var type in types)
        {
            try
            {
                var metadata = ProcessTypeMetadata(type);
                if (metadata != null)
                {
                    var fullName = type.FullName!.Replace('+', '.');
                    typeMetadataDict[fullName] = metadata;
                }
            }
            catch (Exception ex)
            {
                var location = $"{assembly.GetName().Name}::{type.FullName}";
                _typeMapper.AddWarning($"[{location}] Failed to process metadata for type: {ex.Message}");
            }
        }

        return new AssemblyMetadata(
            assembly.GetName().Name ?? assembly.FullName ?? "Unknown",
            assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            typeMetadataDict);
    }

    private TypeMetadata? ProcessTypeMetadata(Type type)
    {
        return MetadataProcessor.ProcessTypeMetadata(type, _signatureFormatter, ShouldIncludeMember);
    }

    private List<(Type interfaceType, System.Reflection.MethodInfo interfaceMethod, System.Reflection.MethodInfo implementation)> GetExplicitInterfaceImplementations(Type type)
    {
        return ExplicitInterfaceAnalyzer.GetExplicitInterfaceImplementations(type);
    }

    private bool HasAnyExplicitImplementation(Type type, Type interfaceType)
    {
        return ExplicitInterfaceAnalyzer.HasAnyExplicitImplementation(type, interfaceType);
    }

    /// <summary>
    /// Adds interface-compatible overloads for methods to handle covariant return types.
    /// This fixes TS2416 (method not assignable) and TS2420 (incorrectly implements interface) errors.
    ///
    /// Note: Properties with covariant types are NOT handled here because TypeScript doesn't allow
    /// multiple property declarations with different types (TS2717). Property type variance is
    /// typically acceptable in TypeScript when the concrete type is more specific than the interface.
    /// </summary>
    private void AddInterfaceCompatibleOverloads(Type type, List<TypeInfo.PropertyInfo> properties, List<TypeInfo.MethodInfo> methods)
    {
        OverloadBuilder.AddInterfaceCompatibleOverloads(type, properties, methods, _typeMapper, ProcessParameter);
    }

    /// <summary>
    /// Adds base class-compatible method and property overloads for TS2416 covariance issues.
    /// When a derived class overrides a base method with a more specific return type,
    /// TypeScript requires both signatures to be present.
    /// </summary>
    private void AddBaseClassCompatibleOverloads(Type type, List<TypeInfo.PropertyInfo> properties, List<TypeInfo.MethodInfo> methods)
    {
        OverloadBuilder.AddBaseClassCompatibleOverloads(type, properties, methods, _typeMapper, ShouldIncludeMember, ProcessParameter, TypeReferencesAnyTypeParam, TrackTypeDependency);
    }

    /// <summary>
    /// Tracks a type dependency for cross-assembly import generation.
    /// Recursively tracks generic type arguments.
    /// </summary>
    private void TrackTypeDependency(Type type)
    {
        DependencyHelpers.TrackTypeDependency(_dependencyTracker, type);
    }

    /// <summary>
    /// Gets the dependency tracker for this assembly processing session.
    /// </summary>
    public DependencyTracker? GetDependencyTracker()
    {
        return _dependencyTracker;
    }

    /// <summary>
    /// Phase 4: Check if a property is redundantly redeclared with same TypeScript type.
    ///
    /// General rule: Walk entire inheritance chain up to System.Object.
    /// If ANY ancestor has a property with same name and same mapped TypeScript type, skip re-emitting.
    ///
    /// Handles edge cases like:
    /// - DataAdapter.TableMappings : ITableMappingCollection
    /// - DbDataAdapter.TableMappings : DataTableMappingCollection (both map to same TS type)
    /// </summary>
}
