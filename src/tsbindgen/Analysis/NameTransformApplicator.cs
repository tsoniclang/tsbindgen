using GenerateDts.Config;
using GenerateDts.Model;
using TypeInfo = GenerateDts.Model.TypeInfo;

namespace GenerateDts.Analysis;

/// <summary>
/// Applies naming transforms to a processed assembly and tracks bindings.
/// </summary>
public sealed class NameTransformApplicator
{
    private readonly GeneratorConfig _config;
    private readonly Dictionary<string, BindingEntry> _bindings = new();

    public NameTransformApplicator(GeneratorConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Applies naming transforms to the processed assembly.
    /// </summary>
    public ProcessedAssembly Apply(ProcessedAssembly assembly)
    {
        var transformedNamespaces = new List<NamespaceInfo>();

        foreach (var ns in assembly.Namespaces)
        {
            var transformedNs = TransformNamespace(ns);
            transformedNamespaces.Add(transformedNs);
        }

        return new ProcessedAssembly(transformedNamespaces, assembly.Warnings);
    }

    /// <summary>
    /// Gets the binding manifest (transformed name → original CLR name).
    /// </summary>
    public Dictionary<string, BindingEntry> GetBindings()
    {
        return _bindings;
    }

    private NamespaceInfo TransformNamespace(NamespaceInfo ns)
    {
        var namespaceName = NameTransform.Apply(ns.Name, _config.NamespaceNames);

        if (namespaceName != ns.Name)
        {
            TrackBinding(namespaceName, ns.Name, "namespace", ns.Name);
        }

        var transformedTypes = ns.Types.Select(t => TransformType(t, ns.Name)).ToList();

        return new NamespaceInfo(namespaceName, transformedTypes);
    }

    private TypeDeclaration TransformType(TypeDeclaration type, string namespaceName)
    {
        return type switch
        {
            ClassDeclaration cls => TransformClass(cls, namespaceName),
            InterfaceDeclaration iface => TransformInterface(iface, namespaceName),
            EnumDeclaration enumDecl => TransformEnum(enumDecl, namespaceName),
            StaticNamespaceDeclaration staticNs => TransformStaticNamespace(staticNs, namespaceName),
            IntersectionTypeAlias alias => TransformIntersectionAlias(alias, namespaceName),
            _ => type
        };
    }

    private ClassDeclaration TransformClass(ClassDeclaration cls, string namespaceName)
    {
        var className = NameTransform.Apply(cls.Name, _config.ClassNames);

        if (className != cls.Name)
        {
            TrackBinding(className, cls.Name, "class", cls.FullName);
        }

        var transformedProperties = cls.Properties
            .Select(p => TransformProperty(p, cls.FullName))
            .ToList();

        var transformedMethods = cls.Methods
            .Select(m => TransformMethod(m, cls.FullName))
            .ToList();

        var transformedCompanion = cls.Companion != null
            ? TransformCompanion(cls.Companion, cls.FullName)
            : null;

        return new ClassDeclaration(
            className,
            cls.FullName,
            cls.IsGeneric,
            cls.GenericParameters,
            cls.BaseType,
            cls.Interfaces,
            cls.Constructors,
            transformedProperties,
            transformedMethods,
            cls.IsStatic,
            transformedCompanion);
    }

    private InterfaceDeclaration TransformInterface(InterfaceDeclaration iface, string namespaceName)
    {
        var interfaceName = NameTransform.Apply(iface.Name, _config.InterfaceNames);

        if (interfaceName != iface.Name)
        {
            TrackBinding(interfaceName, iface.Name, "interface", iface.FullName);
        }

        var transformedProperties = iface.Properties
            .Select(p => TransformProperty(p, iface.FullName))
            .ToList();

        var transformedMethods = iface.Methods
            .Select(m => TransformMethod(m, iface.FullName))
            .ToList();

        return new InterfaceDeclaration(
            interfaceName,
            iface.FullName,
            iface.IsGeneric,
            iface.GenericParameters,
            iface.Extends,
            transformedProperties,
            transformedMethods,
            iface.IsDiamondBase);
    }

    private EnumDeclaration TransformEnum(EnumDeclaration enumDecl, string namespaceName)
    {
        var transformedMembers = enumDecl.Members
            .Select(m => TransformEnumMember(m, enumDecl.FullName))
            .ToList();

        return new EnumDeclaration(
            enumDecl.Name,
            enumDecl.FullName,
            enumDecl.IsGeneric,
            enumDecl.GenericParameters,
            transformedMembers);
    }

    private EnumMember TransformEnumMember(EnumMember member, string enumFullName)
    {
        var memberName = NameTransform.Apply(member.Name, _config.EnumMemberNames);

        if (memberName != member.Name)
        {
            TrackBinding(memberName, member.Name, "enumMember", $"{enumFullName}.{member.Name}");
        }

        return new EnumMember(memberName, member.Value);
    }

    private StaticNamespaceDeclaration TransformStaticNamespace(StaticNamespaceDeclaration staticNs, string namespaceName)
    {
        var transformedProperties = staticNs.Properties
            .Select(p => TransformProperty(p, staticNs.FullName))
            .ToList();

        var transformedMethods = staticNs.Methods
            .Select(m => TransformMethod(m, staticNs.FullName))
            .ToList();

        return new StaticNamespaceDeclaration(
            staticNs.Name,
            staticNs.FullName,
            staticNs.IsGeneric,
            staticNs.GenericParameters,
            transformedProperties,
            transformedMethods);
    }

    private IntersectionTypeAlias TransformIntersectionAlias(IntersectionTypeAlias alias, string namespaceName)
    {
        // Intersection aliases keep their names unchanged (they're synthetic types)
        return alias;
    }

    private CompanionNamespace TransformCompanion(CompanionNamespace companion, string classFullName)
    {
        var transformedProperties = companion.Properties
            .Select(p => TransformProperty(p, classFullName))
            .ToList();

        var transformedMethods = companion.Methods
            .Select(m => TransformMethod(m, classFullName))
            .ToList();

        return new CompanionNamespace(transformedProperties, transformedMethods);
    }

    private TypeInfo.PropertyInfo TransformProperty(TypeInfo.PropertyInfo prop, string typeFullName)
    {
        var propertyName = NameTransform.Apply(prop.Name, _config.PropertyNames);

        if (propertyName != prop.Name)
        {
            TrackBinding(propertyName, prop.Name, "property", $"{typeFullName}.{prop.Name}");
        }

        return new TypeInfo.PropertyInfo(
            propertyName,
            prop.Type,
            prop.IsReadOnly,
            prop.IsStatic);
    }

    private TypeInfo.MethodInfo TransformMethod(TypeInfo.MethodInfo method, string typeFullName)
    {
        var methodName = NameTransform.Apply(method.Name, _config.MethodNames);

        if (methodName != method.Name)
        {
            TrackBinding(methodName, method.Name, "method", $"{typeFullName}.{method.Name}");
        }

        return new TypeInfo.MethodInfo(
            methodName,
            method.ReturnType,
            method.Parameters,
            method.IsStatic,
            method.IsGeneric,
            method.GenericParameters);
    }

    private void TrackBinding(string transformedName, string originalName, string kind, string fullName)
    {
        // Key by CLR name (originalName), store CLR name as Name and TypeScript name as Alias
        _bindings[originalName] = new BindingEntry(
            kind,
            originalName,     // CLR name
            transformedName,  // TS alias
            fullName);
    }
}

/// <summary>
/// Represents a binding entry mapping a CLR name to its TypeScript alias.
/// </summary>
public sealed record BindingEntry(
    string Kind,
    string Name,      // CLR identifier
    string Alias,     // TypeScript-facing identifier
    string FullName);
