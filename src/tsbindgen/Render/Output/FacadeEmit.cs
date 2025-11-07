using System.Linq;
using System.Text;
using tsbindgen.Config;
using tsbindgen.Render;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Output;

/// <summary>
/// Phase 4: Generates clean facade .d.ts files that hide ugly _N arity suffixes.
///
/// For single-arity types (e.g., only List_1):
///   export type List&lt;T&gt; = List_1&lt;T&gt;;
///   export const List: typeof List_1;
///
/// For multi-arity types (e.g., Action_1, Action_2, Action_3...):
///   Uses sentinel pattern to route based on provided type arguments
/// </summary>
public static class FacadeEmit
{
    public static string Generate(NamespaceModel model, AnalysisContext ctx)
    {
        var sb = new StringBuilder();

        // Header comment
        sb.AppendLine("// Auto-generated facade - provides clean names for generic types");
        sb.AppendLine("// Users should import from this file, not from internal/index");
        sb.AppendLine();

        // Shared sentinel symbol (used across all multi-arity types)
        sb.AppendLine("declare const __unspecified: unique symbol;");
        sb.AppendLine("type __ = typeof __unspecified;");
        sb.AppendLine();

        // Group types by base name (strip arity)
        var typesByBaseName = new Dictionary<string, List<TypeModel>>();
        var nonGenericTypesByName = new Dictionary<string, TypeModel>();

        foreach (var type in model.Types)
        {
            if (type.ClrName.Contains('`'))
            {
                // Generic type
                var baseName = StripArity(ctx.GetTypeIdentifier(type));
                if (!typesByBaseName.ContainsKey(baseName))
                {
                    typesByBaseName[baseName] = new List<TypeModel>();
                }
                typesByBaseName[baseName].Add(type);
            }
            else
            {
                // Non-generic type
                nonGenericTypesByName[ctx.GetTypeIdentifier(type)] = type;
            }
        }

        // Partition constraint types into three buckets:
        // 1. Type parameters (don't import)
        // 2. Same-namespace types (import from ./internal/index)
        // 3. Cross-namespace types (import type * as Namespace)
        var typeParameterNames = new HashSet<string>();
        var sameNamespaceTypes = new HashSet<string>();
        var crossNamespaceTypes = new Dictionary<string, HashSet<string>>(); // namespace -> type names

        // Collect all type parameter names from generic types
        foreach (var types in typesByBaseName.Values)
        {
            foreach (var type in types)
            {
                foreach (var gp in type.GenericParameters)
                {
                    typeParameterNames.Add(ctx.GetGenericParameterIdentifier(gp));
                }
            }
        }

        // Partition constraint types
        foreach (var types in typesByBaseName.Values)
        {
            foreach (var type in types)
            {
                foreach (var gp in type.GenericParameters)
                {
                    foreach (var constraint in gp.Constraints)
                    {
                        PartitionConstraintTypes(constraint, model.ClrName, typeParameterNames,
                            sameNamespaceTypes, crossNamespaceTypes);
                    }
                }
            }
        }

        // Generate cross-namespace imports (namespace imports)
        foreach (var (ns, types) in crossNamespaceTypes.OrderBy(kvp => kvp.Key))
        {
            var nsAlias = ns.Replace('.', '$');
            sb.AppendLine($"import type * as {nsAlias} from \"../{ns}/internal/index\";");
        }
        if (crossNamespaceTypes.Count > 0)
        {
            sb.AppendLine();
        }

        // Generate same-namespace imports (named imports)
        if (typesByBaseName.Count > 0 || sameNamespaceTypes.Count > 0)
        {
            sb.Append("import { ");

            var importNames = new List<string>();

            // Add same-namespace constraint types
            importNames.AddRange(sameNamespaceTypes.OrderBy(x => x));

            // Add all generic types
            importNames.AddRange(typesByBaseName.Values
                .SelectMany(list => list.Select(t => t.TsEmitName)));

            // Add non-generic types that collide with generic types (import with alias)
            var collidingNonGenerics = nonGenericTypesByName
                .Where(kvp => typesByBaseName.ContainsKey(kvp.Key))
                .Select(kvp => $"{kvp.Value.TsEmitName} as {kvp.Value.TsEmitName}_0");
            importNames.AddRange(collidingNonGenerics);

            // Deduplicate
            importNames = importNames.Distinct().ToList();

            sb.Append(string.Join(", ", importNames));
            sb.AppendLine(" } from './internal/index';");
            sb.AppendLine();
        }

        // Re-export non-generic types (excluding those that collide with generic types)
        var nonCollidingNonGenerics = nonGenericTypesByName
            .Where(kvp => !typesByBaseName.ContainsKey(kvp.Key))
            .Select(kvp => kvp.Value)
            .ToList();

        if (nonCollidingNonGenerics.Count > 0)
        {
            sb.AppendLine("// Re-export non-generic types");
            sb.Append("export { ");
            sb.Append(string.Join(", ", nonCollidingNonGenerics.Select(t => t.TsEmitName)));
            sb.AppendLine(" } from './internal/index';");
            sb.AppendLine();
        }

        // Generate facades for each base name
        foreach (var (baseName, types) in typesByBaseName.OrderBy(kvp => kvp.Key))
        {
            // Check if there's also a non-generic version with the same name
            bool hasNonGeneric = nonGenericTypesByName.ContainsKey(baseName);

            if (hasNonGeneric || types.Count > 1)
            {
                // Multiple arities OR collision with non-generic - use sentinel pattern
                var allTypes = new List<TypeModel>();

                // Add non-generic version if it exists
                if (hasNonGeneric)
                {
                    allTypes.Add(nonGenericTypesByName[baseName]);
                }

                // Add all generic versions
                allTypes.AddRange(types);

                GenerateMultiArityFacade(sb, baseName, allTypes, model.ClrName, ctx);
            }
            else
            {
                // Single generic arity with no non-generic collision - simple re-export
                var type = types[0];
                GenerateSingleArityFacade(sb, baseName, type, model.ClrName, ctx);
            }
        }

        return sb.ToString();
    }

