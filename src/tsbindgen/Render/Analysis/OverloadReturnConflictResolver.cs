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
            // CRITICAL: Never touch static methods - they must stay on class surface
            // Static side manipulation causes TS2417 regressions
            if (methods.All(m => m.IsStatic))
            {
                resolvedMethods.AddRange(methods);
                continue;
            }

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
    /// Prefers non-void-returning methods (immutable API) over void-returning (mutators).
    /// Returns (kept, moved) where moved methods become ViewOnly.
    /// </summary>
    private static (MethodModel Kept, List<MethodModel> Moved) SelectRepresentativeMethod(
        List<MethodModel> methods)
    {
        // Separate methods by return type: non-void (immutable) vs void (mutator)
        var nonVoidMethods = methods.Where(m => !IsVoidReturn(m)).ToList();
        var voidMethods = methods.Where(IsVoidReturn).ToList();

        if (nonVoidMethods.Count > 0)
        {
            // Prefer non-void (immutable API) - select the best one
            var kept = SelectBestMethod(nonVoidMethods);
            var moved = new List<MethodModel>();
            moved.AddRange(nonVoidMethods.Where(m => m != kept));
            moved.AddRange(voidMethods); // All void-returning methods (mutators)
            return (kept, moved);
        }

        // All methods return void - select best mutator
        var keptMutator = SelectBestMethod(voidMethods);
        var movedMutators = voidMethods.Where(m => m != keptMutator).ToList();
        return (keptMutator, movedMutators);
    }

    /// <summary>
    /// Selects the best method from a list using deterministic tiebreakers.
    /// Prioritizes: return type specificity > non-virtual > lexical order.
    /// </summary>
    private static MethodModel SelectBestMethod(List<MethodModel> methods)
    {
        if (methods.Count == 1)
            return methods[0];

        return methods
            .OrderByDescending(m => GetReturnTypeSpecificity(m.ReturnType))
            .ThenBy(m => m.IsVirtual ? 1 : 0) // Non-virtual first
            .ThenBy(m => m.ReturnType.Namespace ?? "")
            .ThenBy(m => m.ReturnType.TypeName ?? "")
            .First();
    }

    /// <summary>
    /// Returns specificity ranking for return types.
    /// Higher = more specific (concrete types > generic params > Object > void).
    /// </summary>
    private static int GetReturnTypeSpecificity(TypeReference returnType)
    {
        // Void is least specific
        if (returnType.Namespace == "System" && returnType.TypeName == "Void")
            return 0;
        // Object is very unspecific
        if (returnType.Namespace == "System" && returnType.TypeName == "Object")
            return 1;
        // Generic parameter is less specific than concrete types
        if (returnType.Kind == TypeReferenceKind.GenericParameter)
            return 2;
        // Concrete types are most specific
        return 3;
    }

    /// <summary>
    /// Checks if a method returns void.
    /// </summary>
    private static bool IsVoidReturn(MethodModel method)
    {
        return method.ReturnType.TypeName == "Void" &&
               method.ReturnType.Namespace == "System";
    }

    /// <summary>
    /// Generates a hardened bucket key for a method with full signature details.
    /// Format: "name`arity(kind:type:optional:params,...)|accessor=get/set/none|static=true/false"
    /// Includes generic arity, parameter kinds (ref/out/in), optional flags, and params flags.
    /// </summary>
    private static string GetBucketKey(MethodModel method, AnalysisContext ctx)
    {
        var paramSignatures = string.Join(",", method.Parameters.Select(p =>
        {
            var typeStr = NormalizeTypeReference(p.Type);
            var kindStr = p.Kind.ToString().ToLowerInvariant(); // in, ref, out, params
            var optionalStr = p.IsOptional ? "opt" : "req";
            var paramsStr = p.IsParams ? "params" : "noparams";
            return $"{kindStr}:{typeStr}:{optionalStr}:{paramsStr}";
        }));

        var methodName = ctx.GetMethodIdentifier(method);
        var genericArity = method.GenericParameters.Count > 0 ? $"`{method.GenericParameters.Count}" : "";

        // Detect accessor kind (get_/set_ prefix)
        var accessorKind = "none";
        if (method.ClrName.StartsWith("get_"))
            accessorKind = "get";
        else if (method.ClrName.StartsWith("set_"))
            accessorKind = "set";

        var staticFlag = method.IsStatic ? "|static=true" : "|static=false";

        return $"{methodName}{genericArity}({paramSignatures})|accessor={accessorKind}{staticFlag}";
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
