using System.Reflection;

namespace GenerateDts;

public sealed class AssemblyProcessor
{
    private readonly GeneratorConfig _config;
    private readonly HashSet<string>? _namespaceWhitelist;
    private readonly TypeMapper _typeMapper = new();
    private readonly SignatureFormatter _signatureFormatter = new();
    private DependencyTracker? _dependencyTracker;

    // Phase 1: Track intersection type aliases for diamond interfaces
    // Key: namespace name, Value: list of intersection aliases to add to that namespace
    private Dictionary<string, List<IntersectionTypeAlias>> _intersectionAliases = new();

    public AssemblyProcessor(GeneratorConfig config, string[] namespaces)
    {
        _config = config;
        _namespaceWhitelist = namespaces.Length > 0
            ? new HashSet<string>(namespaces)
            : null;
    }

    public ProcessedAssembly ProcessAssembly(Assembly assembly)
    {
        // Initialize dependency tracker for this assembly
        _dependencyTracker = new DependencyTracker(assembly);

        // Set context for TypeMapper to enable cross-assembly reference rewriting
        _typeMapper.SetContext(assembly, _dependencyTracker);

        // Phase 1: Clear intersection aliases for this assembly
        _intersectionAliases.Clear();

        var types = assembly.GetExportedTypes()
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
                    _typeMapper.AddWarning($"Failed to process type {type.FullName}: {ex.Message}\nStack: {ex.StackTrace}");
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
        // Skip if not public
        if (!type.IsPublic && !type.IsNestedPublic)
        {
            return false;
        }

        // Skip compiler-generated types
        if (type.Name.Contains('<') || type.Name.Contains('>'))
        {
            return false;
        }

        // Skip if namespace is in skip list
        if (_config.SkipNamespaces.Contains(type.Namespace ?? ""))
        {
            return false;
        }

        // Apply whitelist if provided
        if (_namespaceWhitelist != null)
        {
            if (type.Namespace == null)
            {
                return false;
            }

            // Check if namespace or any parent namespace is in whitelist
            var ns = type.Namespace;
            while (!string.IsNullOrEmpty(ns))
            {
                if (_namespaceWhitelist.Contains(ns))
                {
                    return true;
                }

                var lastDot = ns.LastIndexOf('.');
                if (lastDot < 0) break;
                ns = ns.Substring(0, lastDot);
            }

            return false;
        }

        return true;
    }

    private TypeDeclaration? ProcessType(Type type)
    {
        return TypeProcessing.ProcessType(
            type,
            ProcessEnum,
            ProcessInterface,
            ProcessStaticNamespace,
            ProcessClass);
    }