    private static void GenerateSingleArityFacade(StringBuilder sb, string cleanName, TypeModel type, string currentNamespace, AnalysisContext ctx)
    {
        var internalName = type.TsEmitName;

        // Generate type alias with mirrored constraints
        sb.Append($"export type {cleanName}");

        // Add type parameters WITH constraints (mirrored from internal type)
        // Constraints are resolved via import type, no cross-namespace references
        if (type.GenericParameters.Count > 0)
        {
            // Build generic parameter scope map: CLR name → facade name
            var gpMap = type.GenericParameters.ToDictionary(
                gp => gp.Name,
                gp => ctx.GetGenericParameterIdentifier(gp));

            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp =>
            {
                var param = ctx.GetGenericParameterIdentifier(gp);
                if (gp.Constraints.Count > 0)
                {
                    // Format constraints: only add namespace prefix for cross-namespace types
                    param += " extends " + string.Join(" & ", gp.Constraints.Select(c =>
                        FormatConstraintReference(c, currentNamespace, gpMap)));
                }
                return param;
            })));
            sb.Append('>');
        }

        sb.Append(" = ");
        sb.Append(internalName);

        // Add type arguments
        if (type.GenericParameters.Count > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", type.GenericParameters.Select(gp => ctx.GetGenericParameterIdentifier(gp))));
            sb.Append('>');
        }

        sb.AppendLine(";");

        // Generate value export (for constructor)
        // Only emit for runtime-bearing types: class and struct
        // Skip type-only: interface, delegate
        if (IsConstructible(type))
        {
            sb.AppendLine($"export const {cleanName}: typeof {internalName};");
        }
        sb.AppendLine();
    }

    private static void GenerateMultiArityFacade(StringBuilder sb, string baseName, List<TypeModel> types, string currentNamespace, AnalysisContext ctx)
    {
        // Sort by arity
        var sorted = types.OrderBy(t => t.GenericParameters.Count).ToList();
        var maxArity = sorted.Last().GenericParameters.Count;

        // Comment (uses shared sentinel from top of file)
        sb.AppendLine($"// {baseName} has multiple arities - using sentinel pattern");

        // Generate type alias with sentinel defaults
        sb.Append($"export type {baseName}<");

        // Add type parameters with sentinel defaults (no constraints on params themselves)
        var typeParams = new List<string>();
        for (int i = 0; i < maxArity; i++)
        {
            var paramName = $"T{i + 1}";
            typeParams.Add($"{paramName} = __");
        }
        sb.Append(string.Join(", ", typeParams));
        sb.Append("> =\n");

        // Generate conditional type routing
        for (int i = 0; i < sorted.Count; i++)
        {
            var type = sorted[i];
            var arity = type.GenericParameters.Count;

            sb.Append("  ");

            // Last item is the fallback - no condition needed
            bool isLast = (i == sorted.Count - 1);

            if (!isLast)
            {
                // Check if T(arity+1) is unspecified (shared sentinel)
                var checkParam = $"T{arity + 1}";
                sb.Append($"[{checkParam}] extends [__] ? ");
            }

            // Emit the type
            var internalName = type.TsEmitName;

            // For non-generic types that collide, we imported them with _0 suffix
            if (arity == 0)
            {
                sb.Append($"{internalName}_0");
            }
            else
            {
                sb.Append(internalName);

                // Add type arguments with intersection constraints (Option A from senior dev)
                // T & ConstraintType resolves constraint via import type
                sb.Append('<');
                var args = new List<string>();

                // Build generic parameter scope map: CLR name → facade name
                var gpMap = new Dictionary<string, string>();
                for (int j = 0; j < arity; j++)
                {
                    var gp = type.GenericParameters[j];
                    var facadeName = $"T{j + 1}";
                    gpMap[gp.Name] = facadeName;
                }

                for (int j = 0; j < arity; j++)
                {
                    var paramName = $"T{j + 1}";
                    var gp = type.GenericParameters[j];

                    if (gp.Constraints.Count > 0)
                    {
                        // Apply intersection: T1 & (IEquatable_1<T1> & ValueType)
                        var constraints = string.Join(" & ", gp.Constraints.Select(c =>
                            FormatConstraintReference(c, currentNamespace, gpMap)));
                        args.Add($"{paramName} & ({constraints})");
                    }
                    else
                    {
                        args.Add(paramName);
                    }
                }
                sb.Append(string.Join(", ", args));
                sb.Append('>');
            }

            if (isLast)
            {
                sb.AppendLine(";");
            }
            else
            {
                sb.AppendLine(" :");
            }
        }

        sb.AppendLine();

        // Generate value export (constructor interface)
        // Only emit if at least one type is constructible (class or struct)
        // Skip if all types are type-only (interface, delegate)
        bool hasConstructible = sorted.Any(IsConstructible);

        if (hasConstructible)
        {
            sb.AppendLine($"export interface {baseName}Constructor {{");
            foreach (var type in sorted)
            {
                // Skip type-only types in constructor interface
                if (!IsConstructible(type))
                {
                    continue;
                }

                var internalName = type.TsEmitName;
                var arity = type.GenericParameters.Count;

                // For non-generic types that collide, we imported them with _0 suffix
                if (arity == 0)
                {
                    internalName = $"{internalName}_0";
                }

                sb.Append($"  new");

                // Type parameters WITH constraints (mirrored from internal type)
                if (arity > 0)
                {
                    // Build generic parameter scope map: CLR name → facade name
                    var gpMap = type.GenericParameters.ToDictionary(
                        gp => gp.Name,
                        gp => ctx.GetGenericParameterIdentifier(gp));

                    sb.Append('<');
                    sb.Append(string.Join(", ", type.GenericParameters.Select(gp =>
                    {
                        var param = ctx.GetGenericParameterIdentifier(gp);
                        if (gp.Constraints.Count > 0)
                        {
                            // Mirror constraints from internal type
                            param += " extends " + string.Join(" & ", gp.Constraints.Select(c => FormatConstraintReference(c, currentNamespace, gpMap)));
                        }
                        return param;
                    })));
                    sb.Append('>');
                }

                sb.Append("(");
                // TODO: Add constructor parameters if needed
                sb.Append("...args: any[]");
                sb.Append("): ");
                sb.Append(internalName);

                if (arity > 0)
                {
                    sb.Append('<');
                    sb.Append(string.Join(", ", type.GenericParameters.Select(gp => ctx.GetGenericParameterIdentifier(gp))));
                    sb.Append('>');
                }

                sb.AppendLine(";");
            }
            sb.AppendLine("}");

            sb.AppendLine($"export const {baseName}: {baseName}Constructor;");
        }
        sb.AppendLine();
    }

    private static string StripArity(string tsName)
    {
        // Strip _1, _2, etc. suffix
        var underscoreIndex = tsName.LastIndexOf('_');
        if (underscoreIndex > 0 && underscoreIndex < tsName.Length - 1)
        {
            var suffix = tsName.Substring(underscoreIndex + 1);
            if (int.TryParse(suffix, out _))
            {
                return tsName.Substring(0, underscoreIndex);
            }
        }
        return tsName;
    }

    private static string FormatTypeReference(TypeReference typeRef)
    {
        var sb = new StringBuilder();

        if (typeRef.Namespace != null)
        {
            sb.Append(typeRef.Namespace.Replace('.', '$'));
            sb.Append('.');
        }

        sb.Append(typeRef.TypeName);

        if (typeRef.GenericArgs.Count > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", typeRef.GenericArgs.Select(FormatTypeReference)));
            sb.Append('>');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Partitions constraint type references into three buckets:
    /// 1. Type parameters (typeParameterNames) - don't import
    /// 2. Same-namespace types (sameNamespaceTypes) - import from ./internal/index
    /// 3. Cross-namespace types (crossNamespaceTypes) - import type * as Namespace
    /// </summary>
    private static void PartitionConstraintTypes(
        TypeReference typeRef,
        string currentNamespace,
        HashSet<string> typeParameterNames,
        HashSet<string> sameNamespaceTypes,
        Dictionary<string, HashSet<string>> crossNamespaceTypes)
    {
        // Skip type parameters
        if (typeParameterNames.Contains(typeRef.TypeName))
        {
            // Type parameter, don't import
            return;
        }

        // Determine if same-namespace or cross-namespace
        if (typeRef.Namespace == null || typeRef.Namespace == currentNamespace)
        {
            // Same namespace - add to named imports
            sameNamespaceTypes.Add(TsNaming.ForEmit(typeRef));
        }
        else
        {
            // Cross-namespace - add to namespace imports
            if (!crossNamespaceTypes.ContainsKey(typeRef.Namespace))
            {
                crossNamespaceTypes[typeRef.Namespace] = new HashSet<string>();
            }
            // Note: We don't add type names for namespace imports, just track the namespace
        }

        // Recursively partition generic argument types
        foreach (var arg in typeRef.GenericArgs)
        {
            PartitionConstraintTypes(arg, currentNamespace, typeParameterNames,
                sameNamespaceTypes, crossNamespaceTypes);
        }
    }

    /// <summary>
    /// Formats a constraint type reference for use in facade extends clauses.
    /// Uses namespace aliases (Namespace$Subnamespace.TypeName) for cross-namespace types only.
    /// Same-namespace types use unqualified names. Type parameters are never prefixed.
    /// </summary>
    /// <param name="typeRef">The type reference to format</param>
    /// <param name="currentNamespace">Current namespace for relative references</param>
    /// <param name="genericParamMap">Maps CLR generic parameter names (e.g., "T") to facade names (e.g., "T1")</param>
    private static string FormatConstraintReference(
        TypeReference typeRef,
        string currentNamespace,
        IReadOnlyDictionary<string, string>? genericParamMap = null)
    {
        var sb = new StringBuilder();

        // Heuristic: Type parameters are simple names (T, TKey, TSelf, etc.)
        // Never add namespace prefix for these, even if Namespace field is set
        bool isTypeParameter = IsLikelyTypeParameter(typeRef.TypeName);

        // Determine if this type needs a namespace prefix:
        // - Type parameters: never prefix
        // - Same namespace (null or == currentNamespace): never prefix
        // - Cross-namespace: prefix with namespace alias
        bool needsNamespacePrefix = typeRef.Namespace != null
            && typeRef.Namespace != currentNamespace
            && !isTypeParameter;

        if (needsNamespacePrefix)
        {
            sb.Append(typeRef.Namespace.Replace('.', '$'));
            sb.Append('.');
        }

        // Add type name (emit name for actual types, mapped name for type parameters)
        if (!isTypeParameter)
        {
            sb.Append(TsNaming.ForEmit(typeRef));
        }
        else
        {
            // Use mapped name from scope (e.g., T1, TSelf) if available,
            // otherwise fall back to CLR name
            if (genericParamMap != null && genericParamMap.TryGetValue(typeRef.TypeName, out var mappedName))
            {
                sb.Append(mappedName);
            }
            else
            {
                sb.Append(typeRef.TypeName); // Fallback to CLR name
            }
        }

        if (typeRef.GenericArgs.Count > 0)
        {
            sb.Append('<');
            sb.Append(string.Join(", ", typeRef.GenericArgs.Select(arg =>
                FormatConstraintReference(arg, currentNamespace, genericParamMap))));
            sb.Append('>');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Checks if a type is constructible (has a value component that can be used with typeof).
    /// Returns false for:
    /// - Interfaces (type-only)
    /// - Delegates (type-only)
    /// - Enums (branded type alias)
    /// - Generic structs with static members (emitted as type aliases to $instance interfaces)
    /// Returns true for:
    /// - Classes (always have constructor or static interface)
    /// - Non-generic structs (always get export const even with static members)
    /// - Structs without static members (emitted as classes)
    /// </summary>
    private static bool IsConstructible(TypeModel type)
    {
        // Classes are always constructible
        if (type.Kind == TypeKind.Class)
            return true;

        // Only structs remain - check if they get value export
        if (type.Kind == TypeKind.Struct)
        {
            // Generic structs with static members use instance/static split
            // and don't get 'export const' (only non-generic types get it - see EmitStructWithSplit line 1074)
            // So they're type-only from facade perspective
            if (type.GenericParameters.Count > 0 && HasStaticMembers(type))
                return false;

            // All other structs are constructible:
            // - Non-generic structs (get 'export const' even with static split)
            // - Structs without static members (emitted as classes)
            return true;
        }

        // Interfaces, delegates, enums are type-only
        return false;
    }

    private static bool HasStaticMembers(TypeModel type)
    {
        return type.Members.Methods.Any(m => m.IsStatic)
            || type.Members.Properties.Any(p => p.IsStatic)
            || type.Members.Fields.Any(f => f.IsStatic)
            || type.Members.Events.Any(e => e.IsStatic);
    }

    /// <summary>
    /// Heuristic to detect if a type name is likely a type parameter.
    /// Type parameters are usually: T, TKey, TValue, TSelf, etc.
    /// </summary>
    private static bool IsLikelyTypeParameter(string typeName)
    {
        // Single uppercase letter: T, K, V, etc.
        if (typeName.Length == 1 && char.IsUpper(typeName[0]))
        {
            return true;
        }

        // Starts with T followed by uppercase: TKey, TValue, TSelf, etc.
        if (typeName.Length > 1 && typeName[0] == 'T' && char.IsUpper(typeName[1]))
        {
            return true;
        }

        return false;
    }
}
