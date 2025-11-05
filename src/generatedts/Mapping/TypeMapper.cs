using System.Reflection;
using GenerateDts.Pipeline;

namespace GenerateDts.Mapping;

public sealed class TypeMapper
{
    private readonly List<string> _warnings = new();
    private Assembly? _currentAssembly;
    private DependencyTracker? _dependencyTracker;

    public IReadOnlyList<string> Warnings => _warnings;

    public void SetContext(Assembly currentAssembly, DependencyTracker? dependencyTracker)
    {
        _currentAssembly = currentAssembly;
        _dependencyTracker = dependencyTracker;
    }

    public string MapType(Type type)
    {
        // Handle ref/out parameters (ByRef types)
        if (type.IsByRef)
        {
            return MapType(type.GetElementType()!);
        }

        // Handle pointer types
        if (type.IsPointer)
        {
            var location = type.FullName ?? type.Name;
            AddWarning($"[{location}] Pointer type mapped to 'any' - TypeScript doesn't support pointers");
            return "any";
        }

        // Handle nullable value types and arrays
        var arrayOrNullable = ArrayMapping.TryMapArrayOrNullable(type, MapType);
        if (arrayOrNullable != null)
        {
            return arrayOrNullable;
        }

        // Handle delegates
        if (DelegateMapping.IsDelegate(type))
        {
            var delegateSignature = DelegateMapping.MapDelegateToFunctionType(type, MapType, AddWarning);
            if (delegateSignature != null)
            {
                return delegateSignature;
            }
        }

        // Handle generic types
        if (type.IsGenericType)
        {
            return GenericMapping.MapGenericType(type, MapType, GetFullTypeName);
        }

        // Handle primitive types
        var fullName = type.FullName ?? type.Name;
        if (type.IsPrimitive || fullName == "System.String" || fullName == "System.Void")
        {
            return PrimitiveMapping.MapPrimitiveType(type);
        }

        // Handle special types
        if (type.Namespace?.StartsWith("System") == true)
        {
            var mapped = PrimitiveMapping.MapSystemType(type);
            if (mapped != null)
            {
                return mapped;
            }
        }

        // Default: use fully qualified name, fallback to "any" if empty
        var fullTypeName = GetFullTypeName(type);
        if (string.IsNullOrWhiteSpace(fullTypeName))
        {
            var location = type.FullName ?? type.Name ?? type.ToString();
            AddWarning($"[{location}] Type has no name - mapped to 'any'");
            return "any";
        }
        return fullTypeName;
    }

    public string GetFullTypeName(Type type)
    {
        return TypeNameMapping.GetFullTypeName(type, _currentAssembly, _dependencyTracker);
    }

    public void AddWarning(string message)
    {
        _warnings.Add(message);
    }
}
