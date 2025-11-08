using System.Reflection;
using tsbindgen.SinglePhase.Model.Types;
using tsbindgen.SinglePhase.Model.Symbols;

namespace tsbindgen.SinglePhase.Load;

/// <summary>
/// Converts System.Type to our TypeReference model.
/// Handles all type constructs: named, generic, array, pointer, byref, nested.
/// </summary>
public sealed class TypeReferenceFactory
{
    private readonly BuildContext _ctx;
    private readonly Dictionary<Type, TypeReference> _cache = new();

    public TypeReferenceFactory(BuildContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Convert a System.Type to TypeReference.
    /// </summary>
    public TypeReference Create(Type type)
    {
        // Check cache first
        if (_cache.TryGetValue(type, out var cached))
            return cached;

        var result = CreateInternal(type);
        _cache[type] = result;
        return result;
    }

    private TypeReference CreateInternal(Type type)
    {
        // Handle special cases first
        if (type.IsByRef)
        {
            return new ByRefTypeReference
            {
                ReferencedType = Create(type.GetElementType()!)
            };
        }

        if (type.IsPointer)
        {
            var depth = 1;
            var elementType = type.GetElementType()!;
            while (elementType.IsPointer)
            {
                depth++;
                elementType = elementType.GetElementType()!;
            }

            return new PointerTypeReference
            {
                PointeeType = Create(elementType),
                Depth = depth
            };
        }

        if (type.IsArray)
        {
            return new ArrayTypeReference
            {
                ElementType = Create(type.GetElementType()!),
                Rank = type.GetArrayRank()
            };
        }

        if (type.IsGenericParameter)
        {
            return CreateGenericParameter(type);
        }

        // Named type (class, struct, interface, enum, delegate)
        return CreateNamed(type);
    }

    private TypeReference CreateNamed(Type type)
    {
        var assemblyName = type.Assembly.GetName().Name ?? "Unknown";
        var fullName = type.FullName ?? type.Name;
        var namespaceName = type.Namespace ?? "";
        var name = type.Name;

        // Handle generic types
        var arity = 0;
        var typeArgs = new List<TypeReference>();

        if (type.IsGenericType)
        {
            arity = type.GetGenericArguments().Length;

            // For constructed generic types, get type arguments
            if (type.IsConstructedGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                {
                    typeArgs.Add(Create(arg));
                }
            }
        }

        return new NamedTypeReference
        {
            AssemblyName = _ctx.Intern(assemblyName),
            FullName = _ctx.Intern(fullName),
            Namespace = _ctx.Intern(namespaceName),
            Name = _ctx.Intern(name),
            Arity = arity,
            TypeArguments = typeArgs,
            IsValueType = type.IsValueType
        };
    }

    private TypeReference CreateGenericParameter(Type type)
    {
        var declaringType = type.DeclaringType ?? type.DeclaringMethod?.DeclaringType;
        var declaringName = declaringType?.FullName ?? "Unknown";

        var id = new GenericParameterId
        {
            DeclaringTypeName = _ctx.Intern(declaringName),
            Position = type.GenericParameterPosition,
            IsMethodParameter = type.DeclaringMethod != null
        };

        var constraints = new List<TypeReference>();
        foreach (var constraint in type.GetGenericParameterConstraints())
        {
            constraints.Add(Create(constraint));
        }

        return new GenericParameterReference
        {
            Id = id,
            Name = _ctx.Intern(type.Name),
            Position = type.GenericParameterPosition,
            Constraints = constraints
        };
    }

    /// <summary>
    /// Create a GenericParameterSymbol from a Type.
    /// </summary>
    public GenericParameterSymbol CreateGenericParameterSymbol(Type type)
    {
        if (!type.IsGenericParameter)
            throw new ArgumentException("Type must be a generic parameter", nameof(type));

        var declaringType = type.DeclaringType ?? type.DeclaringMethod?.DeclaringType;
        var declaringName = declaringType?.FullName ?? "Unknown";

        var id = new GenericParameterId
        {
            DeclaringTypeName = _ctx.Intern(declaringName),
            Position = type.GenericParameterPosition,
            IsMethodParameter = type.DeclaringMethod != null
        };

        var constraints = new List<TypeReference>();
        foreach (var constraint in type.GetGenericParameterConstraints())
        {
            constraints.Add(Create(constraint));
        }

        var variance = Variance.None;
        var attrs = type.GenericParameterAttributes;
        if ((attrs & GenericParameterAttributes.Covariant) != 0)
            variance = Variance.Covariant;
        else if ((attrs & GenericParameterAttributes.Contravariant) != 0)
            variance = Variance.Contravariant;

        var specialConstraints = GenericParameterConstraints.None;
        if ((attrs & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            specialConstraints |= GenericParameterConstraints.ReferenceType;
        if ((attrs & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
            specialConstraints |= GenericParameterConstraints.ValueType;
        if ((attrs & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
            specialConstraints |= GenericParameterConstraints.DefaultConstructor;

        return new GenericParameterSymbol
        {
            Id = id,
            Name = _ctx.Intern(type.Name),
            Position = type.GenericParameterPosition,
            Constraints = constraints,
            Variance = variance,
            SpecialConstraints = specialConstraints
        };
    }

    /// <summary>
    /// Clear the cache (for testing).
    /// </summary>
    public void ClearCache() => _cache.Clear();
}
