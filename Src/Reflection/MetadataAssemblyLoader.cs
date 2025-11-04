using System.Reflection;
using System.Runtime.Loader;

namespace GenerateDts.Reflection;

/// <summary>
/// Loads assemblies using MetadataLoadContext for inspection without execution.
/// This is required for System.Private.CoreLib and other core assemblies.
/// </summary>
public sealed class MetadataAssemblyLoader : IDisposable
{
    private readonly MetadataLoadContext _context;
    private bool _disposed;

    /// <summary>
    /// Creates a MetadataLoadContext for loading assemblies from the .NET reference pack.
    /// </summary>
    /// <param name="assemblyPath">Path to the assembly to load</param>
    /// <param name="referencePackPath">Path to the .NET reference pack (e.g., /dotnet/packs/Microsoft.NETCore.App.Ref/10.0.0/ref/net10.0/)</param>
    public MetadataAssemblyLoader(string assemblyPath, string? referencePackPath = null)
    {
        // For System.Private.CoreLib, use only the runtime directory to avoid conflicts
        // between reference pack and runtime assemblies
        var assemblyFileName = Path.GetFileName(assemblyPath);
        var isCoreLib = assemblyFileName.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase);

        // Gather all assemblies for resolution
        var resolverPaths = new List<string>();

        if (isCoreLib)
        {
            // For System.Private.CoreLib, use ONLY the runtime directory
            var assemblyDir = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrEmpty(assemblyDir) && Directory.Exists(assemblyDir))
            {
                resolverPaths.AddRange(Directory.GetFiles(assemblyDir, "*.dll"));
            }
        }
        else
        {
            // For other assemblies, try reference pack first, then assembly directory
            if (string.IsNullOrEmpty(referencePackPath))
            {
                referencePackPath = FindReferencePackPath(assemblyPath);
            }

            if (Directory.Exists(referencePackPath))
            {
                resolverPaths.AddRange(Directory.GetFiles(referencePackPath, "*.dll"));
            }

            // Also include the target assembly's directory
            var assemblyDir = Path.GetDirectoryName(assemblyPath);
            if (!string.IsNullOrEmpty(assemblyDir) && Directory.Exists(assemblyDir))
            {
                foreach (var dll in Directory.GetFiles(assemblyDir, "*.dll"))
                {
                    if (!resolverPaths.Contains(dll))
                    {
                        resolverPaths.Add(dll);
                    }
                }
            }
        }

        // Create resolver with all discovered assemblies
        var resolver = new PathAssemblyResolver(resolverPaths);
        _context = new MetadataLoadContext(resolver);
    }

    /// <summary>
    /// Loads an assembly from the specified path.
    /// </summary>
    public Assembly LoadFromAssemblyPath(string assemblyPath)
    {
        return _context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
    }

    /// <summary>
    /// Tries to find the .NET reference pack path based on the assembly location.
    /// </summary>
    private static string FindReferencePackPath(string assemblyPath)
    {
        // Try to find the reference pack by looking for common patterns
        var assemblyDir = Path.GetDirectoryName(assemblyPath);

        // Check if we're in a runtime directory - try to find corresponding ref pack
        if (assemblyDir?.Contains("/shared/Microsoft.NETCore.App/") == true)
        {
            // Extract version from path like: /dotnet/shared/Microsoft.NETCore.App/10.0.0-rc.1/
            var parts = assemblyDir.Split(new[] { "/shared/Microsoft.NETCore.App/" }, StringSplitOptions.None);
            if (parts.Length >= 2)
            {
                var dotnetRoot = parts[0];
                var versionAndRest = parts[1];
                var version = versionAndRest.Split('/')[0];

                // Try reference pack
                var refPackPath = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref", version, "ref", $"net{GetNetVersion(version)}");
                if (Directory.Exists(refPackPath))
                {
                    return refPackPath;
                }
            }
        }

        // Check if we're already in a reference pack
        if (assemblyDir?.Contains("/packs/Microsoft.NETCore.App.Ref/") == true)
        {
            return assemblyDir;
        }

        // Fallback: use the assembly directory itself
        return assemblyDir ?? Environment.CurrentDirectory;
    }

    /// <summary>
    /// Extracts the .NET version string (e.g., "10.0") from a full version (e.g., "10.0.0-rc.1.25451.107").
    /// </summary>
    private static string GetNetVersion(string fullVersion)
    {
        var parts = fullVersion.Split('.');
        if (parts.Length >= 2)
        {
            return $"{parts[0]}.{parts[1].TrimStart('0')}";
        }
        return "10.0"; // Default fallback
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _context?.Dispose();
            _disposed = true;
        }
    }
}
