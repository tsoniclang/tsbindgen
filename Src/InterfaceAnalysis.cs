using System.Reflection;

namespace GenerateDts;

public static class InterfaceAnalysis
{
    public static HashSet<Type> GetAllTransitiveInterfaces(Type interfaceType)
    {
        var result = new HashSet<Type>();
        var queue = new Queue<Type>();

        foreach (var parent in interfaceType.GetInterfaces())
        {
            queue.Enqueue(parent);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (result.Add(current))
            {
                foreach (var parent in current.GetInterfaces())
                {
                    queue.Enqueue(parent);
                }
            }
        }

        return result;
    }

    public static bool HasDiamondInheritance(Type interfaceType, out List<Type> diamondAncestors)
    {
        diamondAncestors = new List<Type>();

        var directParents = interfaceType.GetInterfaces()
            .Where(i => i.FullName != "System.IDisposable")
            .ToList();

        if (directParents.Count <= 1)
            return false;

        var ancestorReachability = new Dictionary<string, HashSet<Type>>();

        foreach (var directParent in directParents)
        {
            var key = directParent.FullName ?? directParent.Name;
            if (!ancestorReachability.ContainsKey(key))
                ancestorReachability[key] = new HashSet<Type>();
            ancestorReachability[key].Add(directParent);

            var transitiveParents = GetAllTransitiveInterfaces(directParent);

            foreach (var transitive in transitiveParents)
            {
                var transitiveKey = transitive.FullName ?? transitive.Name;
                if (!ancestorReachability.ContainsKey(transitiveKey))
                    ancestorReachability[transitiveKey] = new HashSet<Type>();
                ancestorReachability[transitiveKey].Add(directParent);
            }
        }

        var diamondKeys = ancestorReachability
            .Where(kvp => kvp.Value.Count > 1)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in diamondKeys)
        {
            var ancestor = directParents.FirstOrDefault(p => (p.FullName ?? p.Name) == key);
            if (ancestor == null)
            {
                foreach (var parent in directParents)
                {
                    var transitives = parent.GetInterfaces();
                    ancestor = transitives.FirstOrDefault(t => (t.FullName ?? t.Name) == key);
                    if (ancestor != null)
                        break;
                }
            }

            if (ancestor != null)
                diamondAncestors.Add(ancestor);
        }

        return diamondAncestors.Count > 0;
    }

    public static InterfaceDeclaration GenerateBaseInterface(
        Type type,
        Func<Type, string> getTypeName,
        Func<MemberInfo, bool> shouldIncludeMember,
        Func<System.Reflection.PropertyInfo, TypeInfo.PropertyInfo?> processProperty,
        Func<System.Reflection.MethodInfo, Type, TypeInfo.MethodInfo?> processMethod)
    {
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

        var genericParams = type.IsGenericType
            ? type.GetGenericArguments().Select(t => t.Name).ToList()
            : new List<string>();

        return new InterfaceDeclaration(
            getTypeName(type) + "_Base",
            type.FullName + "_Base",
            type.IsGenericType,
            genericParams,
            Extends: new List<string>(),
            properties,
            methods,
            IsDiamondBase: true);
    }

    public static IntersectionTypeAlias CreateIntersectionAlias(
        Type type,
        List<Type> parents,
        Func<Type, string> getTypeName,
        Func<Type, string> mapType)
    {
        var genericParamString = type.IsGenericType
            ? $"<{string.Join(", ", type.GetGenericArguments().Select(t => t.Name))}>"
            : "";

        var intersectedTypes = new List<string>();

        intersectedTypes.Add(getTypeName(type) + "_Base" + genericParamString);

        foreach (var parent in parents)
        {
            var mapped = mapType(parent);
            intersectedTypes.Add(mapped);
        }

        return new IntersectionTypeAlias(
            getTypeName(type),
            type.FullName!,
            type.IsGenericType,
            type.IsGenericType ? type.GetGenericArguments().Select(t => t.Name).ToList() : new List<string>(),
            intersectedTypes);
    }

    public static List<Type> PruneRedundantInterfaceExtends(List<Type> directInterfaces)
    {
        if (directInterfaces.Count <= 1)
            return directInterfaces;

        var result = new List<Type>();

        foreach (var candidate in directInterfaces)
        {
            bool isRedundant = false;

            foreach (var other in directInterfaces)
            {
                if (candidate == other)
                    continue;

                var transitiveParents = GetAllTransitiveInterfaces(other);

                if (transitiveParents.Contains(candidate))
                {
                    isRedundant = true;
                    break;
                }
            }

            if (!isRedundant)
            {
                result.Add(candidate);
            }
        }

        return result;
    }
}
