using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

// InterfaceKey provides canonical key generation for interface lookups

namespace tsbindgen.Render.Analysis;

/// <summary>
/// The gate - decides whether to keep or drop `implements` based on structural equality.
///
/// Core rule: Never claim `implements I` unless class C is structurally equal to I after normalization.
///
/// For each class C and each interface I in C.Implements:
/// 1. Deep substitute I's type params with C's instantiation
/// 2. Get fanned-in interface surface (all inherited members)
/// 3. Normalize both C and I:
///    - Indexers as method pairs (already done by A2)
///    - Overload sets per member name
///    - Deep type equality after substitution
/// 4. Compare:
///    - Equal → keep in C.Implements
///    - Not equal → move to C.ExplicitViews, remove from C.Implements
///
/// This resolves TS2420 errors by only claiming `implements` when structurally true.
/// </summary>
public static class StructuralConformance
{
    /// <summary>
    /// Applies structural conformance check to all classes and structs.
    /// Only processes classes/structs (not interfaces).
    /// </summary>
    public static NamespaceModel Apply(
        NamespaceModel model,
        IReadOnlyDictionary<string, NamespaceModel> allModels,
        AnalysisContext ctx)
    {
        // Build global interface lookup for cross-namespace resolution
        var globalInterfaceLookup = BuildGlobalInterfaceLookup(allModels);

        var updatedTypes = model.Types.Select(type =>
        {
            // Only process classes and structs
            if (type.Kind == TypeKind.Interface || type.Kind == TypeKind.Enum || type.Kind == TypeKind.Delegate)
                return type;

            // Only process types that implement interfaces
            if (type.Implements.Count == 0)
                return type;

            return CheckConformance(type, globalInterfaceLookup, ctx);
        }).ToList();

        return model with { Types = updatedTypes };
    }

    /// <summary>
    /// Builds a lookup of all interfaces across all namespaces.
    /// Key: "Namespace.TypeName" (e.g., "System.Collections.Generic.IList_1")
    /// </summary>
    private static Dictionary<string, TypeModel> BuildGlobalInterfaceLookup(
        IReadOnlyDictionary<string, NamespaceModel> allModels)
    {
        var lookup = new Dictionary<string, TypeModel>();

        foreach (var (namespaceName, model) in allModels)
        {
            foreach (var type in model.Types)
            {
                if (type.Kind == TypeKind.Interface)
                {
                    // Use InterfaceKey for consistent key generation
                    var key = InterfaceKey.FromNames(namespaceName, type.ClrName);
                    lookup[key] = type;
                }
            }
        }

        return lookup;
    }

