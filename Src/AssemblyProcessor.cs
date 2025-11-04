using System.Reflection;

namespace GenerateDts;

public sealed class AssemblyProcessor
{
    private readonly GeneratorConfig _config;
    private readonly HashSet<string>? _namespaceWhitelist;
    private readonly TypeMapper _typeMapper = new();
    private readonly SignatureFormatter _signatureFormatter = new();
    private DependencyTracker? _dependencyTracker;

    /// <summary>
    /// TypeScript/JavaScript reserved keywords and special identifiers.
    /// </summary>
    private static readonly HashSet<string> TypeScriptReservedKeywords = new(StringComparer.Ordinal)
    {
        // Keywords
        "break", "case", "catch", "class", "const", "continue", "debugger",
        "default", "delete", "do", "else", "enum", "export", "extends",
        "false", "finally", "for", "function", "if", "import", "in",
        "instanceof", "new", "null", "return", "super", "switch", "this",
        "throw", "true", "try", "typeof", "var", "void", "while", "with",

        // Strict / future reserved
        "implements", "interface", "let", "package", "private", "protected",
        "public", "static", "yield", "async", "await",

        // Problematic identifiers
        "arguments", "eval"
    };

    /// <summary>
    /// Prefixes parameter names that conflict with TypeScript keywords.
    /// </summary>
    private static string EscapeParameterName(string name)
    {
        return TypeScriptReservedKeywords.Contains(name)
            ? $"_{name}"
            : name;
    }

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
        if (type.IsEnum)
        {
            return ProcessEnum(type);
        }
        else if (type.IsInterface)
        {
            return ProcessInterface(type);
        }
        else if (type.IsClass || type.IsValueType)
        {
            // Skip delegate types - they're mapped to function types in TypeMapper
            if (IsDelegate(type))
            {
                return null;
            }

            // Check if this is a static-only type
            if (IsStaticOnly(type))
            {
                return ProcessStaticNamespace(type);
            }
            return ProcessClass(type);
        }

