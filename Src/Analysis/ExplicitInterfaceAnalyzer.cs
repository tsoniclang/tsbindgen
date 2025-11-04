using System.Reflection;

namespace GenerateDts.Analysis;

public static class ExplicitInterfaceAnalyzer
{
    public static List<(Type interfaceType, System.Reflection.MethodInfo interfaceMethod, System.Reflection.MethodInfo implementation)> GetExplicitInterfaceImplementations(Type type)
    {
        var result = new List<(Type, System.Reflection.MethodInfo, System.Reflection.MethodInfo)>();

        try
        {
            var interfaces = type.GetInterfaces();
            foreach (var iface in interfaces)
            {
                try
                {
                    var map = type.GetInterfaceMap(iface);
                    for (int i = 0; i < map.TargetMethods.Length; i++)
                    {
                        var targetMethod = map.TargetMethods[i];
                        var interfaceMethod = map.InterfaceMethods[i];

                        // Explicit implementation = not public
                        // For classes: private + virtual
                        // For structs: private (can't be virtual)
                        // Method name will be like "System.IDisposable.Dispose"
                        if (!targetMethod.IsPublic)
                        {
                            result.Add((iface, interfaceMethod, targetMethod));
                        }
                    }
                }
                catch
                {
                    // GetInterfaceMap can fail for some types in MetadataLoadContext
                    // Skip and continue
                }
            }
        }
        catch
        {
            // Type may not support interface mapping
        }

        return result;
    }

    public static bool HasAnyExplicitImplementation(Type type, Type interfaceType)
    {
        // Check if the given interface has ANY members that are explicitly implemented (not public)
        // This includes both methods and property accessors
        try
        {
            var map = type.GetInterfaceMap(interfaceType);

            // Check all methods
            var allMethodsPublic = map.TargetMethods.All(m => m.IsPublic);
            if (!allMethodsPublic)
                return true;

            // Check all property accessors
            var allAccessorsPublic = interfaceType
                .GetProperties()
                .SelectMany(p => p.GetAccessors(nonPublic: true))
                .All(a => a.IsPublic);

            if (!allAccessorsPublic)
                return true;

            return false; // All members are public
        }
        catch
        {
            // GetInterfaceMap can fail for some types in MetadataLoadContext
            // Be conservative: if we can't check, assume explicit to avoid TypeScript errors
            return true;
        }
    }
}
