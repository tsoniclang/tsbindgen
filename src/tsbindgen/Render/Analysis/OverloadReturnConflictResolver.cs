using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Analysis;

/// <summary>
/// Resolves return-type conflicts in method overloads to produce a TypeScript-representable surface.
///
/// Problem: C# allows explicit interface implementations alongside public methods with the same
/// name and parameters but different return types. TypeScript does not support this - overloads
/// must have a single return type per (name, parameters) combination.
///
/// Solution: Bucket methods by (name, parameters, staticness). Within each bucket with conflicting
/// return types, keep exactly one method for the class surface (preferring public over explicit),
/// and mark the others as ViewOnly (emitted only in explicit interface views).
/// </summary>
public static class OverloadReturnConflictResolver
{
    /// <summary>
    /// Applies return-type conflict resolution to classes and structs.
    /// Interfaces are skipped (they don't have explicit implementations).
    /// </summary>
    public static NamespaceModel Apply(
        NamespaceModel model,
        IReadOnlyDictionary<string, NamespaceModel> allModels,
        AnalysisContext ctx)
    {
        var updatedTypes = model.Types.Select(type =>
        {
            // Only process classes and structs (interfaces don't have explicit implementations)
            if (type.Kind == TypeKind.Interface || type.Kind == TypeKind.Enum || type.Kind == TypeKind.Delegate)
                return type;

            return ResolveConflicts(type, ctx);
        }).ToList();

        return model with { Types = updatedTypes };
    }

    /// <summary>
    /// Resolves return-type conflicts within a single type's methods.
    /// </summary>
    private static TypeModel ResolveConflicts(TypeModel type, AnalysisContext ctx)
    {
        // Bucket methods by (name, parameters, staticness)
        var buckets = new Dictionary<string, List<MethodModel>>();

        foreach (var method in type.Members.Methods)
        {
            var bucketKey = GetBucketKey(method, ctx);
            if (!buckets.ContainsKey(bucketKey))
            {
                buckets[bucketKey] = new List<MethodModel>();
            }
            buckets[bucketKey].Add(method);
        }

        // Process each bucket: if multiple return types exist, keep one for class surface
        var resolvedMethods = new List<MethodModel>();
        var diagnostics = new List<Diagnostic>(type.Diagnostics);

        foreach (var (bucketKey, methods) in buckets)
        {
            if (methods.Count == 1)
            {
                // No conflict - keep as-is
                resolvedMethods.Add(methods[0]);
                continue;
            }

            // Check if there are different return types
            var returnTypes = methods
                .Select(m => NormalizeTypeReference(m.ReturnType))
                .Distinct()
                .ToList();

            if (returnTypes.Count == 1)
            {
                // Same return type - no conflict, keep all overloads
                resolvedMethods.AddRange(methods);
                continue;
            }

            // Return type conflict! Select one for class surface, mark others as ViewOnly
            var (keptMethod, movedMethods) = SelectRepresentativeMethod(methods);

            resolvedMethods.Add(keptMethod);

            // Mark moved methods as ViewOnly
            foreach (var moved in movedMethods)
            {
                resolvedMethods.Add(moved with { EmitScope = EmitScope.ViewOnly });
            }

            // Add diagnostic
            diagnostics.Add(new Diagnostic(
                "RETURN_CONFLICT",
                DiagnosticSeverity.Info,
                $"Return type conflict in '{type.ClrName}.{keptMethod.ClrName}': " +
                $"kept {NormalizeTypeReference(keptMethod.ReturnType)}, " +
                $"moved {movedMethods.Count} explicit implementation(s) to view-only"));
        }

        // If no changes, return original
        if (diagnostics.Count == type.Diagnostics.Count)
            return type;

        return type with
        {
            Members = type.Members with { Methods = resolvedMethods },
            Diagnostics = diagnostics
        };
    }

    /// <summary>
    /// Selects the representative method to keep on the class surface.
    /// Prefers public methods over explicit interface implementations.
    /// Returns (kept, moved) where moved methods become ViewOnly.
    /// </summary>
    private static (MethodModel Kept, List<MethodModel> Moved) SelectRepresentativeMethod(
        List<MethodModel> methods)
    {
        // Prefer public/non-explicit methods
        var publicMethods = methods.Where(m => !IsExplicitInterfaceImplementation(m)).ToList();
        var explicitMethods = methods.Where(IsExplicitInterfaceImplementation).ToList();

        if (publicMethods.Count > 0)
        {
            // Keep the first public method (deterministic order)
            // Move all explicit implementations to view-only
            var kept = publicMethods[0];
            var moved = new List<MethodModel>();
            moved.AddRange(publicMethods.Skip(1)); // Other public overloads with different returns (rare)
            moved.AddRange(explicitMethods); // All explicit implementations
            return (kept, moved);
        }

        // No public methods - keep first explicit, move rest
        // (This is rare - usually there's a public method)
        return (explicitMethods[0], explicitMethods.Skip(1).ToList());
    }

    /// <summary>
    /// Determines if a method is an explicit interface implementation.
    /// Heuristic: explicit implementations typically have interface-shaped names
    /// or are marked in metadata. For now, check if the return type suggests
    /// it's implementing a mutable interface (void return) vs immutable public API.
    /// </summary>
    private static bool IsExplicitInterfaceImplementation(MethodModel method)
    {
        // Heuristic: If return type is void and there are other overloads with
        // non-void returns, this is likely an explicit interface implementation
        // of a mutable interface like IList.Add(object):void vs public Add(T):ImmutableArray<T>

        // For now, use a simple heuristic: if return is void and name suggests mutation
        // (Add, Remove, Clear, Insert, etc.), it's likely explicit
        var isVoid = method.ReturnType.TypeName == "Void" &&
                     method.ReturnType.Namespace == "System";

        var isMutatingMethod = method.ClrName switch
        {
            "Add" => true,
            "Remove" => true,
            "Clear" => true,
            "Insert" => true,
            "RemoveAt" => true,
            _ => false
        };

        return isVoid && isMutatingMethod;
    }

    /// <summary>
    /// Generates a bucket key for a method based on name, parameters, and staticness.
    /// Format: "name(param1Type,param2Type,...)|static=true/false"
    /// </summary>
    private static string GetBucketKey(MethodModel method, AnalysisContext ctx)
    {
        var paramTypes = string.Join(",",
            method.Parameters.Select(p => NormalizeTypeReference(p.Type)));

        var methodName = ctx.GetMethodIdentifier(method);
        var staticFlag = method.IsStatic ? "|static=true" : "|static=false";

        return $"{methodName}({paramTypes}){staticFlag}";
    }

    /// <summary>
    /// Normalizes a TypeReference to a canonical string for comparison.
    /// </summary>
    private static string NormalizeTypeReference(TypeReference typeRef)
    {
        var genericArgs = typeRef.GenericArgs.Count > 0
            ? $"<{string.Join(",", typeRef.GenericArgs.Select(NormalizeTypeReference))}>"
            : "";

        var array = typeRef.ArrayRank > 0 ? "[]" : "";
        var pointer = typeRef.PointerDepth > 0 ? new string('*', typeRef.PointerDepth) : "";

        return $"{typeRef.Namespace}.{typeRef.TypeName}{genericArgs}{array}{pointer}";
    }
}
