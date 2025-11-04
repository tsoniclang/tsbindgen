using System.Reflection;
using System.Runtime.CompilerServices;

namespace GenerateDts.Reflection;

/// <summary>
/// Resolves type-forwarding assemblies to their implementation assemblies.
/// </summary>
public class TypeForwardingResolver
{
    /// <summary>
    /// Known mappings of type-forwarding assemblies to their implementation assemblies.
    /// This is used as a fallback when reflection-based detection fails.
    /// </summary>
    private static readonly Dictionary<string, string[]> KnownForwardingMappings = new()
    {
        // Core runtime - most forward to System.Private.CoreLib
        ["System.Runtime"] = new[] { "System.Private.CoreLib" },
        ["System.Runtime.Extensions"] = new[] { "System.Private.CoreLib" },
        ["System.IO"] = new[] { "System.Private.CoreLib" },
        ["System.IO.FileSystem"] = new[] { "System.Private.CoreLib" },
        ["System.Reflection"] = new[] { "System.Private.CoreLib" },
        ["System.Text.Encoding"] = new[] { "System.Private.CoreLib" },
        ["System.Threading.Tasks"] = new[] { "System.Private.CoreLib" },

        // Networking
        ["System.Net"] = new[] { "System.Net.Primitives" },

        // Data
        ["System.Data"] = new[] { "System.Data.Common" },

        // Numerics
        ["System.Numerics"] = new[] { "System.Private.CoreLib" },
        ["System.Numerics.Vectors"] = new[] { "System.Private.CoreLib", "System.Numerics.Vectors" },

        // XML assemblies all forward to System.Private.Xml
        ["System.Xml"] = new[] { "System.Private.Xml" },
        ["System.Xml.ReaderWriter"] = new[] { "System.Private.Xml" },
        ["System.Xml.XDocument"] = new[] { "System.Private.Xml.Linq" },
        ["System.Xml.XmlDocument"] = new[] { "System.Private.Xml" },
        ["System.Xml.XmlSerializer"] = new[] { "System.Private.Xml" },
        ["System.Xml.XPath"] = new[] { "System.Private.Xml" },
        ["System.Xml.XPath.XDocument"] = new[] { "System.Private.Xml.Linq" },
        ["System.Xml.Linq"] = new[] { "System.Private.Xml.Linq" },
        ["System.Xml.Serialization"] = new[] { "System.Private.Xml" },

        // Security
        ["System.Security.Cryptography"] = new[] { "System.Security.Cryptography" },
        ["System.Security.Principal"] = new[] { "System.Private.CoreLib" },

        // Drawing
        ["System.Drawing"] = new[] { "System.Drawing.Common" },

        // Transactions
        ["System.Transactions"] = new[] { "System.Transactions.Local" },
    };

    /// <summary>
    /// Core assemblies that are typically generated separately and should not be duplicated
    /// by generating their forwarders.
    /// </summary>
    private static readonly HashSet<string> CoreAssemblies = new()
    {
        "System.Private.CoreLib",
        "System.Private.Xml",
        "System.Private.Xml.Linq",
        "System.Private.Uri",
        "System.Data.Common",
        "System.Drawing.Common",
        "System.Net.Primitives",
    };

    /// <summary>
    /// Determines if a type-forwarding assembly should be skipped because its target
    /// is a core assembly that will be generated separately.
    /// </summary>
    public static bool ShouldSkipForwarder(string targetAssemblyName)
    {
        return CoreAssemblies.Contains(targetAssemblyName);
    }

    /// <summary>
    /// Checks if an assembly is primarily a type-forwarding assembly (no or very few types).
    /// </summary>
    public static bool IsTypeForwardingAssembly(Assembly assembly)
    {
        try
        {
            // Get exportable types (types we can access)
            var types = assembly.GetExportedTypes();

            // If very few types (less than 10), likely a type-forwarding assembly
            // Real assemblies have dozens or hundreds of types
            return types.Length < 10;
        }
        catch
        {
            // If we can't get types, assume it's problematic
            return true;
        }
    }

