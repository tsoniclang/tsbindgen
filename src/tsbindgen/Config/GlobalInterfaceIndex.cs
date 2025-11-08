using System.Reflection;
using System.Runtime.Loader;
using tsbindgen.Snapshot;

namespace tsbindgen.Config;

/// <summary>
/// Global index of all public interfaces loaded in the MetadataLoadContext.
/// Used by StructuralConformance to check conformance for type-forwarded interfaces
/// that don't appear in the emitted namespace models.
/// </summary>
public sealed class GlobalInterfaceIndex
{
    private readonly Dictionary<string, InterfaceSynopsis> _interfaces = new();

    /// <summary>
    /// Builds a GlobalInterfaceIndex by scanning all assembly paths.
    /// Creates a MetadataLoadContext with all assemblies and indexes public interfaces.
    /// </summary>
    public static GlobalInterfaceIndex Build(IEnumerable<string> assemblyPaths)
    {
        var index = new GlobalInterfaceIndex();

        // Find the shared runtime directory from the first assembly path
        var firstAssembly = assemblyPaths.FirstOrDefault();
        if (firstAssembly == null)
            return index;

        var runtimeDir = Path.GetDirectoryName(firstAssembly);
        if (string.IsNullOrEmpty(runtimeDir) || !Directory.Exists(runtimeDir))
            return index;

        // Gather all DLLs in the runtime directory for resolution
        var resolverPaths = Directory.GetFiles(runtimeDir, "*.dll").ToList();

        try
        {
            // Create MetadataLoadContext with all runtime assemblies
            var resolver = new PathAssemblyResolver(resolverPaths);
            using var context = new MetadataLoadContext(resolver);

            // Load each target assembly and index its public interfaces
            foreach (var assemblyPath in assemblyPaths)
            {
                try
                {
                    var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
                    IndexAssembly(index, assembly);
                }
                catch
                {
                    // Skip assemblies that fail to load
                    continue;
                }
            }

            // Also index types from all other loaded assemblies (type-forwarding targets)
            foreach (var assembly in context.GetAssemblies())
            {
                try
                {
                    IndexAssembly(index, assembly);
                }
                catch
                {
                    // Skip assemblies that fail to enumerate types
                    continue;
                }
            }
        }
        catch
        {
            // If MetadataLoadContext creation fails, return empty index
            return index;
        }

        return index;
    }

    /// <summary>
    /// Indexes all public interfaces from an assembly.
    /// </summary>
    private static void IndexAssembly(GlobalInterfaceIndex index, Assembly assembly)
    {
        var exportedTypes = GetExportedTypes(assembly);

        foreach (var type in exportedTypes)
        {
            // Index public interfaces only
            if (type.IsInterface && type.IsPublic)
            {
                var key = InterfaceKey.FromNames(
                    type.Namespace ?? "",
                    GetClrName(type));

                // Skip if already indexed (avoid duplicates)
                if (index._interfaces.ContainsKey(key))
                    continue;

                var synopsis = BuildSynopsis(type);
                index._interfaces[key] = synopsis;
            }
        }
    }

    /// <summary>
    /// Gets the number of indexed interfaces.
    /// </summary>
    public int Count => _interfaces.Count;

    /// <summary>
    /// Tries to get an interface synopsis by key.
    /// </summary>
    public bool TryGetInterface(string key, out InterfaceSynopsis? synopsis)
    {
        return _interfaces.TryGetValue(key, out synopsis);
    }