    /// <summary>
    /// Checks structural conformance for each implemented interface.
    /// Returns updated TypeModel with ExplicitViews populated and Implements filtered.
    /// </summary>
    private static TypeModel CheckConformance(
        TypeModel type,
        Dictionary<string, TypeModel> interfaceLookup,
        AnalysisContext ctx)
    {
        var keptImplements = new List<TypeReference>();
        var explicitViews = new List<InterfaceView>();

        // Build set of interfaces already in ConflictingInterfaces (handled by EmitExplicitInterfaceViews)
        var conflictingInterfaceKeys = new HashSet<string>();
        if (type.ConflictingInterfaces != null)
        {
            foreach (var conflictingInterface in type.ConflictingInterfaces)
            {
                var conflictingKey = $"{conflictingInterface.Namespace}.{conflictingInterface.TypeName}";
                conflictingInterfaceKeys.Add(conflictingKey);
            }
        }

        foreach (var interfaceRef in type.Implements)
        {
            // Use InterfaceKey for consistent key generation
            var key = InterfaceKey.FromTypeReference(interfaceRef);

            // Skip interfaces already in ConflictingInterfaces - they're handled by EmitExplicitInterfaceViews
            if (conflictingInterfaceKeys.Contains(key))
            {
                keptImplements.Add(interfaceRef);  // Keep in Implements so it's available to ConflictingInterfaces
                continue;
            }

            // Resolve interface type from local lookup first
            if (!interfaceLookup.TryGetValue(key, out var interfaceType))
            {
                // Interface not found in local models - try GlobalInterfaceIndex
                InterfaceSynopsis? synopsis = null;
                var foundInGlobal = ctx.GlobalInterfaceIndex != null &&
                    ctx.GlobalInterfaceIndex.TryGetInterface(key, out synopsis) &&
                    synopsis != null;

                if (foundInGlobal)
                {
                    // Found in global index - check conformance using synopsis
                    var globalClassSurface = GetClassSurface(type);
                    var globalInterfaceSurface = ConvertSynopsisToSurface(synopsis!);

                    if (!AreSurfacesEqual(globalClassSurface, globalInterfaceSurface))
                    {
                        // Not structurally equal - create explicit view
                        var viewName = GenerateViewName(interfaceRef);
                        var viewOnlyMethods = GetViewOnlyMethodsForInterface(type, globalInterfaceSurface, ctx);
                        explicitViews.Add(new InterfaceView(viewName, interfaceRef, viewOnlyMethods, Disambiguator: null));
                        continue; // Don't add to keptImplements
                    }
                }

                // Interface not found in either lookup, or conformance check passed - keep as-is
                keptImplements.Add(interfaceRef);
                continue;
            }

            // Build substitution map for generic parameters
            var substitutions = GenericSubstitution.BuildSubstitutionMap(
                interfaceRef,
                interfaceType.GenericParameters);

            // Get fully substituted interface surface
            var interfaceSurface = GetInterfaceSurface(interfaceType, interfaceRef, interfaceLookup, substitutions);

            // Get class surface
            var classSurface = GetClassSurface(type);

            // Compare surfaces
            if (AreSurfacesEqual(classSurface, interfaceSurface))
            {
                // Structurally equal - keep implements
                keptImplements.Add(interfaceRef);
            }
            else
            {
                // Not structurally equal - create explicit view
                var viewName = GenerateViewName(interfaceRef);
                var viewOnlyMethods = GetViewOnlyMethodsForInterface(type, interfaceSurface, ctx);
                explicitViews.Add(new InterfaceView(viewName, interfaceRef, viewOnlyMethods, Disambiguator: null));
            }
        }

        // If no changes needed, return original
        if (explicitViews.Count == 0)
            return type;

        // Apply disambiguation to view names if there are collisions
        var disambiguatedViews = DisambiguateViewNames(explicitViews);

        // Update type with filtered implements and explicit views
        return type with
        {
            Implements = keptImplements,
            ExplicitViews = disambiguatedViews
        };
    }

    /// <summary>
    /// Gets the fully substituted interface surface (all members, including inherited).
    /// Uses BFS to fan-in all ancestor interfaces with deep substitution at each edge.
    /// </summary>
    private static MemberSurface GetInterfaceSurface(
        TypeModel interfaceType,
        TypeReference interfaceRef,
        Dictionary<string, TypeModel> interfaceLookup,
        Dictionary<string, TypeReference> substitutions)
    {
        var surface = new MemberSurface();

        // Add interface's own members (with substitution)
        AddMembersToSurface(surface, interfaceType, substitutions);

        // Fan-in inherited interfaces via BFS
        var visited = new HashSet<string>();
        var queue = new Queue<(TypeReference Ref, Dictionary<string, TypeReference> Subs)>();

        // Seed with direct parents
        foreach (var parentRef in interfaceType.Implements)
        {
            queue.Enqueue((parentRef, substitutions));
        }

        while (queue.Count > 0)
        {
            var (currentRef, currentSubs) = queue.Dequeue();
            var key = $"{currentRef.Namespace}.{currentRef.TypeName}";

            if (visited.Contains(key))
                continue;

            visited.Add(key);

            // Resolve parent interface
            if (!interfaceLookup.TryGetValue(key, out var parentType))
                continue; // External assembly

            // Build substitution map for this parent
            var parentSubs = GenericSubstitution.BuildSubstitutionMap(currentRef, parentType.GenericParameters);

            // Combine with current substitutions (deep substitution)
            var combinedSubs = CombineSubstitutions(currentSubs, parentSubs);

            // Add parent's members
            AddMembersToSurface(surface, parentType, combinedSubs);

            // Enqueue parent's parents
            foreach (var grandparentRef in parentType.Implements)
            {
                queue.Enqueue((grandparentRef, combinedSubs));
            }
        }

        return surface;
    }

