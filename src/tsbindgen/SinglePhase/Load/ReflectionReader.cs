using System.Reflection;
using tsbindgen.Core.Renaming;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Model.Symbols;
using tsbindgen.SinglePhase.Model.Symbols.MemberSymbols;

namespace tsbindgen.SinglePhase.Load;

/// <summary>
/// Reads assemblies via reflection and builds the SymbolGraph.
/// Pure CLR facts - no TypeScript concepts yet.
/// </summary>
public sealed class ReflectionReader
{
    private readonly BuildContext _ctx;
    private readonly TypeReferenceFactory _typeFactory;

    public ReflectionReader(BuildContext ctx)
    {
        _ctx = ctx;
        _typeFactory = new TypeReferenceFactory(ctx);
    }

    /// <summary>
    /// Read assemblies and build the complete SymbolGraph.
    /// </summary>
    public SymbolGraph ReadAssemblies(
        MetadataLoadContext loadContext,
        IReadOnlyList<string> assemblyPaths)
    {
        var loader = new AssemblyLoader(_ctx);
        var assemblies = loader.LoadAssemblies(loadContext, assemblyPaths);

        // Group types by namespace
        var namespaceGroups = new Dictionary<string, List<TypeSymbol>>();
        var sourceAssemblies = new HashSet<string>();

        foreach (var assembly in assemblies)
        {
            sourceAssemblies.Add(assembly.Location);
            _ctx.Log($"Reading types from {assembly.GetName().Name}...");

            foreach (var type in assembly.GetTypes())
            {
                // Only process public types
                if (!type.IsPublic && !type.IsNestedPublic)
                    continue;

                var typeSymbol = ReadType(type);
                var ns = typeSymbol.Namespace;

                if (!namespaceGroups.ContainsKey(ns))
                    namespaceGroups[ns] = new List<TypeSymbol>();

                namespaceGroups[ns].Add(typeSymbol);
            }
        }

        // Build namespace symbols
        var namespaces = new List<NamespaceSymbol>();
        foreach (var (ns, types) in namespaceGroups.OrderBy(kvp => kvp.Key))
        {
            var nsStableId = new TypeStableId
            {
                AssemblyName = "Namespace",
                ClrFullName = ns
            };

            var contributingAssemblies = types
                .Select(t => t.StableId.AssemblyName)
                .ToHashSet();

            namespaces.Add(new NamespaceSymbol
            {
                Name = ns,
                Types = types,
                StableId = nsStableId,
                ContributingAssemblies = contributingAssemblies
            });
        }

        return new SymbolGraph
        {
            Namespaces = namespaces,
            SourceAssemblies = sourceAssemblies
        };
    }

    private TypeSymbol ReadType(Type type)
    {
        var stableId = new TypeStableId
        {
            AssemblyName = _ctx.Intern(type.Assembly.GetName().Name ?? "Unknown"),
            ClrFullName = _ctx.Intern(type.FullName ?? type.Name)
        };

        var kind = DetermineTypeKind(type);
        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(_typeFactory.CreateGenericParameterSymbol).ToList()
            : new List<GenericParameterSymbol>();

        var baseType = type.BaseType != null ? _typeFactory.Create(type.BaseType) : null;
        var interfaces = type.GetInterfaces().Select(_typeFactory.Create).ToList();

        // Read members
        var members = ReadMembers(type);

        // Read nested types
        var nestedTypes = type.GetNestedTypes(BindingFlags.Public)
            .Select(ReadType)
            .ToList();

        return new TypeSymbol
        {
            StableId = stableId,
            ClrFullName = _ctx.Intern(type.FullName ?? type.Name),
            ClrName = _ctx.Intern(type.Name),
            Namespace = _ctx.Intern(type.Namespace ?? ""),
            Kind = kind,
            Arity = type.IsGenericType ? type.GetGenericArguments().Length : 0,
            GenericParameters = genericParams,
            BaseType = baseType,
            Interfaces = interfaces,
            Members = members,
            NestedTypes = nestedTypes,
            IsValueType = type.IsValueType,
            IsAbstract = type.IsAbstract,
            IsSealed = type.IsSealed,
            IsStatic = type.IsAbstract && type.IsSealed && !type.IsValueType
        };
    }

    private TypeKind DetermineTypeKind(Type type)
    {
        if (type.IsEnum) return TypeKind.Enum;
        if (type.IsInterface) return TypeKind.Interface;
        if (type.IsSubclassOf(typeof(Delegate)) || type.IsSubclassOf(typeof(MulticastDelegate)))
            return TypeKind.Delegate;
        if (type.IsAbstract && type.IsSealed && !type.IsValueType)
            return TypeKind.StaticNamespace;
        if (type.IsValueType) return TypeKind.Struct;
        return TypeKind.Class;
    }

    private TypeMembers ReadMembers(Type type)
    {
        // Simplified member reading - full implementation would handle all details
        var methods = new List<MethodSymbol>();
        var properties = new List<PropertySymbol>();
        var fields = new List<FieldSymbol>();
        var events = new List<EventSymbol>();
        var constructors = new List<ConstructorSymbol>();

        // TODO: Implement full member reading
        // For now, return empty to allow compilation

        return new TypeMembers
        {
            Methods = methods,
            Properties = properties,
            Fields = fields,
            Events = events,
            Constructors = constructors
        };
    }
}