        return null;
    }

    private static bool IsDelegate(Type type)
    {
        // Check if type inherits from System.Delegate or System.MulticastDelegate
        // Use name-based comparison for MetadataLoadContext compatibility
        var baseType = type.BaseType;
        while (baseType != null)
        {
            var baseName = baseType.FullName;
            if (baseName == "System.Delegate" || baseName == "System.MulticastDelegate")
            {
                return true;
            }
            baseType = baseType.BaseType;
        }
        return false;
    }

    /// <summary>
    /// Checks if an interface should be excluded from implements clause.
    /// Interfaces that map to ReadonlyArray<T> cause TS2420 errors because
    /// they require array methods that .NET collections don't have.
    /// </summary>
    private static bool ShouldSkipInterfaceInImplementsClause(Type iface)
    {
        var fullName = iface.FullName;
        if (fullName == null) return false;

        // Skip interfaces that map to ReadonlyArray<T>
        // These require array methods (length, concat, join, slice, etc.) that .NET collections don't have
        return fullName.StartsWith("System.Collections.Generic.IEnumerable`") ||
               fullName.StartsWith("System.Collections.Generic.IReadOnlyList`") ||
               fullName.StartsWith("System.Collections.Generic.IReadOnlyCollection`");
    }

    private static bool IsStaticOnly(Type type)
    {
        // Static class in C# (abstract sealed)
        if (type.IsAbstract && type.IsSealed)
        {
            return true;
        }

        // ValueType cannot be static-only
        if (type.IsValueType)
        {
            return false;
        }

        // Check for public instance members
        var instanceMembers = type.GetMembers(BindingFlags.Public | BindingFlags.Instance);
        if (instanceMembers.Length > 0)
        {
            return false;
        }

        // Check for public instance constructors
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (constructors.Any(c => !c.IsPrivate))
        {
            return false;
        }

        // Must have static members to be considered static-only
        var staticMembers = type.GetMembers(BindingFlags.Public | BindingFlags.Static);
        return staticMembers.Length > 0;
    }

    private EnumDeclaration ProcessEnum(Type type)
    {
        // Use GetFields() for MetadataLoadContext compatibility instead of Enum.GetValues()
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static);
        var members = fields
            .Select(f => new EnumMember(
                f.Name,
                Convert.ToInt64(f.GetRawConstantValue())))
            .ToList();

        return new EnumDeclaration(
            GetTypeName(type), // Use GetTypeName() for nested types (e.g., Environment_SpecialFolder)
            type.FullName!,
            false,
            Array.Empty<string>(),
            members);
    }

    private InterfaceDeclaration ProcessInterface(Type type)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(ShouldIncludeMember)
            .Select(ProcessProperty)
            .Where(p => p != null)
            .ToList();

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(ShouldIncludeMember)
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.Name.Contains('.')) // Skip explicit interface implementations early
            .Select(m => ProcessMethod(m, type))
            .OfType<TypeInfo.MethodInfo>() // Filter nulls and cast to non-nullable
            .ToList();

        var extends = type.GetInterfaces()
            .Where(i => i.FullName != "System.IDisposable") // Often not needed in TS (name-based for MetadataLoadContext)
            .Select(i => _typeMapper.MapType(i))
            .Where(mapped => !mapped.StartsWith("ReadonlyArray<")) // Skip interfaces that map to ReadonlyArray<T>
            .Distinct() // Remove duplicates
            .ToList();

        // Track base interface dependencies
        foreach (var iface in type.GetInterfaces().Where(i => i.FullName != "System.IDisposable"))
        {
            TrackTypeDependency(iface);
        }

        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(t => t.Name).ToList()
            : new List<string>();

        return new InterfaceDeclaration(
            GetTypeName(type),
            type.FullName!,
            type.IsGenericType,
            genericParams,
            extends,
            properties,
            methods);
    }

    private StaticNamespaceDeclaration ProcessStaticNamespace(Type type)
    {
        // For static-only types, only process static members
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(ShouldIncludeMember)
            .Select(ProcessProperty)
            .Where(p => p != null)
            .ToList();

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(ShouldIncludeMember)
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.Name.Contains('.')) // Skip explicit interface implementations early
            .Select(m => ProcessMethod(m, type))
            .OfType<TypeInfo.MethodInfo>() // Filter nulls and cast to non-nullable
            .ToList();

        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(t => t.Name).ToList()
            : new List<string>();

        return new StaticNamespaceDeclaration(
            GetTypeName(type),
            type.FullName!,
            type.IsGenericType,
            genericParams,
            properties,
            methods);
    }

    private ClassDeclaration ProcessClass(Type type)
    {
        var constructors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Select(ProcessConstructor)
            .ToList();

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(ShouldIncludeMember)
            .Select(ProcessProperty)
            .Where(p => p != null)
            .ToList();

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(ShouldIncludeMember)
            .Where(m => !m.IsSpecialName)
            .Where(m => !m.Name.Contains('.')) // Skip explicit interface implementations early
            .Select(m => ProcessMethod(m, type))
            .OfType<TypeInfo.MethodInfo>() // Filter nulls and cast to non-nullable
            .ToList();

        // Add public wrappers for explicit interface implementations
        // These won't appear in TypeScript implements clause but are needed for metadata
        var explicitImplementations = GetExplicitInterfaceImplementations(type);
        foreach (var (interfaceType, interfaceMethod, implementation) in explicitImplementations)
        {
            // All explicit implementations are kept for metadata, no filtering needed here

            // Check if this is a property getter/setter (special name)
            if (interfaceMethod.IsSpecialName && interfaceMethod.Name.StartsWith("get_"))
            {
                // Property getter - emit as readonly property
                var propName = interfaceMethod.Name.Substring(4); // Remove "get_"
                var propType = _typeMapper.MapType(interfaceMethod.ReturnType);

                // Check if we already have this property (from public implementation)
                if (!properties.Any(p => p.Name == propName))
                {
                    properties.Add(new TypeInfo.PropertyInfo(propName, propType, true, false));
                }
            }
            else if (interfaceMethod.IsSpecialName && interfaceMethod.Name.StartsWith("set_"))
            {
                // Property setter - usually handled with getter, skip
                continue;
            }
            else if (!interfaceMethod.IsSpecialName)
            {
                // Regular method - emit as public method
                // Check if we already have this method (from public implementation)
                if (!methods.Any(m => m.Name == interfaceMethod.Name))
                {
                    var processedMethod = ProcessMethod(interfaceMethod, type);
                    if (processedMethod != null)
                    {
                        methods.Add(processedMethod);
                    }
                }
            }
        }

        // Add interface-compatible overloads for all interface members
        // This handles TS2416 (covariant return types) and remaining TS2420 (interface implementation)
        AddInterfaceCompatibleOverloads(type, properties, methods);

        // Add base class-compatible overloads for all base class members
        // This handles TS2416 (method covariance) when derived classes override with more specific types
        AddBaseClassCompatibleOverloads(type, properties, methods);

        // Use name-based comparison for MetadataLoadContext compatibility
        // (typeof(object) returns runtime type, but type.BaseType returns MetadataLoadContext type)
        var baseType = type.BaseType != null
            && type.BaseType.FullName != "System.Object"
            && type.BaseType.FullName != "System.ValueType"
            ? _typeMapper.MapType(type.BaseType)
            : null;

        // Track base type dependency
        if (type.BaseType != null
            && type.BaseType.FullName != "System.Object"
            && type.BaseType.FullName != "System.ValueType")
        {
            TrackTypeDependency(type.BaseType);
        }

        // Filter interfaces for TypeScript implements clause
        // General rule: only include interfaces where ALL members are publicly implemented
        var interfaces = type.GetInterfaces()
            .Where(i => i.IsPublic)
            .Where(i => !HasAnyExplicitImplementation(type, i)) // Skip explicitly implemented interfaces
            .Select(i => _typeMapper.MapType(i))
            .Where(mapped => !mapped.StartsWith("ReadonlyArray<")) // Skip interfaces that map to ReadonlyArray<T>
            .Distinct() // Remove duplicates
            .ToList();

        // Track interface dependencies
        foreach (var iface in type.GetInterfaces().Where(i => i.IsPublic))
        {
            TrackTypeDependency(iface);
        }

        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(t => t.Name).ToList()
            : new List<string>();

        return new ClassDeclaration(
            GetTypeName(type),
            type.FullName!,
            type.IsGenericType,
            genericParams,
            baseType,
            interfaces,
            constructors,
            properties,
            methods,
            type.IsAbstract && type.IsSealed); // Static class
    }

    private TypeInfo.ConstructorInfo ProcessConstructor(System.Reflection.ConstructorInfo ctor)
    {
        // Track parameter type dependencies
        foreach (var param in ctor.GetParameters())
        {
            TrackTypeDependency(param.ParameterType);
        }

        var parameters = ctor.GetParameters()
            .Select(ProcessParameter)
            .ToList();

        return new TypeInfo.ConstructorInfo(parameters);
    }

    private TypeInfo.PropertyInfo ProcessProperty(System.Reflection.PropertyInfo prop)
    {
        // Skip indexers (properties with index parameters like this[int index])
        // These cause TS2300 duplicate identifier errors when multiple indexers exist
        // with the same name but different parameter types
        var indexParams = prop.GetIndexParameters();
        if (indexParams.Length > 0)
        {
            // Log for visibility - indexers are tracked in metadata
            _typeMapper.AddWarning($"Skipped indexer {prop.DeclaringType?.Name}.{prop.Name} - " +
                $"indexers with parameters cannot be represented as TypeScript properties (TS2300)");
            return null!; // Will be filtered out
        }

        var isStatic = prop.GetMethod?.IsStatic ?? prop.SetMethod?.IsStatic ?? false;

        // TypeScript: static properties cannot reference class type parameters
        // Skip static properties in generic classes to avoid TS2302 errors
        if (isStatic && prop.DeclaringType != null && prop.DeclaringType.IsGenericType)
        {
            // Check if property type references any class type parameters
            var classTypeParams = prop.DeclaringType.GetGenericArguments().Select(t => t.Name).ToHashSet();
            if (PropertyTypeReferencesTypeParams(prop.PropertyType, classTypeParams))
            {
                _typeMapper.AddWarning($"Skipped static property {prop.DeclaringType.Name}.{prop.Name} - " +
                    $"references class type parameters (TS2302: Static members cannot reference class type parameters)");
                return null!; // Will be filtered out
            }
        }

        // Track property type dependency
        TrackTypeDependency(prop.PropertyType);

        // P2: Apply Covariant wrapper for known property covariance patterns
        var mappedType = _typeMapper.MapType(prop.PropertyType);
        var propertyType = ApplyCovariantWrapperIfNeeded(prop, mappedType);

        return new TypeInfo.PropertyInfo(
            prop.Name,
            propertyType,
            !prop.CanWrite,
            isStatic);
    }

    /// <summary>
    /// P2: Apply Covariant wrapper for property covariance patterns.
    /// Dictionary Keys/Values properties return more specific types than interfaces require.
    /// Use Covariant<TSpecific, TContract> to satisfy both runtime and interface contracts.
    /// </summary>
    private string ApplyCovariantWrapperIfNeeded(System.Reflection.PropertyInfo prop, string mappedType)
    {
        // Only apply to readonly properties
        if (prop.CanWrite)
            return mappedType;

        // P2: Dictionary Keys/Values covariance
        // Pattern: Keys property returns ICollection_1<TKey> but interface expects ICollection
        if (prop.Name == "Keys" || prop.Name == "Values")
        {
            var declaringType = prop.DeclaringType;
            if (declaringType == null)
                return mappedType;

            // Check if this is a dictionary-like type
            var isDictionary = declaringType.GetInterfaces().Any(i =>
                i.FullName == "System.Collections.IDictionary" ||
                i.FullName == "System.Collections.Generic.IDictionary`2" ||
                i.FullName == "System.Collections.Generic.IReadOnlyDictionary`2");

            if (isDictionary)
            {
                // Keys property: wrap specific type with non-generic contract
                if (prop.Name == "Keys" && mappedType.Contains("ICollection_1<"))
                {
                    // Specific: ICollection_1<TKey>, Contract: ICollection
                    return $"Covariant<{mappedType}, System_Private_CoreLib.System.Collections.ICollection>";
                }

                // Values property: wrap specific type with non-generic contract
                if (prop.Name == "Values" && mappedType.Contains("ICollection_1<"))
                {
                    // Specific: ICollection_1<TValue>, Contract: ICollection
                    return $"Covariant<{mappedType}, System_Private_CoreLib.System.Collections.ICollection>";
                }
            }
        }

        return mappedType;
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

    private TypeInfo.MethodInfo? ProcessMethod(System.Reflection.MethodInfo method, Type declaringType)
    {
        // Skip explicit interface implementations (TS1434, TS1068)
        // C# explicit interface implementations have dots in their method names
        // Example: "System.IUtf8SpanFormattable.TryFormat" - invalid TypeScript syntax
        if (method.Name.Contains('.'))
        {
            _typeMapper.AddWarning($"Skipped explicit interface implementation {declaringType.Name}.{method.Name} - " +
                $"method name contains dot (TS1434: Unexpected keyword or identifier)");
            return null; // Will be filtered out
        }

        // Track return type dependency
        TrackTypeDependency(method.ReturnType);

        // Track parameter type dependencies
        foreach (var param in method.GetParameters())
        {
            TrackTypeDependency(param.ParameterType);
        }

        var parameters = method.GetParameters()
            .Select(ProcessParameter)
            .ToList();

        var genericParams = new List<string>();
        var isGeneric = method.IsGenericMethod;

        // TypeScript: static methods cannot reference class type parameters
        // Solution: If this is a static method in a generic class, add the class's type parameters
        if (method.IsStatic && declaringType.IsGenericType)
        {
            // Start with class type parameters
            var classTypeParams = declaringType.GetGenericArguments().Select(t => t.Name).ToList();

            if (method.IsGenericMethod)
            {
                // Method already has its own type parameters - prepend class params
                var methodTypeParams = method.GetGenericArguments().Select(t => t.Name).ToList();
                genericParams = classTypeParams.Concat(methodTypeParams).ToList();
            }
            else
            {
                // Method has no generic params - use class params
                genericParams = classTypeParams;
            }

            isGeneric = genericParams.Count > 0;
        }
        else if (method.IsGenericMethod)
        {
            // Non-static method with its own type parameters
            genericParams = method.GetGenericArguments().Select(t => t.Name).ToList();
        }

        return new TypeInfo.MethodInfo(
            method.Name,
            _typeMapper.MapType(method.ReturnType),
            parameters,
            method.IsStatic,
            isGeneric,
            genericParams);
    }

    private TypeInfo.ParameterInfo ProcessParameter(System.Reflection.ParameterInfo param)
    {
        // Use GetCustomAttributesData() for MetadataLoadContext compatibility
        var isParams = param.GetCustomAttributesData()
            .Any(a => a.AttributeType.FullName == "System.ParamArrayAttribute");

        var paramType = isParams && param.ParameterType.IsArray
            ? param.ParameterType.GetElementType()!
            : param.ParameterType;

        var originalName = param.Name ?? $"arg{param.Position}";
        var safeName = EscapeParameterName(originalName);

        return new TypeInfo.ParameterInfo(
            safeName,
            _typeMapper.MapType(paramType),
            param.IsOptional || param.HasDefaultValue,
            isParams);
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
        try
        {
            var interfaces = type.GetInterfaces();
            foreach (var iface in interfaces)
            {
                try
                {
                    var map = type.GetInterfaceMap(iface);
                    for (int i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        var interfaceMethod = map.InterfaceMethods[i];

                        // Skip property getters/setters - they can't have overloads in TypeScript
                        if (interfaceMethod.IsSpecialName)
                        {
                            continue;
                        }

                        // Skip explicit interface implementations (method name contains dot)
                        if (interfaceMethod.Name.Contains('.'))
                        {
                            continue;
                        }

                        // Regular method - add interface-compatible overload
                        var interfaceReturnType = _typeMapper.MapType(interfaceMethod.ReturnType);
                        var interfaceParams = interfaceMethod.GetParameters()
                            .Select(ProcessParameter)
                            .ToList();

                        // Check if we already have this exact method signature
                        var hasExactMatch = methods.Any(m =>
                            m.Name == interfaceMethod.Name &&
                            m.ReturnType == interfaceReturnType &&
                            ParameterListsMatch(m.Parameters, interfaceParams));

                        if (!hasExactMatch)
                        {
                            // Add interface-compatible method signature
                            var genericParams = interfaceMethod.IsGenericMethod
                                ? interfaceMethod.GetGenericArguments().Select(t => t.Name).ToList()
                                : new List<string>();

                            methods.Add(new TypeInfo.MethodInfo(
                                interfaceMethod.Name,
                                interfaceReturnType,
                                interfaceParams,
                                false, // Instance method (interface methods are never static)
                                interfaceMethod.IsGenericMethod,
                                genericParams));
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
    }

    /// <summary>
    /// Adds base class-compatible method and property overloads for TS2416 covariance issues.
    /// When a derived class overrides a base method with a more specific return type,
    /// TypeScript requires both signatures to be present.
    /// </summary>
    private void AddBaseClassCompatibleOverloads(Type type, List<TypeInfo.PropertyInfo> properties, List<TypeInfo.MethodInfo> methods)
    {
        if (type.BaseType == null
            || type.BaseType.FullName == "System.Object"
            || type.BaseType.FullName == "System.ValueType"
            || type.BaseType.FullName == "System.MarshalByRefObject")
        {
            return; // No base class to process
        }

        try
        {
            AddBaseClassOverloadsRecursive(type.BaseType, properties, methods);
        }
        catch
        {
            // Base class may not be accessible in MetadataLoadContext
        }
    }

    /// <summary>
    /// Recursively adds base class overloads from the entire inheritance chain.
    /// </summary>
    private void AddBaseClassOverloadsRecursive(Type baseType, List<TypeInfo.PropertyInfo> properties, List<TypeInfo.MethodInfo> methods)
    {
        if (baseType == null
            || baseType.FullName == "System.Object"
            || baseType.FullName == "System.ValueType"
            || baseType.FullName == "System.MarshalByRefObject")
        {
            return;
        }

        try
        {
            // Process base class methods
            var baseMethods = baseType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(ShouldIncludeMember)
                .Where(m => !m.IsSpecialName)
                .Where(m => !m.Name.Contains('.')); // Skip explicit interface implementations

            foreach (var baseMethod in baseMethods)
            {
                try
                {
                    // Track dependency for base method return type and parameters
                    TrackTypeDependency(baseMethod.ReturnType);
                    foreach (var param in baseMethod.GetParameters())
                    {
                        TrackTypeDependency(param.ParameterType);
                    }

                    var baseReturnType = _typeMapper.MapType(baseMethod.ReturnType);
                    var baseParams = baseMethod.GetParameters()
                        .Select(ProcessParameter)
                        .ToList();

                    // Check if we already have this exact method signature
                    var hasExactMatch = methods.Any(m =>
                        m.Name == baseMethod.Name &&
                        m.ReturnType == baseReturnType &&
                        ParameterListsMatch(m.Parameters, baseParams));

                    if (!hasExactMatch)
                    {
                        // Add base class-compatible method signature
                        var genericParams = baseMethod.IsGenericMethod
                            ? baseMethod.GetGenericArguments().Select(t => t.Name).ToList()
                            : new List<string>();

                        methods.Add(new TypeInfo.MethodInfo(
                            baseMethod.Name,
                            baseReturnType,
                            baseParams,
                            false, // Instance method (base methods are not static in this context)
                            baseMethod.IsGenericMethod,
                            genericParams));
                    }
                }
                catch
                {
                    // Skip methods that can't be processed
                }
            }

            // Process base class properties (for covariant property return types)
            var baseProperties = baseType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(ShouldIncludeMember);

            foreach (var baseProp in baseProperties)
            {
                try
                {
                    // Skip indexers
                    if (baseProp.GetIndexParameters().Length > 0)
                        continue;

                    TrackTypeDependency(baseProp.PropertyType);

                    var basePropertyType = _typeMapper.MapType(baseProp.PropertyType);

                    // Check if we already have a property with this name
                    // (Properties cannot be overloaded in TypeScript, unlike methods)
                    var hasProperty = properties.Any(p => p.Name == baseProp.Name);

                    if (!hasProperty)
                    {
                        // Add base class-compatible property signature
                        var isStatic = baseProp.GetMethod?.IsStatic ?? baseProp.SetMethod?.IsStatic ?? false;

                        properties.Add(new TypeInfo.PropertyInfo(
                            baseProp.Name,
                            basePropertyType,
                            !baseProp.CanWrite,
                            isStatic));
                    }
                }
                catch
                {
                    // Skip properties that can't be processed
                }
            }

            // Recurse up the inheritance chain
            if (baseType.BaseType != null)
            {
                AddBaseClassOverloadsRecursive(baseType.BaseType, properties, methods);
            }
        }
        catch
        {
            // Base type may not be accessible
        }
    }

    /// <summary>
    /// Checks if two parameter lists have matching types.
    /// </summary>
    private bool ParameterListsMatch(IReadOnlyList<TypeInfo.ParameterInfo> params1, IReadOnlyList<TypeInfo.ParameterInfo> params2)
    {
        if (params1.Count != params2.Count) return false;

        for (int i = 0; i < params1.Count; i++)
        {
            if (params1[i].Type != params2[i].Type) return false;
        }

        return true;
    }

    /// <summary>
    /// Tracks a type dependency for cross-assembly import generation.
    /// Recursively tracks generic type arguments.
    /// </summary>
    private void TrackTypeDependency(Type type)
    {
        if (_dependencyTracker == null) return;

        // Track the type itself
        _dependencyTracker.RecordTypeReference(type);

        // Track generic type arguments
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                TrackTypeDependency(arg);
            }
        }

        // Track array element type
        if (type.IsArray)
        {
            var elementType = type.GetElementType();
            if (elementType != null)
            {
                TrackTypeDependency(elementType);
            }
        }

        // Track by-ref element type
        if (type.IsByRef || type.IsPointer)
        {
            var elementType = type.GetElementType();
            if (elementType != null)
            {
                TrackTypeDependency(elementType);
            }
        }
    }

    /// <summary>
    /// Gets the dependency tracker for this assembly processing session.
    /// </summary>
    public DependencyTracker? GetDependencyTracker()
    {
        return _dependencyTracker;
    }
}