    /// <summary>
    /// Gets the class surface (only public instance members with EmitScope.Class).
    /// This represents the TypeScript-representable surface after conflict resolution.
    /// </summary>
    private static MemberSurface GetClassSurface(TypeModel type)
    {
        var surface = new MemberSurface();

        // Add members without substitution (class is concrete)
        // Only include methods with EmitScope.Class (excludes ViewOnly explicit interface implementations)
        AddMembersToSurface(surface, type, new Dictionary<string, TypeReference>(), classRepresentableSurfaceOnly: true);

        return surface;
    }

    /// <summary>
    /// Adds type's members to surface with substitution applied.
    /// Groups by member name for overload comparison.
    /// </summary>
    private static void AddMembersToSurface(
        MemberSurface surface,
        TypeModel type,
        Dictionary<string, TypeReference> substitutions,
        bool classRepresentableSurfaceOnly = false)
    {
        // Add methods (indexers are already method pairs from A2)
        foreach (var method in type.Members.Methods)
        {
            // Skip static methods (not part of instance surface)
            if (method.IsStatic)
                continue;

            // Skip ViewOnly methods when building class representable surface
            if (classRepresentableSurfaceOnly && method.EmitScope == EmitScope.ViewOnly)
                continue;

            var substitutedMethod = substitutions.Count > 0
                ? GenericSubstitution.SubstituteMethod(method, substitutions)
                : method;

            var signature = NormalizeMethodSignature(substitutedMethod);
            surface.AddMethod(method.ClrName, signature);
        }

        // Add properties (non-indexers only - indexers are method pairs)
        foreach (var property in type.Members.Properties)
        {
            // Skip indexers (already handled as method pairs)
            if (property.IsIndexer)
                continue;

            // Skip static properties
            if (property.IsStatic)
                continue;

            var substitutedProperty = substitutions.Count > 0
                ? GenericSubstitution.SubstituteProperty(property, substitutions)
                : property;

            var signature = NormalizePropertySignature(substitutedProperty);
            surface.AddProperty(property.ClrName, signature);
        }

        // Note: Fields and events intentionally skipped - they don't appear in interfaces
    }

    /// <summary>
    /// Combines two substitution maps with deep recursion.
    /// If inner map has T → U and outer map has U → int, result has T → int.
    /// </summary>
    private static Dictionary<string, TypeReference> CombineSubstitutions(
        Dictionary<string, TypeReference> outer,
        Dictionary<string, TypeReference> inner)
    {
        var combined = new Dictionary<string, TypeReference>();

        // Apply outer substitutions to inner mappings
        foreach (var (paramName, innerType) in inner)
        {
            var substitutedType = GenericSubstitution.SubstituteType(innerType, outer);
            combined[paramName] = substitutedType;
        }

        // Add outer substitutions not in inner
        foreach (var (paramName, outerType) in outer)
        {
            if (!combined.ContainsKey(paramName))
            {
                combined[paramName] = outerType;
            }
        }

        return combined;
    }

    /// <summary>
    /// Compares two member surfaces for structural equality.
    /// Returns true if all member names and signatures match exactly.
    /// </summary>
    private static bool AreSurfacesEqual(MemberSurface classSurface, MemberSurface interfaceSurface)
    {
        // Compare methods
        if (!AreMemberSetsEqual(classSurface.Methods, interfaceSurface.Methods))
            return false;

        // Compare properties
        if (!AreMemberSetsEqual(classSurface.Properties, interfaceSurface.Properties))
            return false;

        return true;
    }

    /// <summary>
    /// Compares two member sets (by name and signature sets).
    /// </summary>
    private static bool AreMemberSetsEqual(
        Dictionary<string, HashSet<string>> classMembers,
        Dictionary<string, HashSet<string>> interfaceMembers)
    {
        // All interface members must exist in class with same signatures
        foreach (var (name, interfaceSigs) in interfaceMembers)
        {
            if (!classMembers.TryGetValue(name, out var classSigs))
                return false; // Missing member

            // All interface signatures must exist in class
            foreach (var sig in interfaceSigs)
            {
                if (!classSigs.Contains(sig))
                    return false; // Missing signature
            }
        }

        // Note: Class can have EXTRA members (fine for structural conformance)
        // Only care that ALL interface members exist in class

        return true;
    }

    /// <summary>
    /// Normalizes method signature for comparison.
    /// Format: "(param1Type,param2Type):ReturnType"
    /// Ignores parameter names (only types matter).
    /// </summary>
    private static string NormalizeMethodSignature(MethodModel method)
    {
        var paramTypes = string.Join(",", method.Parameters.Select(p =>
            TypeReferenceToString(p.Type)));

        var returnType = TypeReferenceToString(method.ReturnType);

        return $"({paramTypes}):{returnType}";
    }

