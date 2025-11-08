using System.Reflection;
using System.Runtime.Loader;

namespace tsbindgen.SinglePhase.Load;

/// <summary>
/// Creates MetadataLoadContext for loading assemblies in isolation.
/// Handles reference pack resolution for .NET BCL assemblies.
/// </summary>
public sealed class AssemblyLoader
{
    private readonly BuildContext _ctx;

    public AssemblyLoader(BuildContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>
    /// Create a MetadataLoadContext for the given assemblies.
    /// </summary>
    public MetadataLoadContext CreateLoadContext(IReadOnlyList<string> assemblyPaths)
    {
        _ctx.Log("Creating MetadataLoadContext...");

        // Find reference assemblies directory
        var referenceAssembliesPath = FindReferenceAssembliesPath();
        _ctx.Log($"Reference assemblies: {referenceAssembliesPath}");

        // Create resolver that looks in:
        // 1. The directory containing the target assemblies
        // 2. The reference assemblies directory
        var resolver = new PathAssemblyResolver(
            GetResolverPaths(assemblyPaths, referenceAssembliesPath));

        // Create load context with System.Private.CoreLib as core assembly
        var loadContext = new MetadataLoadContext(resolver);

        _ctx.Log($"MetadataLoadContext created with {resolver.GetType().Name}");

        return loadContext;
    }

    /// <summary>
    /// Load all assemblies into the context.
    /// </summary>
    public IReadOnlyList<Assembly> LoadAssemblies(
        MetadataLoadContext loadContext,
        IReadOnlyList<string> assemblyPaths)
    {
        var assemblies = new List<Assembly>();

        foreach (var path in assemblyPaths)
        {
            try
            {
                var assembly = loadContext.LoadFromAssemblyPath(path);
                assemblies.Add(assembly);
                _ctx.Log($"Loaded: {assembly.GetName().Name}");
            }
            catch (Exception ex)
            {
                _ctx.Diagnostics.Error(
                    Core.Diagnostics.DiagnosticCodes.UnresolvedType,
                    $"Failed to load assembly {path}: {ex.Message}");
            }
        }

        return assemblies;
    }

    /// <summary>
    /// Find the .NET reference assemblies directory.
    /// Tries common locations for different .NET versions.
    /// </summary>
    private string FindReferenceAssembliesPath()
    {
        // Try to find the shared framework directory
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            dotnetRoot = "/usr/local/share/dotnet";
            if (!Directory.Exists(dotnetRoot))
                dotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        }

        // Look for shared/Microsoft.NETCore.App
        var sharedPath = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");
        if (Directory.Exists(sharedPath))
        {
            // Find the latest version
            var versions = Directory.GetDirectories(sharedPath)
                .Select(Path.GetFileName)
                .Where(v => v != null)
                .OrderByDescending(v => v)
                .ToList();

            if (versions.Any())
            {
                var latestVersion = versions.First()!;
                var refPath = Path.Combine(sharedPath, latestVersion);
                if (Directory.Exists(refPath))
                    return refPath;
            }
        }

        // Fallback: use runtime directory
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null && Directory.Exists(runtimeDir))
            return runtimeDir;

        throw new InvalidOperationException(
            "Could not find .NET reference assemblies. " +
            "Please ensure .NET SDK is installed.");
    }

    /// <summary>
    /// Get all paths that the resolver should search.
    /// </summary>
    private IEnumerable<string> GetResolverPaths(
        IReadOnlyList<string> assemblyPaths,
        string referenceAssembliesPath)
    {
        var paths = new HashSet<string>();

        // Add reference assemblies directory
        if (Directory.Exists(referenceAssembliesPath))
        {
            foreach (var dll in Directory.GetFiles(referenceAssembliesPath, "*.dll"))
            {
                paths.Add(dll);
            }
        }

        // Add directories containing target assemblies
        foreach (var assemblyPath in assemblyPaths)
        {
            var dir = Path.GetDirectoryName(assemblyPath);
            if (dir != null && Directory.Exists(dir))
            {
                foreach (var dll in Directory.GetFiles(dir, "*.dll"))
                {
                    paths.Add(dll);
                }
            }
        }

        return paths;
    }
}
