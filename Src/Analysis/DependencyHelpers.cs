using GenerateDts.Pipeline;

namespace GenerateDts.Analysis;

/// <summary>
/// Helpers for tracking type dependencies for cross-assembly imports.
/// </summary>
public static class DependencyHelpers
{
    /// <summary>
    /// Tracks a type dependency for cross-assembly import generation.
    /// Recursively tracks generic type arguments.
    /// </summary>
    public static void TrackTypeDependency(DependencyTracker? dependencyTracker, Type type)
    {
        if (dependencyTracker == null) return;

        // Track the type itself
        dependencyTracker.RecordTypeReference(type);

        // Track generic type arguments
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            foreach (var arg in type.GetGenericArguments())
            {
                TrackTypeDependency(dependencyTracker, arg);
            }
        }

        // Track array element type
        if (type.IsArray)
        {
            var elementType = type.GetElementType();
            if (elementType != null)
            {
                TrackTypeDependency(dependencyTracker, elementType);
            }
        }

        // Track by-ref element type
        if (type.IsByRef || type.IsPointer)
        {
            var elementType = type.GetElementType();
            if (elementType != null)
            {
                TrackTypeDependency(dependencyTracker, elementType);
            }
        }
    }
}
