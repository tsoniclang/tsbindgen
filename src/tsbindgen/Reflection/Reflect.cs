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

        // Determine if this is an override (GetBaseDefinition not supported in MetadataLoadContext)
        var isOverride = false;
        if (method.IsVirtual)
        {
            try
            {
                isOverride = method.GetBaseDefinition() != method;
            }
            catch (NotSupportedException)
            {
                // MetadataLoadContext doesn't support GetBaseDefinition()
                // We'll conservatively mark as non-override
                isOverride = false;
            }
        }

        return new MethodSnapshot(
            method.Name,
            method.IsStatic,
            method.IsVirtual,
            isOverride,
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

        // Determine if this is an override (GetBaseDefinition not supported in MetadataLoadContext)
        var isOverride = false;
        if (isVirtual)
        {
            try
            {
                isOverride = (getter?.GetBaseDefinition() != getter) || (setter?.GetBaseDefinition() != setter);
            }
            catch (NotSupportedException)
            {
                // MetadataLoadContext doesn't support GetBaseDefinition()
                // We'll conservatively mark as non-override
                isOverride = false;
            }
        }

        var visibility = DetermineVisibility(getter ?? setter!);

        var typeRef = CreateTypeReference(property.PropertyType, assembly!);

        return new PropertySnapshot(
            property.Name,
            typeRef,
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
            addMethod?.IsStatic ?? false,
            DetermineVisibility(addMethod!),
            new MemberBinding(
                assembly?.GetName().Name ?? "",
                evt.DeclaringType?.FullName ?? "",
                evt.Name));
    }

    /// <summary>
    /// Reflects enum members.
    /// Uses GetFields() for MetadataLoadContext compatibility instead of Enum.GetValues()/Parse().
    /// </summary>
    private static IReadOnlyList<EnumMember> ReflectEnumMembers(Type enumType)
    {
        // Use GetFields() for MetadataLoadContext compatibility
        return enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(f =>
            {
                var value = f.GetRawConstantValue();
                var longValue = value != null ? Convert.ToInt64(value) : 0L;
                return new EnumMember(f.Name, longValue);
            })
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

        // Check for ParamArrayAttribute (GetCustomAttribute not supported in MetadataLoadContext)
        var isParams = false;
        try
        {
            isParams = param.GetCustomAttribute<ParamArrayAttribute>() != null;
        }
        catch (Exception)
        {
            // MetadataLoadContext doesn't support GetCustomAttribute for parameters
            // Throws InvalidOperationException: "The requested operation cannot be used on objects loaded by a MetadataLoadContext."
            isParams = false;
        }

        return new ParameterSnapshot(
            param.Name ?? $"arg{param.Position}",
            typeRef,
            kind,
            param.IsOptional,
            param.HasDefaultValue ? (param.RawDefaultValue?.ToString() ?? "") : "",
            isParams);
    }

    /// <summary>
    /// Creates a TypeReference from a .NET Type using reflection.
    /// Recursively parses generic arguments.
    /// </summary>
    private static TypeReference CreateTypeReference(Type type, Assembly currentAssembly)
    {
        // Handle ref/out parameters - strip the & suffix
        var actualType = type.IsByRef ? type.GetElementType()! : type;

        // Handle pointer types
        if (actualType.IsPointer)
        {
            var elementType = actualType.GetElementType()!;
            var elementRef = CreateTypeReference(elementType, currentAssembly);
            return TypeReference.CreatePointer(elementRef);
        }

        // Handle array types
        if (actualType.IsArray)
        {
            var elementType = actualType.GetElementType()!;
            var elementRef = CreateTypeReference(elementType, currentAssembly);
            var rank = actualType.GetArrayRank();
            return TypeReference.CreateArray(elementRef, rank);
        }

        var assemblyName = actualType.Assembly.GetName().Name;
        var isCrossAssembly = actualType.Assembly != currentAssembly;
        var assembly = isCrossAssembly ? assemblyName?.Replace(".", "_") : null;

        // Get namespace and type name
        var ns = actualType.Namespace;
        var typeName = actualType.Name.Replace('+', '.');

        // Check for function pointer types (C# 9+ delegate*)
        // Keep full CLR fidelity - mark with special type name for Phase 3 transformation
        if (actualType.IsFunctionPointer)
        {
            // Store as special marker - Phase 3 will transform to 'any'
            return TypeReference.CreateSimple(null, "__FunctionPointer", null);
        }

        // Check for other exotic types with empty names
        if (string.IsNullOrEmpty(typeName))
        {
            // Unknown type - use marker for Phase 3
            return TypeReference.CreateSimple(null, "__UnknownType", null);
        }

        // Handle generic types - recursively create TypeReferences for generic arguments
        if (actualType.IsGenericType)
        {
            // Strip generic arity from type name (e.g., "List`1" -> "List_1")
            typeName = typeName.Replace('`', '_');

            var genericArgs = actualType.GetGenericArguments()
                .Select(arg => CreateTypeReference(arg, currentAssembly))
                .ToList();

            return TypeReference.CreateGeneric(ns, typeName, genericArgs, assembly);
        }

        // Simple type (no generics)
        return TypeReference.CreateSimple(ns, typeName, assembly);
    }

    /// <summary>
    /// Formats a type name with generic arity and arguments.
    /// Converts: List`1[[System.Int32, ...]] to List_1<System.Int32>
    /// Converts nested types: Outer+Inner to Outer.Inner
    /// Always includes full namespace path (e.g., System.Collections.Generic.List_1)
    /// </summary>
    private static string FormatTypeName(Type type, Assembly currentAssembly)
    {
        // Non-generic types - always use full namespace path
        if (!type.IsGenericType)
        {
            return (type.FullName ?? type.Name).Replace('+', '.');
        }

        // Generic type definition (e.g., List`1): add underscore arity, replace + with .
        if (type.IsGenericTypeDefinition)
        {
            return (type.FullName ?? type.Name).Replace('`', '_').Replace('+', '.');
        }

        // Constructed generic type (e.g., List<int>): format with TypeScript syntax
        var genericDef = type.GetGenericTypeDefinition();
        var baseName = (genericDef.FullName ?? genericDef.Name).Replace('`', '_').Replace('+', '.');

        var typeArgs = type.GetGenericArguments();
        var formattedArgs = typeArgs.Select(arg => {
            var argRef = CreateTypeReference(arg, currentAssembly);
            return argRef.ClrType;
        });

        return $"{baseName}<{string.Join(", ", formattedArgs)}>";
    }

    /// <summary>
    /// Gets the simple type name (without namespace) for a type.
    /// Handles nested types by using underscore separators.
    /// Example: Dictionary+KeyCollection -> Dictionary_KeyCollection
    /// </summary>
    private static string GetSimpleTypeName(Type type)
    {
        // Handle nested types - build full name with underscores
        if (type.IsNested && type.DeclaringType != null)
        {
            var parts = new List<string>();
            var current = type;

            while (current != null)
            {
                parts.Insert(0, current.Name);
                current = current.DeclaringType;
            }

            return string.Join("_", parts).Replace('+', '_');
        }

        return type.Name.Replace('+', '_');
    }

    /// <summary>
    /// Extracts cross-assembly dependencies from type snapshots.
    /// Recursively extracts from all TypeReferences including generic arguments.
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
                    ExtractTypeReferenceDependencies(param.Type, deps);
                }
            }

            foreach (var prop in type.Members.Properties)
            {
                ExtractTypeReferenceDependencies(prop.Type, deps);
            }
        }

        return deps.Select(d => new DependencyRef(d.Namespace, d.Assembly)).ToList();
    }

    /// <summary>
    /// Recursively extracts namespace dependencies from a TypeReference.
    /// Handles generic arguments recursively.
    /// </summary>
    private static void ExtractTypeReferenceDependencies(
        TypeReference? typeRef,
        HashSet<(string, string)> deps)
    {
        if (typeRef == null) return;

        // Extract namespace dependency for this type
        if (typeRef.Assembly != null && typeRef.Namespace != null)
        {
            deps.Add((typeRef.Assembly, typeRef.Namespace));
        }

        // Recursively extract dependencies from generic arguments
        foreach (var genericArg in typeRef.GenericArgs)
        {
            ExtractTypeReferenceDependencies(genericArg, deps);
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