    /// <summary>
    /// Gets all type-forwarding target assemblies from a forwarding assembly.
    /// </summary>
    public static List<string> GetForwardedAssemblies(Assembly assembly)
    {
        var forwardedAssemblies = new HashSet<string>();
        var assemblyName = assembly.GetName().Name;

        // First, check if this is a known forwarding assembly
        if (assemblyName != null && KnownForwardingMappings.TryGetValue(assemblyName, out var knownTargets))
        {
            Console.WriteLine($"  Using known forwarding mapping for {assemblyName}");
            foreach (var target in knownTargets)
            {
                forwardedAssemblies.Add(target);
            }
            return forwardedAssemblies.ToList();
        }

        // If not in known mappings, try reflection-based detection
        try
        {
            // TypeForwardedToAttribute is stored as exported types in the manifest
            // We need to check the module's metadata for forwarded types
            var module = assembly.ManifestModule;

            // Get all types from the module (including forwarded types)
            var allTypes = module.GetTypes();

            // For each type, check if it's actually defined in a different assembly
            foreach (var type in allTypes)
            {
                try
                {
                    // If the type's assembly is different from our assembly, it's forwarded
                    var typeAssembly = type.Assembly;
                    if (typeAssembly != assembly)
                    {
                        var targetName = typeAssembly.GetName().Name;
                        if (targetName != null && targetName != assembly.GetName().Name)
                        {
                            forwardedAssemblies.Add(targetName);
                        }
                    }
                }
                catch
                {
                    // Skip types we can't inspect
                    continue;
                }
            }

            // Fallback: Try GetCustomAttributesData approach for assemblies that support it
            if (forwardedAssemblies.Count == 0)
            {
                var forwardedAttributes = assembly.GetCustomAttributesData()
                    .Where(attr => attr.AttributeType.Name == "TypeForwardedToAttribute");

                foreach (var attr in forwardedAttributes)
                {
                    if (attr.ConstructorArguments.Count > 0)
                    {
                        var typeArg = attr.ConstructorArguments[0];
                        if (typeArg.Value is Type forwardedType)
                        {
                            var targetAssembly = forwardedType.Assembly.GetName().Name;
                            if (targetAssembly != null)
                            {
                                forwardedAssemblies.Add(targetAssembly);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Warning: Could not read type-forwarding information: {ex.Message}");
        }

        return forwardedAssemblies.ToList();
    }

    /// <summary>
    /// Attempts to find and load a target assembly from common .NET locations.
    /// </summary>
    public static Assembly? TryLoadTargetAssembly(string assemblyName, string originalAssemblyPath)
    {
        // Strategy 1: Try the same directory as the original assembly
        var originalDir = Path.GetDirectoryName(originalAssemblyPath);
        if (originalDir != null)
        {
            var sameDirPath = Path.Combine(originalDir, $"{assemblyName}.dll");
            if (File.Exists(sameDirPath))
            {
                try
                {
                    Console.WriteLine($"  Found forwarding target: {sameDirPath}");
                    return Assembly.LoadFrom(sameDirPath);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  Warning: Could not load {sameDirPath}: {ex.Message}");
                }
            }
        }

        // Strategy 2: Check common .NET runtime locations
        var dotnetHome = Environment.GetEnvironmentVariable("DOTNET_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "dotnet");

        // Try shared runtime directory (where System.Private.* assemblies live)
        var sharedRuntimePaths = new[]
        {
            Path.Combine(dotnetHome, "shared", "Microsoft.NETCore.App"),
        };

        foreach (var basePath in sharedRuntimePaths)
        {
            if (!Directory.Exists(basePath)) continue;

            // Find version directories (look for highest version)
            var versionDirs = Directory.GetDirectories(basePath)
                .OrderByDescending(d => d)
                .ToList();

            foreach (var versionDir in versionDirs)
            {
                var targetPath = Path.Combine(versionDir, $"{assemblyName}.dll");
                if (File.Exists(targetPath))
                {
                    try
                    {
                        Console.WriteLine($"  Found forwarding target: {targetPath}");
                        return Assembly.LoadFrom(targetPath);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  Warning: Could not load {targetPath}: {ex.Message}");
                    }
                }
            }
        }

        return null;
    }
}
