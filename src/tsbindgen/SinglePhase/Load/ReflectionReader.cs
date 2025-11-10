using System.Collections.Immutable;
using System.Reflection;
using tsbindgen.SinglePhase.Renaming;
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

        // Sort assemblies by name for deterministic iteration
        foreach (var assembly in assemblies.OrderBy(a => a.GetName().FullName))
        {
            sourceAssemblies.Add(assembly.Location);
            _ctx.Log("ReflectionReader", $"Reading types from {assembly.GetName().Name}...");

            foreach (var type in assembly.GetTypes())
            {
                // Skip compiler-generated types first
                // Common patterns: <Name>e__FixedBuffer, <>c__DisplayClass, <>d__Iterator, <>f__AnonymousType
                if (IsCompilerGenerated(type.Name))
                {
                    _ctx.Log("ReflectionReader", $"Skipping compiler-generated type: {type.FullName}");
                    continue;
                }

                // Only process public types (correctly handling nested types)
                var accessibility = ComputeAccessibility(type);
                if (accessibility != Accessibility.Public)
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
                .Distinct()
                .OrderBy(name => name)
                .ToImmutableHashSet();

            namespaces.Add(new NamespaceSymbol
            {
                Name = ns,
                Types = types.ToImmutableArray(),
                StableId = nsStableId,
                ContributingAssemblies = contributingAssemblies
            });
        }

        return new SymbolGraph
        {
            Namespaces = namespaces.ToImmutableArray(),
            SourceAssemblies = sourceAssemblies.ToImmutableHashSet()
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
        var accessibility = ComputeAccessibility(type);
        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(_typeFactory.CreateGenericParameterSymbol).ToImmutableArray()
            : ImmutableArray<GenericParameterSymbol>.Empty;

        var baseType = type.BaseType != null ? _typeFactory.Create(type.BaseType) : null;
        var interfaces = type.GetInterfaces().Select(_typeFactory.Create).ToImmutableArray();

        // Read members
        var members = ReadMembers(type);

        // Read nested types (filter out compiler-generated)
        var nestedTypes = type.GetNestedTypes(BindingFlags.Public)
            .Where(t => !IsCompilerGenerated(t.Name))
            .Select(ReadType)
            .ToImmutableArray();

        return new TypeSymbol
        {
            StableId = stableId,
            ClrFullName = _ctx.Intern(type.FullName ?? type.Name),
            ClrName = _ctx.Intern(type.Name),
            Accessibility = accessibility,
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

    /// <summary>
    /// Compute accessibility for a type, correctly handling nested types.
    /// For nested types, accessibility is the intersection of the declaring type's
    /// accessibility and the nested type's visibility.
    /// </summary>
    private static Accessibility ComputeAccessibility(Type type)
    {
        // Top-level types: simply check IsPublic
        if (!type.IsNested)
        {
            return type.IsPublic ? Accessibility.Public : Accessibility.Internal;
        }

        // Nested types: combine declaring type's accessibility with nested visibility
        // A nested public type is only truly public if its declaring type is also public
        if (type.IsNestedPublic)
        {
            var declaringAccessibility = ComputeAccessibility(type.DeclaringType!);
            return declaringAccessibility == Accessibility.Public
                ? Accessibility.Public
                : Accessibility.Internal;
        }

        // Any other nested visibility (family, assembly, famandassem, famorassem, private)
        // is not public - mark as Internal
        return Accessibility.Internal;
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
        var methods = new List<MethodSymbol>();
        var properties = new List<PropertySymbol>();
        var fields = new List<FieldSymbol>();
        var events = new List<EventSymbol>();
        var constructors = new List<ConstructorSymbol>();

        const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        const BindingFlags publicStatic = BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly;

        // Read methods
        var seenMethods = new HashSet<string>();
        foreach (var method in type.GetMethods(publicInstance | publicStatic))
        {
            // Skip property/event accessors and special methods
            if (method.IsSpecialName) continue;

            // DEBUG: Track method tokens to detect duplicates from reflection
            var methodKey = $"{method.Name}|{method.MetadataToken}";
            if (seenMethods.Contains(methodKey))
            {
                _ctx.Log("ReflectionReader", $"WARNING: GetMethods returned duplicate method: {type.FullName}::{method.Name} (token: {method.MetadataToken})");
                continue; // Skip duplicate
            }
            seenMethods.Add(methodKey);

            var methodSymbol = ReadMethod(method, type);

            // DEBUG: Log if we're about to add a duplicate StableId
            if (methods.Any(m => m.StableId.Equals(methodSymbol.StableId)))
            {
                _ctx.Log("ReflectionReader", $"ERROR: About to add duplicate StableId: {methodSymbol.StableId}");
                _ctx.Log("ReflectionReader", $"  Method name: {method.Name}, MetadataToken: {method.MetadataToken}");
                _ctx.Log("ReflectionReader", $"  Type: {type.FullName}");
                continue; // Skip to prevent duplicate
            }

            methods.Add(methodSymbol);
        }

        // Read properties
        foreach (var property in type.GetProperties(publicInstance | publicStatic))
        {
            properties.Add(ReadProperty(property, type));
        }

        // Read fields
        foreach (var field in type.GetFields(publicInstance | publicStatic))
        {
            fields.Add(ReadField(field, type));
        }

        // Read events
        foreach (var evt in type.GetEvents(publicInstance | publicStatic))
        {
            events.Add(ReadEvent(evt, type));
        }

        // Read constructors
        foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            constructors.Add(ReadConstructor(ctor, type));
        }

        return new TypeMembers
        {
            Methods = methods.ToImmutableArray(),
            Properties = properties.ToImmutableArray(),
            Fields = fields.ToImmutableArray(),
            Events = events.ToImmutableArray(),
            Constructors = constructors.ToImmutableArray()
        };
    }

    private MethodSymbol ReadMethod(MethodInfo method, Type declaringType)
    {
        // Detect explicit interface implementation by checking for dot in name
        // Example: "System.Collections.ICollection.SyncRoot" vs "SyncRoot"
        var clrName = method.Name;
        var memberName = method.Name;

        // For explicit interface implementations, use qualified name for identity
        // This ensures different interface implementations get distinct StableIds
        if (clrName.Contains('.'))
        {
            // Keep qualified name for both ClrName and MemberName
            // Example: "System.Collections.ICollection.SyncRoot"
            memberName = clrName;
        }

        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = _ctx.Intern(memberName),
            CanonicalSignature = CreateMethodSignature(method),
            MetadataToken = method.MetadataToken
        };

        var parameters = method.GetParameters().Select(ReadParameter).ToImmutableArray();
        var genericParams = method.IsGenericMethod
            ? method.GetGenericArguments().Select(_typeFactory.CreateGenericParameterSymbol).ToImmutableArray()
            : ImmutableArray<GenericParameterSymbol>.Empty;

        return new MethodSymbol
        {
            StableId = stableId,
            ClrName = _ctx.Intern(clrName),
            ReturnType = _typeFactory.Create(method.ReturnType),
            Parameters = parameters,
            GenericParameters = genericParams,
            IsStatic = method.IsStatic,
            IsAbstract = method.IsAbstract,
            IsVirtual = method.IsVirtual,
            IsOverride = IsMethodOverride(method),
            IsSealed = method.IsFinal,
            Visibility = GetVisibility(method),
            Provenance = MemberProvenance.Original,
            EmitScope = EmitScope.ClassSurface  // All reflected members start on class surface
        };
    }

    private PropertySymbol ReadProperty(PropertyInfo property, Type declaringType)
    {
        // Detect explicit interface implementation by checking for dot in name
        // Example: "System.Collections.ICollection.SyncRoot" vs "SyncRoot"
        var clrName = property.Name;
        var memberName = property.Name;

        // For explicit interface implementations, use qualified name for identity
        // This ensures different interface implementations get distinct StableIds
        if (clrName.Contains('.'))
        {
            // Keep qualified name for both ClrName and MemberName
            // Example: "System.Collections.ICollection.SyncRoot"
            memberName = clrName;
        }

        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = _ctx.Intern(memberName),
            CanonicalSignature = CreatePropertySignature(property),
            MetadataToken = property.MetadataToken
        };

        var indexParams = property.GetIndexParameters().Select(ReadParameter).ToImmutableArray();
        var getter = property.GetGetMethod();
        var setter = property.GetSetMethod();

        return new PropertySymbol
        {
            StableId = stableId,
            ClrName = _ctx.Intern(clrName),
            PropertyType = _typeFactory.Create(property.PropertyType),
            IndexParameters = indexParams,
            HasGetter = getter != null,
            HasSetter = setter != null,
            IsStatic = (getter ?? setter)?.IsStatic ?? false,
            IsVirtual = (getter ?? setter)?.IsVirtual ?? false,
            IsOverride = getter != null && IsMethodOverride(getter),
            IsAbstract = (getter ?? setter)?.IsAbstract ?? false,
            Visibility = GetPropertyVisibility(property),
            Provenance = MemberProvenance.Original,
            EmitScope = EmitScope.ClassSurface  // All reflected members start on class surface
        };
    }

    private FieldSymbol ReadField(FieldInfo field, Type declaringType)
    {
        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = _ctx.Intern(field.Name),
            CanonicalSignature = field.FieldType.FullName ?? field.FieldType.Name,
            MetadataToken = field.MetadataToken
        };

        return new FieldSymbol
        {
            StableId = stableId,
            ClrName = _ctx.Intern(field.Name),
            FieldType = _typeFactory.Create(field.FieldType),
            IsStatic = field.IsStatic,
            IsReadOnly = field.IsInitOnly,
            IsConst = field.IsLiteral,
            ConstValue = field.IsLiteral ? field.GetRawConstantValue() : null,
            Visibility = GetFieldVisibility(field),
            Provenance = MemberProvenance.Original,
            EmitScope = EmitScope.ClassSurface  // All reflected members start on class surface
        };
    }

    private EventSymbol ReadEvent(EventInfo evt, Type declaringType)
    {
        // Detect explicit interface implementation by checking for dot in name
        // Example: "System.ComponentModel.INotifyPropertyChanged.PropertyChanged" vs "PropertyChanged"
        var clrName = evt.Name!;
        var memberName = evt.Name!;

        // For explicit interface implementations, use qualified name for identity
        // This ensures different interface implementations get distinct StableIds
        if (clrName.Contains('.'))
        {
            // Keep qualified name for both ClrName and MemberName
            // Example: "System.ComponentModel.INotifyPropertyChanged.PropertyChanged"
            memberName = clrName;
        }

        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = _ctx.Intern(memberName),
            CanonicalSignature = evt.EventHandlerType?.FullName ?? "Unknown",
            MetadataToken = evt.MetadataToken
        };

        var addMethod = evt.GetAddMethod();

        return new EventSymbol
        {
            StableId = stableId,
            ClrName = _ctx.Intern(clrName),
            EventHandlerType = _typeFactory.Create(evt.EventHandlerType!),
            IsStatic = addMethod?.IsStatic ?? false,
            IsVirtual = addMethod?.IsVirtual ?? false,
            IsOverride = addMethod != null && IsMethodOverride(addMethod),
            Visibility = GetEventVisibility(evt),
            Provenance = MemberProvenance.Original,
            EmitScope = EmitScope.ClassSurface  // All reflected members start on class surface
        };
    }

    private ConstructorSymbol ReadConstructor(ConstructorInfo ctor, Type declaringType)
    {
        var stableId = new MemberStableId
        {
            AssemblyName = _ctx.Intern(declaringType.Assembly.GetName().Name ?? "Unknown"),
            DeclaringClrFullName = _ctx.Intern(declaringType.FullName ?? declaringType.Name),
            MemberName = ".ctor",
            CanonicalSignature = CreateConstructorSignature(ctor),
            MetadataToken = ctor.MetadataToken
        };

        return new ConstructorSymbol
        {
            StableId = stableId,
            Parameters = ctor.GetParameters().Select(ReadParameter).ToImmutableArray(),
            IsStatic = ctor.IsStatic,
            Visibility = GetConstructorVisibility(ctor)
        };
    }

    private ParameterSymbol ReadParameter(ParameterInfo param)
    {
        // Sanitize parameter name for TypeScript reserved words
        var paramName = param.Name ?? $"arg{param.Position}";
        var sanitizedName = TypeScriptReservedWords.SanitizeParameterName(paramName);

        return new ParameterSymbol
        {
            Name = _ctx.Intern(sanitizedName),
            Type = _typeFactory.Create(param.ParameterType),
            IsRef = param.ParameterType.IsByRef && !param.IsOut,
            IsOut = param.IsOut,
            IsParams = param.GetCustomAttributesData()
                .Any(attr => attr.AttributeType.Name == "ParamArrayAttribute"),
            HasDefaultValue = param.HasDefaultValue,
            DefaultValue = param.HasDefaultValue ? param.RawDefaultValue : null
        };
    }

    private string CreateMethodSignature(MethodInfo method)
    {
        var paramTypes = method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList();
        var returnType = method.ReturnType.FullName ?? method.ReturnType.Name;
        return _ctx.CanonicalizeMethod(method.Name, paramTypes, returnType);
    }

    private string CreatePropertySignature(PropertyInfo property)
    {
        var indexTypes = property.GetIndexParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList();
        var propType = property.PropertyType.FullName ?? property.PropertyType.Name;
        return _ctx.CanonicalizeProperty(property.Name, indexTypes, propType);
    }

    private string CreateConstructorSignature(ConstructorInfo ctor)
    {
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name).ToList();
        return _ctx.CanonicalizeMethod(".ctor", paramTypes, "void");
    }

    private Visibility GetVisibility(MethodInfo method)
    {
        if (method.IsPublic) return Visibility.Public;
        if (method.IsFamily) return Visibility.Protected;
        if (method.IsFamilyOrAssembly) return Visibility.ProtectedInternal;
        if (method.IsFamilyAndAssembly) return Visibility.PrivateProtected;
        if (method.IsAssembly) return Visibility.Internal;
        return Visibility.Private;
    }

    private Visibility GetPropertyVisibility(PropertyInfo property)
    {
        var getter = property.GetGetMethod(true);
        var setter = property.GetSetMethod(true);
        var method = getter ?? setter;
        return method != null ? GetVisibility(method) : Visibility.Private;
    }

    private Visibility GetFieldVisibility(FieldInfo field)
    {
        if (field.IsPublic) return Visibility.Public;
        if (field.IsFamily) return Visibility.Protected;
        if (field.IsFamilyOrAssembly) return Visibility.ProtectedInternal;
        if (field.IsFamilyAndAssembly) return Visibility.PrivateProtected;
        if (field.IsAssembly) return Visibility.Internal;
        return Visibility.Private;
    }

    private Visibility GetEventVisibility(EventInfo evt)
    {
        var addMethod = evt.GetAddMethod(true);
        return addMethod != null ? GetVisibility(addMethod) : Visibility.Private;
    }

    /// <summary>
    /// Check if a method is an override (vs new virtual or original virtual).
    /// Uses MethodAttributes flags which work with MetadataLoadContext.
    /// Overrides are virtual and do NOT have NewSlot set (they reuse vtable slot).
    /// </summary>
    private static bool IsMethodOverride(MethodInfo method)
    {
        return method.IsVirtual && !method.Attributes.HasFlag(MethodAttributes.NewSlot);
    }

    private Visibility GetConstructorVisibility(ConstructorInfo ctor)
    {
        if (ctor.IsPublic) return Visibility.Public;
        if (ctor.IsFamily) return Visibility.Protected;
        if (ctor.IsFamilyOrAssembly) return Visibility.ProtectedInternal;
        if (ctor.IsFamilyAndAssembly) return Visibility.PrivateProtected;
        if (ctor.IsAssembly) return Visibility.Internal;
        return Visibility.Private;
    }

    /// <summary>
    /// Check if a type name indicates compiler-generated code.
    /// Compiler-generated types have unspeakable names containing < or >
    /// Examples: "<Module>", "<PrivateImplementationDetails>", "<Name>e__FixedBuffer", "<>c__DisplayClass"
    /// </summary>
    private static bool IsCompilerGenerated(string typeName)
    {
        return typeName.Contains('<') || typeName.Contains('>');
    }
}
