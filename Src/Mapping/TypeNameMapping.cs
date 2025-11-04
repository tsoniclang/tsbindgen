using System.Reflection;
using System.Text;
using GenerateDts.Pipeline;

namespace GenerateDts.Mapping;

public static class TypeNameMapping
{
    public static string GetFullTypeName(Type type, Assembly? currentAssembly, DependencyTracker? dependencyTracker)
    {
        if (type.IsGenericParameter)
        {
            return type.Name ?? "T";
        }

        var typeName = GetTypeNameWithArity(type);
        var fullName = type.Namespace != null ? $"{type.Namespace}.{typeName}" : typeName;

        if (string.IsNullOrWhiteSpace(fullName))
        {
            return "any";
        }

        // Rewrite cross-assembly references with aliases
        if (currentAssembly != null && dependencyTracker != null)
        {
            if (type.Assembly != currentAssembly)
            {
                var assemblyName = type.Assembly.GetName().Name;
                if (assemblyName != null)
                {
                    var alias = DependencyTracker.GetModuleAlias(assemblyName);
                    return $"{alias}.{fullName}";
                }
            }
        }

        return fullName;
    }

    public static string GetTypeNameWithArity(Type type)
    {
        var baseName = type.Name;
        var arity = 0;

        if (type.IsGenericType || baseName.Contains('`'))
        {
            var backtickIndex = baseName.IndexOf('`');
            if (backtickIndex > 0)
            {
                if (int.TryParse(baseName.Substring(backtickIndex + 1), out var parsedArity))
                {
                    arity = parsedArity;
                }
                baseName = baseName.Substring(0, backtickIndex);
            }
        }

        if (type.IsNested && type.DeclaringType != null)
        {
            var ancestorChain = new List<(string name, int arity)>();
            var current = type.DeclaringType;

            while (current != null)
            {
                var ancestorName = current.Name;
                var ancestorArity = 0;

                var backtickIndex = ancestorName.IndexOf('`');
                if (backtickIndex > 0)
                {
                    if (int.TryParse(ancestorName.Substring(backtickIndex + 1), out var parsedArity))
                    {
                        ancestorArity = parsedArity;
                    }
                    ancestorName = ancestorName.Substring(0, backtickIndex);
                }

                ancestorChain.Insert(0, (ancestorName, ancestorArity));
                current = current.DeclaringType;
            }

            var nameBuilder = new StringBuilder();
            foreach (var (ancestorName, ancestorArity) in ancestorChain)
            {
                if (nameBuilder.Length > 0)
                {
                    nameBuilder.Append('_');
                }

                nameBuilder.Append(ancestorName);
                if (ancestorArity > 0)
                {
                    nameBuilder.Append('_');
                    nameBuilder.Append(ancestorArity);
                }
            }

            nameBuilder.Append('_');
            nameBuilder.Append(baseName);
            if (arity > 0)
            {
                nameBuilder.Append('_');
                nameBuilder.Append(arity);
            }

            return nameBuilder.ToString();
        }

        if (arity > 0)
        {
            return $"{baseName}_{arity}";
        }

        return baseName;
    }
}