    /// <summary>
    /// Normalizes property signature for comparison.
    /// Format: "Type" (properties identified by name + type)
    /// </summary>
    private static string NormalizePropertySignature(PropertyModel property)
    {
        return TypeReferenceToString(property.Type);
    }

    /// <summary>
    /// Converts TypeReference to normalized string for signature comparison.
    /// Fully qualified with generics.
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

    /// <summary>
    /// Generates view name for interface.
    /// Format: "As_<InterfaceName>[_Of_<GenericArg1>_<GenericArg2>...]"
    /// Examples:
    ///   - As_IList (non-generic)
    ///   - As_IList_1_Of_XPathNavigator (closed generic with concrete type)
    ///   - As_IList_1_Of_T (generic with type parameter)
    /// </summary>
    private static string GenerateViewName(TypeReference interfaceRef)
    {
        // Extract base name and arity from CLR name (e.g., "IList`1" → "IList", "1")
        var fullName = interfaceRef.TypeName;
        var baseName = fullName;
        var arity = "";

        var backtickIndex = fullName.IndexOf('`');
        if (backtickIndex > 0)
        {
            baseName = fullName.Substring(0, backtickIndex);
            arity = fullName.Substring(backtickIndex + 1); // "1", "2", etc.
        }

        // Start with basic name
        var viewName = $"As_{baseName}";

        // Add generic arguments if present to disambiguate different closed generics
        if (interfaceRef.GenericArgs.Count > 0)
        {
            var argNames = new List<string>();
            foreach (var arg in interfaceRef.GenericArgs)
            {
                var argName = GetTypeArgumentName(arg);
                if (argName != null)
                {
                    argNames.Add(argName);
                }
            }

            if (argNames.Count > 0)
            {
                var argSuffix = string.Join("_", argNames);
                // Use actual arity if present (e.g., "As_IList_1_Of_XPathNavigator")
                if (!string.IsNullOrEmpty(arity))
                {
                    viewName = $"As_{baseName}_{arity}_Of_{argSuffix}";
                }
                else
                {
                    viewName = $"As_{baseName}_Of_{argSuffix}";
                }
            }
        }

        return viewName;
    }

    /// <summary>
    /// Gets a name for a type argument suitable for view naming.
    /// </summary>
    private static string? GetTypeArgumentName(TypeReference typeRef)
    {
        if (typeRef.Kind == TypeReferenceKind.GenericParameter)
        {
            return typeRef.TypeName; // e.g., "T", "TKey", "TSelf"
        }

        // For closed types, use the type name without generic arity
        var name = typeRef.TypeName;
        var backtickIndex = name.IndexOf('`');
        if (backtickIndex > 0)
        {
            name = name.Substring(0, backtickIndex);
        }
        return name;
    }

    /// <summary>
    /// Converts an InterfaceSynopsis from GlobalInterfaceIndex to a MemberSurface for comparison.
    /// </summary>
    private static MemberSurface ConvertSynopsisToSurface(InterfaceSynopsis synopsis)
    {
        var surface = new MemberSurface();

        // Add methods from synopsis
        foreach (var method in synopsis.Methods)
        {
            var paramTypes = string.Join(",", method.Parameters.Select(TypeReferenceToString));
            var returnType = TypeReferenceToString(method.ReturnType);
            var signature = $"({paramTypes}):{returnType}";

            surface.AddMethod(method.Name, signature);
        }

        // Add properties from synopsis
        foreach (var property in synopsis.Properties)
        {
            var signature = TypeReferenceToString(property.Type);
            surface.AddProperty(property.Name, signature);
        }

        return surface;
    }

    /// <summary>
    /// Represents the normalized member surface of a type.
    /// Groups members by name, then by signature (to handle overloads).
    /// </summary>
    private class MemberSurface
    {
        public Dictionary<string, HashSet<string>> Methods { get; } = new();
        public Dictionary<string, HashSet<string>> Properties { get; } = new();

        public void AddMethod(string name, string signature)
        {
            if (!Methods.TryGetValue(name, out var signatures))
            {
                signatures = new HashSet<string>();
                Methods[name] = signatures;
            }
            signatures.Add(signature);
        }