    /// <summary>
    /// Gets exported types from an assembly, handling load failures gracefully.
    /// </summary>
    private static Type[] GetExportedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Return only the types that loaded successfully
            return ex.Types.Where(t => t != null).Cast<Type>().ToArray();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    /// <summary>
    /// Builds a minimal interface synopsis for conformance checking.
    /// </summary>
    private static InterfaceSynopsis BuildSynopsis(Type interfaceType)
    {
        var methods = new List<MethodSynopsis>();
        var properties = new List<PropertySynopsis>();
        var genericParameters = new List<string>();

        // Extract generic parameters
        if (interfaceType.IsGenericType)
        {
            genericParameters.AddRange(
                interfaceType.GetGenericArguments().Select(t => t.Name));
        }

        // Extract methods (instance only)
        foreach (var method in interfaceType.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            // Skip property accessors (they're handled via properties)
            if (method.IsSpecialName && (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")))
                continue;

            var parameters = method.GetParameters()
                .Select(p => ConvertTypeToReference(p.ParameterType))
                .ToList();

            var returnType = ConvertTypeToReference(method.ReturnType);

            methods.Add(new MethodSynopsis(
                method.Name,
                parameters,
                returnType));
        }

        // Extract properties (instance only, non-indexers)
        foreach (var property in interfaceType.GetProperties(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            // Skip indexers (they have parameters)
            if (property.GetIndexParameters().Length > 0)
                continue;

            var propertyType = ConvertTypeToReference(property.PropertyType);

            properties.Add(new PropertySynopsis(
                property.Name,
                propertyType));
        }

        return new InterfaceSynopsis(
            interfaceType.Namespace ?? "",
            GetClrName(interfaceType),
            genericParameters,
            methods,
            properties);
    }

    /// <summary>
    /// Converts a System.Reflection.Type to our TypeReference format.
    /// </summary>
    private static TypeReference ConvertTypeToReference(Type type)
    {
        // Handle generic parameters
        if (type.IsGenericParameter)
        {
            return new TypeReference(
                Kind: TypeReferenceKind.GenericParameter,
                Namespace: null,
                TypeName: type.Name,
                GenericArgs: new List<TypeReference>(),
                ArrayRank: 0,
                PointerDepth: 0,
                DeclaringType: null,
                GenericParameter: null); // TODO: Create GenericParameterInfo if needed
        }

        // Handle arrays
        if (type.IsArray)
        {
            var elementType = ConvertTypeToReference(type.GetElementType()!);
            return elementType with { ArrayRank = type.GetArrayRank() };
        }

        // Handle pointers
        if (type.IsPointer)
        {
            var elementType = ConvertTypeToReference(type.GetElementType()!);
            return elementType with { PointerDepth = elementType.PointerDepth + 1 };
        }

        // Handle by-ref (skip the ref wrapper and use element type)
        if (type.IsByRef)
        {
            return ConvertTypeToReference(type.GetElementType()!);
        }

        // Handle generic type instantiations
        var genericArgs = new List<TypeReference>();
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            genericArgs.AddRange(
                type.GetGenericArguments().Select(ConvertTypeToReference));
        }

        return new TypeReference(
            Kind: TypeReferenceKind.NamedType,
            Namespace: type.Namespace,
            TypeName: GetClrName(type),
            GenericArgs: genericArgs,
            ArrayRank: 0,
            PointerDepth: 0,
            DeclaringType: null, // TODO: Handle nested types if needed
            GenericParameter: null);
    }

    /// <summary>
    /// Gets the CLR name for a type (with generic arity suffix if applicable).
    /// Examples: "List`1", "Dictionary`2", "IEnumerable`1"
    /// </summary>
    private static string GetClrName(Type type)
    {
        if (type.IsGenericType)
        {
            var name = type.Name;
            // For generic types, Name already includes backtick and arity (e.g., "List`1")
            return name;
        }

        return type.Name;
    }
}

/// <summary>
/// Minimal interface surface for structural conformance checking.
/// Contains only the information needed to compare signatures.
/// </summary>
public sealed record InterfaceSynopsis(
    string Namespace,
    string ClrName,
    IReadOnlyList<string> GenericParameters,
    IReadOnlyList<MethodSynopsis> Methods,
    IReadOnlyList<PropertySynopsis> Properties);

/// <summary>
/// Method signature for conformance checking.
/// </summary>
public sealed record MethodSynopsis(
    string Name,
    IReadOnlyList<TypeReference> Parameters,
    TypeReference ReturnType);

/// <summary>
/// Property signature for conformance checking.
/// </summary>
public sealed record PropertySynopsis(
    string Name,
    TypeReference Type);
