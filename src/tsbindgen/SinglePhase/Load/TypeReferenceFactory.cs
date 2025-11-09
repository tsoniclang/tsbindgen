using System.Collections.Immutable;
using System.Reflection;
using tsbindgen.SinglePhase.Model.Types;
using tsbindgen.SinglePhase.Model.Symbols;

namespace tsbindgen.SinglePhase.Load;

/// <summary>
/// Converts System.Type to our TypeReference model.
/// Handles all type constructs: named, generic, array, pointer, byref, nested.
/// Uses memoization with cycle detection to prevent stack overflow on recursive constraints.
/// </summary>
public sealed class TypeReferenceFactory
{
    private readonly BuildContext _ctx;
    private readonly Dictionary<Type, TypeReference> _cache = new();
    private readonly HashSet<Type> _inProgress = new();

    public TypeReferenceFactory(BuildContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Convert a System.Type to TypeReference.
    /// Memoized with cycle detection to prevent infinite recursion.
    /// </summary>
    public TypeReference Create(Type type)
    {
        // Check cache first
        if (_cache.TryGetValue(type, out var cached))
            return cached;

        // Detect cycle - return placeholder to break recursion
        if (_inProgress.Contains(type))
        {
            return new PlaceholderTypeReference
            {
                DebugName = _ctx.Intern(type.FullName ?? type.Name)
            };
        }

        // Mark as in-progress
        _inProgress.Add(type);
        try
        {
            var result = CreateInternal(type);
            _cache[type] = result;
            return result;
        }
        finally
        {
            _inProgress.Remove(type);
        }
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

        // HARDENING: Stamp interface StableId at load time for interfaces
        // Format: AssemblyName:FullName (same as ScopeFactory.GetInterfaceStableId)
        // This eliminates repeated computation and graph lookups
        string? interfaceStableId = null;
        if (type.IsInterface)
        {
            interfaceStableId = _ctx.Intern($"{assemblyName}:{fullName}");
        }

        return new NamedTypeReference
        {
            AssemblyName = _ctx.Intern(assemblyName),
            FullName = _ctx.Intern(fullName),
            Namespace = _ctx.Intern(namespaceName),
            Name = _ctx.Intern(name),
            Arity = arity,
            TypeArguments = typeArgs,
            IsValueType = type.IsValueType,
            InterfaceStableId = interfaceStableId
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

        // NOTE: Constraints are NOT resolved here to avoid infinite recursion
        // on recursive constraints like IComparable<T> where T : IComparable<T>.
        // ConstraintCloser will resolve constraints during Shape phase.

        return new GenericParameterReference
        {
            Id = id,
            Name = _ctx.Intern(type.Name),
            Position = type.GenericParameterPosition,
            Constraints = new List<TypeReference>() // Empty - filled by ConstraintCloser
        };
    }

    /// <summary>
    /// Create a GenericParameterSymbol from a Type.
    /// Stores variance and special constraints; ConstraintCloser resolves type constraints later.
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

        // NOTE: Constraint types are NOT resolved here to avoid infinite recursion.
        // ConstraintCloser will resolve them during Shape phase.
        // We only store the raw System.Type[] for later resolution.

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
            Constraints = ImmutableArray<TypeReference>.Empty, // Empty - ConstraintCloser fills this
            RawConstraintTypes = type.GetGenericParameterConstraints(), // Raw for ConstraintCloser
            Variance = variance,
            SpecialConstraints = specialConstraints
        };
    }

    /// <summary>
    /// Clear the cache (for testing).
    /// </summary>
    public void ClearCache() => _cache.Clear();
}
