using tsbindgen.Render;
using tsbindgen.Render.Transform;
using tsbindgen.Snapshot;

namespace tsbindgen.Config;

/// <summary>
/// Context for Phase 3 analysis and Phase 4 emission.
/// Provides on-demand name computation with caching.
/// Eliminates need to store computed strings in models - everything derived from structure.
/// </summary>
public sealed class AnalysisContext
{
    private readonly GeneratorConfig _config;

    // Caches for computed names (identity-based, not value-based)
    private readonly Dictionary<object, string> _identifierCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<TypeReference, string> _analysisNameCache = new();
    private readonly Dictionary<TypeReference, string> _emitNameCache = new();

    /// <summary>
    /// Global interface index for cross-assembly interface lookups.
    /// Used by StructuralConformance to check type-forwarded interfaces.
    /// </summary>
    public GlobalInterfaceIndex? GlobalInterfaceIndex { get; init; }

    public AnalysisContext(GeneratorConfig config, GlobalInterfaceIndex? globalInterfaceIndex = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        GlobalInterfaceIndex = globalInterfaceIndex;
    }

    /// <summary>
    /// Gets the TypeScript identifier for a type.
    /// Applies CLI transformations (interface prefix, class suffix, etc.).
    /// Used for: Analysis passes, lookups, internal references.
    /// Example: "IList_1" (with interface prefix if configured)
    /// </summary>
    public string GetTypeIdentifier(TypeModel type)
    {
        if (_identifierCache.TryGetValue(type, out var cached))
            return cached;

        // Compute base name from structure
        var baseName = TsNaming.ForAnalysis(type.Binding.Type);

        // Apply CLI transformations
        var identifier = type.Kind switch
        {
            TypeKind.Interface => NameTransformation.Apply(baseName, _config.InterfaceNames),
            TypeKind.Class => NameTransformation.Apply(baseName, _config.ClassNames),
            _ => baseName
        };

        _identifierCache[type] = identifier;
        return identifier;
    }

    /// <summary>
    /// Gets the TypeScript identifier for a method.
    /// Applies CLI transformations (camelCase, etc.).
    /// </summary>
    public string GetMethodIdentifier(MethodModel method)
    {
        if (_identifierCache.TryGetValue(method, out var cached))
            return cached;

        var identifier = NameTransformation.Apply(method.ClrName, _config.MethodNames);
        _identifierCache[method] = identifier;
        return identifier;
    }

    /// <summary>
    /// Gets the TypeScript identifier for a property.
    /// Applies CLI transformations.
    /// </summary>
    public string GetPropertyIdentifier(PropertyModel property)
    {
        if (_identifierCache.TryGetValue(property, out var cached))
            return cached;

        var identifier = NameTransformation.Apply(property.ClrName, _config.PropertyNames);
        _identifierCache[property] = identifier;
        return identifier;
    }

    /// <summary>
    /// Gets the TypeScript identifier for a field.
    /// Applies CLI transformations.
    /// </summary>
    public string GetFieldIdentifier(FieldModel field)
    {
        if (_identifierCache.TryGetValue(field, out var cached))
            return cached;

        var identifier = NameTransformation.Apply(field.ClrName, _config.PropertyNames);
        _identifierCache[field] = identifier;
        return identifier;
    }

    /// <summary>
    /// Gets the TypeScript identifier for an event.
    /// Applies CLI transformations.
    /// </summary>
    public string GetEventIdentifier(EventModel evt)
    {
        if (_identifierCache.TryGetValue(evt, out var cached))
            return cached;

        var identifier = NameTransformation.Apply(evt.ClrName, _config.PropertyNames);
        _identifierCache[evt] = identifier;
        return identifier;
    }

    /// <summary>
    /// Gets the TypeScript identifier for a generic parameter.
    /// No transformations applied (T, U, TKey, etc. used as-is).
    /// </summary>
    public string GetGenericParameterIdentifier(GenericParameterModel param)
    {
        // Generic parameters are not transformed
        return param.Name;
    }

    /// <summary>
    /// Gets the TypeScript analysis name for a TypeReference.
    /// Uses underscore for nesting: "Console_Error_1"
    /// Used for: Analysis lookups, type matching.
    /// </summary>
    public string GetAnalysisName(TypeReference typeRef)
    {
        if (_analysisNameCache.TryGetValue(typeRef, out var cached))
            return cached;

        var name = TsNaming.ForAnalysis(typeRef);
        _analysisNameCache[typeRef] = name;
        return name;
    }

    /// <summary>
    /// Gets the TypeScript emit name for a TypeReference.
    /// Uses dollar for nesting: "Console$Error_1"
    /// Used for: .d.ts emission, file output.
    /// </summary>
    public string GetEmitName(TypeReference typeRef)
    {
        if (_emitNameCache.TryGetValue(typeRef, out var cached))
            return cached;

        var name = TsNaming.ForEmit(typeRef);
        _emitNameCache[typeRef] = name;
        return name;
    }

    /// <summary>
    /// Checks if two methods have the same TypeScript identifier.
    /// Used by analysis passes to detect conflicts.
    /// </summary>
    public bool SameIdentifier(MethodModel m1, MethodModel m2)
    {
        return GetMethodIdentifier(m1) == GetMethodIdentifier(m2);
    }

    /// <summary>
    /// Checks if two properties have the same TypeScript identifier.
    /// </summary>
    public bool SameIdentifier(PropertyModel p1, PropertyModel p2)
    {
        return GetPropertyIdentifier(p1) == GetPropertyIdentifier(p2);
    }

    /// <summary>
    /// Checks if two fields have the same TypeScript identifier.
    /// </summary>
    public bool SameIdentifier(FieldModel f1, FieldModel f2)
    {
        return GetFieldIdentifier(f1) == GetFieldIdentifier(f2);
    }

    /// <summary>
    /// Checks if two events have the same TypeScript identifier.
    /// </summary>
    public bool SameIdentifier(EventModel e1, EventModel e2)
    {
        return GetEventIdentifier(e1) == GetEventIdentifier(e2);
    }
}