    private EnumDeclaration ProcessEnum(Type type)
    {
        return EnumEmitter.ProcessEnum(type, GetTypeName);
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
            GetTypeName);
    }

    private StaticNamespaceDeclaration ProcessStaticNamespace(Type type)
    {
        return StaticNamespaceEmitter.ProcessStaticNamespace(
            type,
            GetTypeName,
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
            GetTypeName);
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
        // Check if this type is a generic parameter
        if (propertyType.IsGenericParameter && classTypeParams.Contains(propertyType.Name))
        {
            return true;
        }

        // Check if this is a generic type that uses the class's type parameters
        if (propertyType.IsGenericType)
        {
            var typeArgs = propertyType.GetGenericArguments();
            foreach (var arg in typeArgs)
            {
                if (PropertyTypeReferencesTypeParams(arg, classTypeParams))
                {
                    return true;
                }
            }
        }

        // Check arrays
        if (propertyType.IsArray)
        {
            return PropertyTypeReferencesTypeParams(propertyType.GetElementType()!, classTypeParams);
        }

        return false;
    }

    private bool TypeReferencesAnyTypeParam(Type type, HashSet<Type> typeParams)
    {
        // Check if this type IS a type parameter
        if (type.IsGenericParameter && typeParams.Contains(type))
        {
            return true;
        }

        // Check if this is a generic type that uses any of the type parameters
        if (type.IsGenericType)
        {
            var typeArgs = type.GetGenericArguments();
            foreach (var arg in typeArgs)
            {
                if (TypeReferencesAnyTypeParam(arg, typeParams))
                {
                    return true;
                }
            }
        }

        // Check arrays
        if (type.IsArray)
        {
            return TypeReferencesAnyTypeParam(type.GetElementType()!, typeParams);
        }

        return false;
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
        var fullMemberName = $"{member.DeclaringType?.FullName}::{member.Name}";

        if (_config.SkipMembers.Contains(fullMemberName))
        {
            return false;
        }

        // Skip common Object methods unless explicitly needed
        if (member.Name is "Equals" or "GetHashCode" or "GetType" or "ToString" or "ReferenceEquals")
        {
            return false;
        }

        return true;
    }

    private string GetTypeName(Type type)
    {
        var baseName = type.Name;
        var arity = 0;

        // Handle generic types - extract arity and strip the `N suffix
        if (type.IsGenericType)
        {
            var backtickIndex = baseName.IndexOf('`');
            if (backtickIndex > 0)
            {
                // Extract arity (e.g., "Tuple`3" -> arity = 3)
                if (int.TryParse(baseName.Substring(backtickIndex + 1), out var parsedArity))
                {
                    arity = parsedArity;
                }
                baseName = baseName.Substring(0, backtickIndex);
            }
        }

        // Handle nested types - build full ancestry chain to avoid conflicts
        // For deeply nested types like Dictionary<K,V>.KeyCollection.Enumerator,
        // we need to include the top-level type's arity to distinguish from other variants
        if (type.IsNested && type.DeclaringType != null)
        {
            // Walk up the nesting chain to find the top-level type
            var ancestorChain = new List<(string name, int arity)>();
            var current = type.DeclaringType;

            while (current != null)
            {
                var ancestorName = current.Name;
                var ancestorArity = 0;

                var backtickIndex = ancestorName.IndexOf('`');
                if (backtickIndex > 0)
                {
                    if (int.TryParse(ancestorName.Substring(backtickIndex + 1), out var parsedArity))
                    {
                        ancestorArity = parsedArity;
                    }
                    ancestorName = ancestorName.Substring(0, backtickIndex);
                }

                ancestorChain.Insert(0, (ancestorName, ancestorArity));
                current = current.DeclaringType;
            }

            // Build name from ancestor chain
            var nameBuilder = new System.Text.StringBuilder();
            foreach (var (ancestorName, ancestorArity) in ancestorChain)
            {
                if (nameBuilder.Length > 0)
                {
                    nameBuilder.Append('_');
                }

                nameBuilder.Append(ancestorName);
                if (ancestorArity > 0)
                {
                    nameBuilder.Append('_');
                    nameBuilder.Append(ancestorArity);
                }
            }

            // Append the current type
            nameBuilder.Append('_');
            nameBuilder.Append(baseName);
            if (arity > 0)
            {
                nameBuilder.Append('_');
                nameBuilder.Append(arity);
            }

            return nameBuilder.ToString();
        }

        // For top-level generic types, include arity to distinguish Tuple<T1> from Tuple<T1,T2>
        // Example: Tuple`1 becomes Tuple_1, Tuple`2 becomes Tuple_2
        if (arity > 0)
        {
            return $"{baseName}_{arity}";
        }

        return baseName;
    }

    /// <summary>
    /// Processes assembly and extracts metadata for all types and members.
    /// </summary>
    public AssemblyMetadata ProcessAssemblyMetadata(Assembly assembly)
    {
        var types = assembly.GetExportedTypes()
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
                _typeMapper.AddWarning($"Failed to process metadata for type {type.FullName}: {ex.Message}");
            }
        }

        return new AssemblyMetadata(
            assembly.GetName().Name ?? assembly.FullName ?? "Unknown",
            assembly.GetName().Version?.ToString() ?? "0.0.0.0",
            typeMetadataDict);
    }

    private TypeMetadata? ProcessTypeMetadata(Type type)
    {
        // Determine the kind of type
        string kind;
        if (type.IsEnum)
        {
            kind = "enum";
        }
        else if (type.IsInterface)
        {
            kind = "interface";
        }
        else if (type.IsValueType)
        {
            kind = "struct";
        }
        else
        {
            kind = "class";
        }

        // Get type-level flags
        bool isAbstract = type.IsAbstract && !type.IsInterface && !type.IsSealed;
        bool isSealed = type.IsSealed && !type.IsValueType && !type.IsEnum;
        bool isStatic = type.IsAbstract && type.IsSealed && type.IsClass;

        // Get base type (if any)
        string? baseType = null;
        if (type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
        {
            baseType = type.BaseType.FullName?.Replace('+', '.');
        }

        // Get interfaces
        var interfaces = type.GetInterfaces()
            .Where(i => i.IsPublic)
            .Select(i => i.FullName?.Replace('+', '.') ?? i.Name)
            .ToList();

        // Process members
        var memberMetadataDict = new Dictionary<string, MemberMetadata>();

        // Process constructors (skip for interfaces and enums)
        if (!type.IsInterface && !type.IsEnum)
        {
            var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            foreach (var ctor in constructors)
            {
                var signature = _signatureFormatter.FormatConstructor(ctor);
                var metadata = ProcessConstructorMetadata(ctor);
                memberMetadataDict[signature] = metadata;
            }
        }

        // Process properties
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(ShouldIncludeMember);

        foreach (var prop in properties)
        {
            var signature = _signatureFormatter.FormatProperty(prop);
            var metadata = ProcessPropertyMetadata(prop);
            memberMetadataDict[signature] = metadata;
        }

        // Process methods (skip special methods like property getters/setters)
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(ShouldIncludeMember)
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.Name.Contains('.')); // Skip explicit interface implementations

        foreach (var method in methods)
        {
            var signature = _signatureFormatter.FormatMethod(method);
            var metadata = ProcessMethodMetadata(method);
            memberMetadataDict[signature] = metadata;
        }

        return new TypeMetadata(
            kind,
            isAbstract,
            isSealed,
            isStatic,
            baseType,
            interfaces,
            memberMetadataDict);
    }

    private MemberMetadata ProcessConstructorMetadata(System.Reflection.ConstructorInfo ctor)
    {
        return new MemberMetadata(
            "constructor",
            IsVirtual: false,
            IsAbstract: false,
            IsSealed: false,
            IsOverride: false,
            IsStatic: false,
            Accessibility: GetAccessibility(ctor));
    }

    private MemberMetadata ProcessPropertyMetadata(System.Reflection.PropertyInfo prop)
    {
        // For properties, check the getter method for virtual/override information
        var getter = prop.GetMethod;
        var setter = prop.SetMethod;
        var accessMethod = getter ?? setter;

        bool isVirtual = accessMethod?.IsVirtual == true && !accessMethod.IsFinal;
        bool isAbstract = accessMethod?.IsAbstract == true;
        bool isSealed = accessMethod?.IsFinal == true && accessMethod.IsVirtual;
        bool isOverride = IsOverrideMethod(accessMethod);
        bool isStatic = accessMethod?.IsStatic ?? false;

        // Check if this is an indexer (has index parameters)
        bool isIndexer = prop.GetIndexParameters().Length > 0;

        return new MemberMetadata(
            "property",
            isVirtual,
            isAbstract,
            isSealed,
            isOverride,
            isStatic,
            GetAccessibility(accessMethod),
            IsIndexer: isIndexer ? true : null);
    }

    private MemberMetadata ProcessMethodMetadata(System.Reflection.MethodInfo method)
    {
        bool isVirtual = method.IsVirtual && !method.IsFinal;
        bool isAbstract = method.IsAbstract;
        bool isSealed = method.IsFinal && method.IsVirtual;
        bool isOverride = IsOverrideMethod(method);
        bool isStatic = method.IsStatic;

        return new MemberMetadata(
            "method",
            isVirtual,
            isAbstract,
            isSealed,
            isOverride,
            isStatic,
            GetAccessibility(method));
    }

    private bool IsOverrideMethod(MethodInfo? method)
    {
        if (method == null || !method.IsVirtual)
        {
            return false;
        }

        // A method is an override if its base definition is different from itself
        var baseDefinition = method.GetBaseDefinition();
        return baseDefinition != method;
    }

    private List<(Type interfaceType, System.Reflection.MethodInfo interfaceMethod, System.Reflection.MethodInfo implementation)> GetExplicitInterfaceImplementations(Type type)
    {
        var result = new List<(Type, System.Reflection.MethodInfo, System.Reflection.MethodInfo)>();

        try
        {
            var interfaces = type.GetInterfaces();
            foreach (var iface in interfaces)
            {
                try
                {
                    var map = type.GetInterfaceMap(iface);
                    for (int i = 0; i < map.TargetMethods.Length; i++)
                    {
                        var targetMethod = map.TargetMethods[i];
                        var interfaceMethod = map.InterfaceMethods[i];

                        // Explicit implementation = not public
                        // For classes: private + virtual
                        // For structs: private (can't be virtual)
                        // Method name will be like "System.IDisposable.Dispose"
                        if (!targetMethod.IsPublic)
                        {
                            result.Add((iface, interfaceMethod, targetMethod));
                        }
                    }
                }
                catch
                {
                    // GetInterfaceMap can fail for some types in MetadataLoadContext
                    // Skip and continue
                }
            }
        }
        catch
        {
            // Type may not support interface mapping
        }

        return result;
    }

    private bool HasAnyExplicitImplementation(Type type, Type interfaceType)
    {
        // Check if the given interface has ANY members that are explicitly implemented (not public)
        // This includes both methods and property accessors
        try
        {
            var map = type.GetInterfaceMap(interfaceType);

            // Check all methods
            var allMethodsPublic = map.TargetMethods.All(m => m.IsPublic);
            if (!allMethodsPublic)
                return true;

            // Check all property accessors
            var allAccessorsPublic = interfaceType
                .GetProperties()
                .SelectMany(p => p.GetAccessors(nonPublic: true))
                .All(a => a.IsPublic);

            if (!allAccessorsPublic)
                return true;

            return false; // All members are public
        }
        catch
        {
            // GetInterfaceMap can fail for some types in MetadataLoadContext
            // Be conservative: if we can't check, assume explicit to avoid TypeScript errors
            return true;
        }
    }

    private string GetAccessibility(MethodBase? method)
    {
        if (method == null) return "public";

        if (method.IsPublic) return "public";
        if (method.IsFamily) return "protected";
        if (method.IsFamilyOrAssembly) return "protected internal";
        if (method.IsFamilyAndAssembly) return "private protected";
        if (method.IsAssembly) return "internal";
        if (method.IsPrivate) return "private";

        return "public";
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
