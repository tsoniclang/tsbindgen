using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Analysis;

/// <summary>
/// Resolves TS2430 errors caused by method signature conflicts between child and parent interfaces.
///
/// Category B errors: Same method name, different signature
/// Example:
/// - Parent: Add(item: object): int
/// - Child:  Add(name: string, value: string): IColumnMapping
///
/// TypeScript requires child interfaces to have compatible signatures with parents.
/// This pass adds parent method signatures as overloads in child interfaces.
///
/// Strategy:
/// 1. For each interface, collect all ancestor interfaces
/// 2. For methods in child that exist in parent with different signature:
///    - Add parent signature as overload in child
/// 3. Use signature normalization to detect conflicts (ignore parameter names)
/// </summary>
public static class InterfaceOverloadFanIn
{
    /// <summary>
    /// Adds parent method signatures as overloads in child interfaces to resolve TS2430 errors.
    /// Only processes interfaces (not classes/structs).
    /// </summary>
    public static NamespaceModel Apply(
        NamespaceModel model,
        IReadOnlyDictionary<string, NamespaceModel> allModels,
        AnalysisContext ctx)
    {
        // Build global interface lookup for cross-namespace parent resolution
        var globalInterfaceLookup = BuildGlobalInterfaceLookup(allModels);

        var updatedTypes = model.Types.Select(type =>
        {
            // Only process interfaces
            if (type.Kind != TypeKind.Interface)
                return type;

            // Only process interfaces that implement other interfaces
            if (type.Implements.Count == 0)
                return type;

            return AddParentOverloads(type, globalInterfaceLookup, ctx);
        }).ToList();

        return model with { Types = updatedTypes };
    }

    /// <summary>
    /// Builds a lookup of all interfaces across all namespaces for resolving parents.
    /// Key: "Namespace.TypeName" (e.g., "System.Collections.IList")
    /// </summary>
    private static Dictionary<string, TypeModel> BuildGlobalInterfaceLookup(
        IReadOnlyDictionary<string, NamespaceModel> allModels)
    {
        var lookup = new Dictionary<string, TypeModel>();

        foreach (var (_, model) in allModels)
        {
            foreach (var type in model.Types)
            {
                if (type.Kind == TypeKind.Interface)
                {
                    var key = $"{type.Binding.Type.Namespace}.{type.ClrName}";
                    lookup[key] = type;
                }
            }
        }

        return lookup;
    }

    /// <summary>
    /// Adds parent method signatures as overloads in child interface.
    /// </summary>
    private static TypeModel AddParentOverloads(
        TypeModel type,
        Dictionary<string, TypeModel> interfaceLookup,
        AnalysisContext ctx)
    {
        // Collect all ancestor interfaces
        var ancestors = CollectAncestors(type, interfaceLookup);

        if (ancestors.Count == 0)
            return type;

        // Build a set of child method signatures (normalized, ignore param names)
        var childSignatures = new HashSet<string>();
        foreach (var method in type.Members.Methods)
        {
            var sig = NormalizeSignature(method);
            childSignatures.Add(sig);
        }

        // Collect parent methods that need to be added as overloads
        var overloadsToAdd = new List<MethodModel>();

        foreach (var (ancestorRef, ancestorType) in ancestors)
        {
            foreach (var ancestorMethod in ancestorType.Members.Methods)
            {
                // Check if child has a method with same name but different signature
                var childMethodsWithSameName = type.Members.Methods
                    .Where(m => m.ClrName == ancestorMethod.ClrName)
                    .ToList();

                if (childMethodsWithSameName.Count == 0)
                    continue; // No name conflict, skip

                var ancestorSig = NormalizeSignature(ancestorMethod);

                // If child already has this exact signature, skip
                if (childSignatures.Contains(ancestorSig))
                    continue;

                // Signature conflict detected - add parent signature as overload
                // Apply generic substitution if ancestor is a generic instantiation
                var substitutedMethod = ancestorMethod;
                if (ancestorRef.GenericArgs.Count > 0 && ancestorType.GenericParameters.Count > 0)
                {
                    var substitutions = GenericSubstitution.BuildSubstitutionMap(
                        ancestorRef,
                        ancestorType.GenericParameters);
                    substitutedMethod = GenericSubstitution.SubstituteMethod(ancestorMethod, substitutions);
                }

                overloadsToAdd.Add(substitutedMethod);
                childSignatures.Add(ancestorSig); // Track to avoid duplicates
            }
        }

        if (overloadsToAdd.Count == 0)
            return type;

        // Add overloads to child interface
        var updatedMethods = type.Members.Methods.Concat(overloadsToAdd).ToList();
        var updatedMembers = type.Members with { Methods = updatedMethods };

        return type with { Members = updatedMembers };
    }

    /// <summary>
    /// Collects all ancestor interfaces (parents, grandparents, etc.) via BFS.
    /// Returns list of (TypeReference, TypeModel) pairs.
    /// </summary>
    private static List<(TypeReference Ref, TypeModel Type)> CollectAncestors(
        TypeModel type,
        Dictionary<string, TypeModel> interfaceLookup)
    {
        var ancestors = new List<(TypeReference, TypeModel)>();
        var visited = new HashSet<string>(); // Track by "Namespace.TypeName"
        var queue = new Queue<TypeReference>();

        // Seed with direct parents
        foreach (var parent in type.Implements)
        {
            queue.Enqueue(parent);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var key = $"{current.Namespace}.{current.TypeName}";

            if (visited.Contains(key))
                continue;

            visited.Add(key);

            // Resolve parent type
            if (!interfaceLookup.TryGetValue(key, out var parentType))
                continue; // Parent not found (external assembly or missing)

            ancestors.Add((current, parentType));

            // Add parent's parents to queue
            foreach (var grandparent in parentType.Implements)
            {
                queue.Enqueue(grandparent);
            }
        }

        return ancestors;
    }

    /// <summary>
    /// Normalizes a method signature for comparison.
    /// Format: "MethodName(param1Type,param2Type):ReturnType"
    /// Ignores parameter names to detect signature conflicts.
    /// </summary>
    private static string NormalizeSignature(MethodModel method)
    {
        var paramTypes = string.Join(",", method.Parameters.Select(p =>
            TypeReferenceToString(p.Type)));

        var returnType = TypeReferenceToString(method.ReturnType);

        return $"{method.ClrName}({paramTypes}):{returnType}";
    }

    /// <summary>
    /// Converts a TypeReference to a normalized string for signature comparison.
    /// </summary>
    private static string TypeReferenceToString(TypeReference typeRef)
    {
        var genericArgs = typeRef.GenericArgs.Count > 0
            ? $"<{string.Join(",", typeRef.GenericArgs.Select(TypeReferenceToString))}>"
            : "";

        var array = typeRef.ArrayRank > 0 ? "[]" : "";
        var pointer = typeRef.PointerDepth > 0 ? new string('*', typeRef.PointerDepth) : "";

        return $"{typeRef.Namespace}.{typeRef.TypeName}{genericArgs}{array}{pointer}";
    }
}
