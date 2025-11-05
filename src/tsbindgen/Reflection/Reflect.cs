using System.Reflection;
using tsbindgen.Config;
using tsbindgen.Snapshot;

namespace tsbindgen.Reflection;

/// <summary>
/// Pure functional reflection over .NET assemblies.
/// Extracts CLR metadata and produces AssemblySnapshot (pure data).
/// </summary>
public static class Reflect
{
    /// <summary>
    /// Reflects over an assembly and produces a pure CLR snapshot.
    /// </summary>
    public static AssemblySnapshot Assembly(
        Assembly assembly,
        string assemblyPath,
        GeneratorConfig config,
        string[]? namespaceFilter = null,
        bool verbose = false)
    {
        var assemblyName = assembly.GetName().Name ?? Path.GetFileNameWithoutExtension(assemblyPath);
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

        // Get all exported types
        var types = GetExportedTypes(assembly, verbose);

        // Group types by namespace, excluding compiler-generated types
        var namespaceGroups = types
            .Where(t => ShouldIncludeNamespace(t.Namespace, namespaceFilter))
            .Where(t => !IsCompilerGenerated(t))
            .GroupBy(t => t.Namespace ?? "")
            .OrderBy(g => g.Key)
            .ToList();

        // Convert each namespace group to NamespaceSnapshot
        var namespaceSnapshots = namespaceGroups
            .Select(g => ReflectNamespace(g.Key, g.ToList(), assembly, verbose))
            .ToList();

        return new AssemblySnapshot(
            assemblyName,
            Path.GetFullPath(assemblyPath),
            timestamp,
            namespaceSnapshots);
    }

    /// <summary>
    /// Reflects over a namespace and its types.
    /// </summary>
    private static NamespaceSnapshot ReflectNamespace(
        string namespaceName,
        List<Type> types,
        Assembly assembly,
        bool verbose)
    {
        var diagnostics = new List<Diagnostic>();
        var typeSnapshots = new List<TypeSnapshot>();

        foreach (var type in types)
        {
            try
            {
                var snapshot = ReflectType(type, assembly);
                typeSnapshots.Add(snapshot);
            }
            catch (Exception ex)
            {
                diagnostics.Add(new Diagnostic(
                    "REFLECT001",
                    DiagnosticSeverity.Warning,
                    $"Failed to reflect type {type.FullName}: {ex.Message}"));

                if (verbose)
                {
                    Console.Error.WriteLine($"  Warning: {type.FullName} - {ex.Message}");
                }
            }
        }

        // Extract cross-assembly dependencies
        var imports = ExtractDependencies(typeSnapshots);

        return new NamespaceSnapshot(
            namespaceName,
            typeSnapshots,
            imports,
            diagnostics);
    }

    /// <summary>
    /// Reflects over a single type.
    /// </summary>
    private static TypeSnapshot ReflectType(Type type, Assembly assembly)
    {
        var kind = DetermineTypeKind(type);
        var visibility = DetermineVisibility(type);

        // Reflect members
        var members = kind == TypeKind.Enum
            ? new MemberCollection([], [], [], [], [])
            : ReflectMembers(type);

        // Base type and interfaces
        var baseType = type.BaseType != null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType)
            ? CreateTypeReference(type.BaseType, assembly)
            : null;

        var interfaces = type.GetInterfaces()
            .Select(i => CreateTypeReference(i, assembly))
            .ToList();

        var snapshot = new TypeSnapshot(
            type.Name,
            type.FullName ?? type.Name,
            kind,
            type.IsAbstract && type.IsSealed, // IsStatic
            type.IsSealed,
            type.IsAbstract,
            visibility,
            ReflectGenericParameters(type),
            baseType,
            interfaces,
            members,
            new BindingInfo(assembly.GetName().Name ?? "", type.FullName ?? type.Name));

        // Enum-specific
        if (kind == TypeKind.Enum)
        {
            return snapshot with
            {
                UnderlyingType = Enum.GetUnderlyingType(type).Name,
                EnumMembers = ReflectEnumMembers(type)
            };
        }