        public void AddProperty(string name, string signature)
        {
            if (!Properties.TryGetValue(name, out var signatures))
            {
                signatures = new HashSet<string>();
                Properties[name] = signatures;
            }
            signatures.Add(signature);
        }
    }

    /// <summary>
    /// Gets ViewOnly methods from the type that match the specified interface surface.
    /// This ensures each explicit view only claims methods that actually implement that specific interface.
    /// </summary>
    private static IReadOnlyList<MethodModel> GetViewOnlyMethodsForInterface(
        TypeModel type,
        MemberSurface interfaceSurface,
        AnalysisContext ctx)
    {
        var viewOnlyMethods = type.Members.Methods
            .Where(m => m.EmitScope == EmitScope.ViewOnly)
            .ToList();

        if (viewOnlyMethods.Count == 0)
            return Array.Empty<MethodModel>();

        // Build set of interface method signatures for fast lookup
        var interfaceSignatures = new HashSet<string>();
        foreach (var (methodName, signatures) in interfaceSurface.Methods)
        {
            foreach (var sig in signatures)
            {
                interfaceSignatures.Add($"{methodName}:{sig}");
            }
        }

        // Filter ViewOnly methods to only those that match interface surface
        var filtered = new List<MethodModel>();
        foreach (var method in viewOnlyMethods)
        {
            var signature = NormalizeMethodSignature(method);
            var key = $"{method.ClrName}:{signature}";

            if (interfaceSignatures.Contains(key))
            {
                filtered.Add(method);
            }
        }

        return filtered;
    }

    /// <summary>
    /// Disambiguates view names when multiple views have the same name.
    /// Uses FNV-1a hash of the full interface type for deterministic suffixes.
    /// </summary>
    private static List<InterfaceView> DisambiguateViewNames(List<InterfaceView> views)
    {
        // Group views by base name
        var nameGroups = views
            .GroupBy(v => v.ViewName)
            .ToList();

        // Check if any group has collisions
        var hasCollisions = nameGroups.Any(g => g.Count() > 1);
        if (!hasCollisions)
            return views; // No collisions, return as-is

        // Rebuild list with disambiguators
        var disambiguated = new List<InterfaceView>();
        foreach (var group in nameGroups)
        {
            if (group.Count() == 1)
            {
                // No collision for this name - keep as-is
                disambiguated.Add(group.First());
            }
            else
            {
                // Collision - add hash-based disambiguators
                // Sort by full interface name for deterministic ordering
                var sorted = group
                    .OrderBy(v => v.Interface.Namespace)
                    .ThenBy(v => v.Interface.TypeName)
                    .ThenBy(v => string.Join(",", v.Interface.GenericArgs.Select(GetTypeReferenceKey)))
                    .ToList();

                foreach (var view in sorted)
                {
                    var fullInterfaceName = GetTypeReferenceKey(view.Interface);
                    var hash = ComputeFnv1aHash(fullInterfaceName);
                    var disambiguator = $"_{hash:x8}"; // 8-character hex suffix

                    disambiguated.Add(view with { Disambiguator = disambiguator });
                }
            }
        }

        return disambiguated;
    }

    /// <summary>
    /// Gets a unique key for a TypeReference (namespace.TypeName with generic args).
    /// </summary>
    private static string GetTypeReferenceKey(TypeReference typeRef)
    {
        var genericArgs = typeRef.GenericArgs.Count > 0
            ? $"<{string.Join(",", typeRef.GenericArgs.Select(GetTypeReferenceKey))}>"
            : "";

        var array = typeRef.ArrayRank > 0 ? "[]" : "";
        var pointer = typeRef.PointerDepth > 0 ? new string('*', typeRef.PointerDepth) : "";

        return $"{typeRef.Namespace}.{typeRef.TypeName}{genericArgs}{array}{pointer}";
    }

    /// <summary>
    /// Computes FNV-1a 32-bit hash for a string.
    /// Deterministic hash algorithm for stable view name disambiguation.
    /// </summary>
    private static uint ComputeFnv1aHash(string input)
    {
        const uint FnvPrime = 0x01000193;
        const uint FnvOffsetBasis = 0x811c9dc5;

        uint hash = FnvOffsetBasis;
        foreach (var c in input)
        {
            hash ^= c;
            hash *= FnvPrime;
        }
        return hash;
    }
}
