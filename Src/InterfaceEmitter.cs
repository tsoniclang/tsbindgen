using System.Reflection;

namespace GenerateDts;

public static class InterfaceEmitter
{
    public static InterfaceDeclaration ProcessInterface(
        Type type,
        TypeMapper typeMapper,
        Func<MemberInfo, bool> shouldIncludeMember,
        Func<System.Reflection.PropertyInfo, TypeInfo.PropertyInfo?> processProperty,
        Func<System.Reflection.MethodInfo, Type, TypeInfo.MethodInfo?> processMethod,
        Action<Type> trackTypeDependency,
        Dictionary<string, List<IntersectionTypeAlias>> intersectionAliases,
        Func<Type, string> getTypeName)
    {
        // Phase 3: Collect direct interfaces and prune redundant ones
        var directInterfaces = type.GetInterfaces()
            .Where(i => i.FullName != "System.IDisposable")
            .ToList();

        // Prune redundant extends
        var prunedInterfaces = InterfaceAnalysis.PruneRedundantInterfaceExtends(directInterfaces);

        // Phase 1: Check for diamond inheritance AFTER pruning
        if (InterfaceAnalysis.HasDiamondInheritance(type, out var diamondAncestors))
        {
            Console.WriteLine($"[PHASE1] Diamond detected in {type.FullName}");
            Console.WriteLine($"[PHASE1] Conflicting ancestors: {string.Join(", ", diamondAncestors.Select(a => a.FullName))}");

            // Generate _Base interface
            var baseInterface = InterfaceAnalysis.GenerateBaseInterface(
                type,
                getTypeName,
                shouldIncludeMember,
                processProperty,
                processMethod);

            // Generate intersection alias (added to namespace later)
            var alias = InterfaceAnalysis.CreateIntersectionAlias(
                type,
                prunedInterfaces,
                getTypeName,
                typeMapper.MapType);

            // Track dependencies for cross-assembly references
            foreach (var parent in prunedInterfaces)
            {
                trackTypeDependency(parent);
            }

            var ns = type.Namespace ?? "";
            if (!intersectionAliases.ContainsKey(ns))
                intersectionAliases[ns] = new List<IntersectionTypeAlias>();
            intersectionAliases[ns].Add(alias);

            Console.WriteLine($"[PHASE1] Generated {getTypeName(type)}_Base interface + intersection alias");

            return baseInterface;
        }

        // Normal case: no diamond, proceed with standard interface generation
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Cast<MemberInfo>()
            .Where(shouldIncludeMember)
            .Cast<System.Reflection.PropertyInfo>()
            .Select(processProperty)
            .Where(p => p != null)
            .ToList();

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Cast<MemberInfo>()
            .Where(shouldIncludeMember)
            .Cast<System.Reflection.MethodInfo>()
            .Where(m => m.IsSpecialName == false)
            .Where(m => !m.Name.Contains('.'))
            .Select(m => processMethod(m, type))
            .OfType<TypeInfo.MethodInfo>()
            .ToList();

        // Map to TypeScript names
        var extends = prunedInterfaces
            .Select(i => typeMapper.MapType(i))
            .Distinct()
            .ToList();

        // Track base interface dependencies (use pruned list)
        foreach (var iface in prunedInterfaces)
        {
            trackTypeDependency(iface);
        }

        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(t => t.Name).ToList()
            : new List<string>();

        return new InterfaceDeclaration(
            getTypeName(type),
            type.FullName!,
            type.IsGenericType,
            genericParams,
            extends,
            properties,
            methods);
    }
}