        // Delegate-specific
        if (kind == TypeKind.Delegate)
        {
            var invokeMethod = type.GetMethod("Invoke");
            if (invokeMethod != null)
            {
                return snapshot with
                {
                    DelegateParameters = ReflectParameters(invokeMethod.GetParameters(), assembly),
                    DelegateReturnType = CreateTypeReference(invokeMethod.ReturnType, assembly)
                };
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Reflects all members of a type.
    /// </summary>
    private static MemberCollection ReflectMembers(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        var constructors = type.GetConstructors(flags)
            .Where(c => c.IsPublic)
            .Select(ReflectConstructor)
            .ToList();

        var methods = type.GetMethods(flags)
            .Where(m => m.IsPublic && !m.IsSpecialName)
            .Select(ReflectMethod)
            .ToList();

        var properties = type.GetProperties(flags)
            .Where(p => (p.GetMethod?.IsPublic ?? false) || (p.SetMethod?.IsPublic ?? false))
            .Select(ReflectProperty)
            .ToList();

        var fields = type.GetFields(flags)
            .Where(f => f.IsPublic)
            .Select(ReflectField)
            .ToList();

        var events = type.GetEvents(flags)
            .Where(e => e.AddMethod?.IsPublic ?? false)
            .Select(ReflectEvent)
            .ToList();

        return new MemberCollection(constructors, methods, properties, fields, events);
    }

    /// <summary>
    /// Reflects a constructor.
    /// </summary>
    private static ConstructorSnapshot ReflectConstructor(ConstructorInfo ctor)
    {
        var assembly = ctor.DeclaringType?.Assembly;
        return new ConstructorSnapshot(
            DetermineVisibility(ctor),
            ReflectParameters(ctor.GetParameters(), assembly!));
    }

    /// <summary>
    /// Reflects a method.
    /// </summary>
    private static MethodSnapshot ReflectMethod(MethodInfo method)
    {
        var assembly = method.DeclaringType?.Assembly;
        return new MethodSnapshot(
            method.Name,
            method.IsStatic,
            method.IsVirtual,
            method.IsVirtual && method.GetBaseDefinition() != method,
            method.IsAbstract,
            DetermineVisibility(method),
            ReflectGenericParameters(method),
            ReflectParameters(method.GetParameters(), assembly!),
            CreateTypeReference(method.ReturnType, assembly!),
            new MemberBinding(
                assembly?.GetName().Name ?? "",
                method.DeclaringType?.FullName ?? "",
                method.Name));
    }

    /// <summary>
    /// Reflects a property.
    /// </summary>
    private static PropertySnapshot ReflectProperty(PropertyInfo property)
    {
        var assembly = property.DeclaringType?.Assembly;
        var getter = property.GetMethod;
        var setter = property.SetMethod;

        var isStatic = (getter?.IsStatic ?? setter?.IsStatic) ?? false;
        var isVirtual = (getter?.IsVirtual ?? setter?.IsVirtual) ?? false;
        var isOverride = isVirtual && ((getter?.GetBaseDefinition() != getter) || (setter?.GetBaseDefinition() != setter));
        var visibility = DetermineVisibility(getter ?? setter!);

        var typeRef = CreateTypeReference(property.PropertyType, assembly!);

        return new PropertySnapshot(
            property.Name,
            typeRef.ClrType,
            typeRef.TsType,
            setter == null,
            isStatic,
            isVirtual,
            isOverride,
            visibility,
            new MemberBinding(
                assembly?.GetName().Name ?? "",
                property.DeclaringType?.FullName ?? "",
                property.Name));
    }

    /// <summary>
    /// Reflects a field.
    /// </summary>
    private static FieldSnapshot ReflectField(FieldInfo field)
    {
        var assembly = field.DeclaringType?.Assembly;
        var typeRef = CreateTypeReference(field.FieldType, assembly!);

        return new FieldSnapshot(
            field.Name,
            typeRef.ClrType,
            typeRef.TsType,
            field.IsInitOnly,
            field.IsStatic,
            DetermineVisibility(field),
            new MemberBinding(
                assembly?.GetName().Name ?? "",
                field.DeclaringType?.FullName ?? "",
                field.Name));
    }

    /// <summary>
    /// Reflects an event.
    /// </summary>
    private static EventSnapshot ReflectEvent(EventInfo evt)
    {
        var assembly = evt.DeclaringType?.Assembly;
        var addMethod = evt.AddMethod;
        var typeRef = CreateTypeReference(evt.EventHandlerType!, assembly!);

        return new EventSnapshot(
            evt.Name,
            typeRef.ClrType,
            typeRef.TsType,
            addMethod?.IsStatic ?? false,
            DetermineVisibility(addMethod!),
            new MemberBinding(
                assembly?.GetName().Name ?? "",
                evt.DeclaringType?.FullName ?? "",
                evt.Name));
    }

    /// <summary>
    /// Reflects enum members.
    /// </summary>
    private static IReadOnlyList<EnumMember> ReflectEnumMembers(Type enumType)
    {
        return Enum.GetNames(enumType)
            .Select(name => new EnumMember(name, Convert.ToInt64(Enum.Parse(enumType, name))))
            .ToList();
    }

    /// <summary>
    /// Reflects method/type generic parameters.
    /// </summary>
    private static IReadOnlyList<GenericParameter> ReflectGenericParameters(Type type)
    {
        if (!type.IsGenericType && !type.IsGenericTypeDefinition)
            return [];

        return type.GetGenericArguments()
            .Where(t => t.IsGenericParameter)
            .Select(t => new GenericParameter(
                t.Name,
                t.GetGenericParameterConstraints().Select(c => FormatTypeName(c, type.Assembly)).ToList(),
                DetermineVariance(t)))
            .ToList();
    }

    private static IReadOnlyList<GenericParameter> ReflectGenericParameters(MethodInfo method)
    {
        if (!method.IsGenericMethod && !method.IsGenericMethodDefinition)
            return [];

        return method.GetGenericArguments()
            .Where(t => t.IsGenericParameter)
            .Select(t => new GenericParameter(
                t.Name,
                t.GetGenericParameterConstraints().Select(c => FormatTypeName(c, method.DeclaringType!.Assembly)).ToList(),
                Variance.None)) // Methods don't have variance
            .ToList();
    }

    /// <summary>
    /// Reflects method parameters.
    /// </summary>
    private static IReadOnlyList<ParameterSnapshot> ReflectParameters(
        ParameterInfo[] parameters,
        Assembly assembly)
    {
        return parameters
            .Select(p => ReflectParameter(p, assembly))
            .ToList();
    }

    private static ParameterSnapshot ReflectParameter(ParameterInfo param, Assembly assembly)
    {
        var typeRef = CreateTypeReference(param.ParameterType, assembly);
        var kind = param.IsOut ? ParameterKind.Out :
                   param.ParameterType.IsByRef ? ParameterKind.Ref :
                   ParameterKind.In;

        return new ParameterSnapshot(
            param.Name ?? $"arg{param.Position}",
            typeRef.ClrType,
            typeRef.TsType,
            kind,
            param.IsOptional,
            param.DefaultValue?.ToString(),
            param.GetCustomAttribute<ParamArrayAttribute>() != null);
    }

    /// <summary>
    /// Creates a type reference from a System.Type.
    /// </summary>
    private static TypeReference CreateTypeReference(Type type, Assembly currentAssembly)
    {
        // Handle ref/out parameters - strip the & suffix
        var actualType = type.IsByRef ? type.GetElementType()! : type;

        // Handle pointer types - map to 'any'
        if (actualType.IsPointer)
        {
            return new TypeReference("void*", "any", null);
        }

        // Handle array types
        if (actualType.IsArray)
        {
            var elementType = actualType.GetElementType()!;
            var elementRef = CreateTypeReference(elementType, currentAssembly);
            var rank = actualType.GetArrayRank();
            var brackets = string.Concat(Enumerable.Repeat("[]", rank));
            return new TypeReference(
                elementRef.ClrType + brackets,
                elementRef.TsType + brackets,
                elementRef.Assembly
            );
        }

        var assemblyName = actualType.Assembly.GetName().Name;
        var isCrossAssembly = actualType.Assembly != currentAssembly;
        var assemblyAlias = isCrossAssembly ? assemblyName?.Replace(".", "_") : null;

        // Format type name with proper generic syntax
        var typeName = FormatTypeName(actualType, currentAssembly);
        var qualifiedName = assemblyAlias != null ? $"{assemblyAlias}.{typeName}" : typeName;

        return new TypeReference(qualifiedName, qualifiedName, assemblyAlias);
    }

    /// <summary>
    /// Formats a type name with generic arity and arguments for TypeScript.
    /// Converts: List`1[[System.Int32, ...]] to List_1<System.Int32>
    /// Converts nested types: Outer+Inner to Outer.Inner
    /// </summary>
    private static string FormatTypeName(Type type, Assembly currentAssembly)
    {
        // Non-generic types: use FullName, replace + with . for nested types
        if (!type.IsGenericType)
            return (type.FullName ?? type.Name).Replace('+', '.');

        // Generic type definition (e.g., List`1): add underscore arity, replace + with .
        if (type.IsGenericTypeDefinition)
        {
            var name = type.FullName ?? type.Name;
            return name.Replace('`', '_').Replace('+', '.');
        }

        // Constructed generic type (e.g., List<int>): format with TypeScript syntax
        var genericDef = type.GetGenericTypeDefinition();
        var baseName = (genericDef.FullName ?? genericDef.Name).Replace('`', '_').Replace('+', '.');

        var typeArgs = type.GetGenericArguments();
        var formattedArgs = typeArgs.Select(arg => {
            var argRef = CreateTypeReference(arg, currentAssembly);
            return argRef.TsType;
        });

        return $"{baseName}<{string.Join(", ", formattedArgs)}>";
    }

    /// <summary>
    /// Extracts cross-assembly dependencies from type snapshots.
    /// </summary>
    private static IReadOnlyList<DependencyRef> ExtractDependencies(List<TypeSnapshot> types)
    {
        var deps = new HashSet<(string Assembly, string Namespace)>();

        foreach (var type in types)
        {
            ExtractTypeReferenceDependencies(type.BaseType, deps);
            foreach (var iface in type.Implements)
                ExtractTypeReferenceDependencies(iface, deps);

            foreach (var method in type.Members.Methods)
            {
                ExtractTypeReferenceDependencies(method.ReturnType, deps);
                foreach (var param in method.Parameters)
                {
                    // Extract from clrType string
                    if (param.ClrType.Contains('.'))
                        ExtractFromTypeString(param.ClrType, deps);
                }
            }

            foreach (var prop in type.Members.Properties)
            {
                if (prop.ClrType.Contains('.'))
                    ExtractFromTypeString(prop.ClrType, deps);
            }
        }

        return deps.Select(d => new DependencyRef(d.Assembly, d.Namespace)).ToList();
    }

    private static void ExtractTypeReferenceDependencies(
        TypeReference? typeRef,
        HashSet<(string, string)> deps)
    {
        if (typeRef?.Assembly == null) return;

        var parts = typeRef.ClrType.Split('.');
        if (parts.Length >= 2)
        {
            var ns = string.Join(".", parts.Take(parts.Length - 1));
            deps.Add((typeRef.Assembly, ns));
        }
    }

    private static void ExtractFromTypeString(string typeString, HashSet<(string, string)> deps)
    {
        // Parse "AssemblyAlias.Namespace.Type" format
        var parts = typeString.Split('.');
        if (parts.Length >= 3 && parts[0].Contains('_'))
        {
            // Cross-assembly reference
            var assembly = parts[0];
            var ns = string.Join(".", parts.Skip(1).Take(parts.Length - 2));
            deps.Add((assembly, ns));
        }
    }

    // ============================================================================
    // Helper functions
    // ============================================================================

    private static Type[] GetExportedTypes(Assembly assembly, bool verbose)
    {
        try
        {
            return assembly.GetExportedTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            if (verbose)
            {
                Console.Error.WriteLine($"  Warning: Could not load some types from {assembly.GetName().Name}");
                foreach (var loaderEx in ex.LoaderExceptions.Where(e => e != null).Take(3))
                {
                    Console.Error.WriteLine($"    {loaderEx!.Message}");
                }
            }
            return ex.Types.Where(t => t != null).ToArray()!;
        }
    }

    private static bool ShouldIncludeNamespace(string? ns, string[]? filter)
    {
        if (filter == null || filter.Length == 0)
            return true;

        if (ns == null)
            return false;

        return filter.Any(f => ns.Equals(f, StringComparison.Ordinal) || ns.StartsWith(f + ".", StringComparison.Ordinal));
    }

    private static bool IsCompilerGenerated(Type type)
    {
        // Compiler-generated types have unspeakable names containing < and >
        // Examples: "<Module>", "<PrivateImplementationDetails>", "<G>$A9DC899..."
        var name = type.Name;
        return name.Contains('<') || name.Contains('>');
    }

    private static TypeKind DetermineTypeKind(Type type)
    {
        if (type.IsEnum) return TypeKind.Enum;
        if (type.IsValueType) return TypeKind.Struct;
        if (type.IsInterface) return TypeKind.Interface;
        if (typeof(Delegate).IsAssignableFrom(type)) return TypeKind.Delegate;
        if (type.IsAbstract && type.IsSealed) return TypeKind.StaticNamespace;
        return TypeKind.Class;
    }

    private static string DetermineVisibility(Type type)
    {
        return type.IsPublic || type.IsNestedPublic ? "public" :
               type.IsNestedFamily ? "protected" :
               type.IsNestedPrivate ? "private" :
               "internal";
    }

    private static string DetermineVisibility(MethodBase method)
    {
        return method.IsPublic ? "public" :
               method.IsFamily ? "protected" :
               method.IsPrivate ? "private" :
               "internal";
    }

    private static string DetermineVisibility(FieldInfo field)
    {
        return field.IsPublic ? "public" :
               field.IsFamily ? "protected" :
               field.IsPrivate ? "private" :
               "internal";
    }

    private static Variance DetermineVariance(Type genericParam)
    {
        var attributes = genericParam.GenericParameterAttributes;
        if ((attributes & GenericParameterAttributes.Covariant) != 0)
            return Variance.Out;
        if ((attributes & GenericParameterAttributes.Contravariant) != 0)
            return Variance.In;
        return Variance.None;
    }
}
