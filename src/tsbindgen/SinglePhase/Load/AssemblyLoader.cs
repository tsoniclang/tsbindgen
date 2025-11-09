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
        _ctx.Log("AssemblyLoader", "Creating MetadataLoadContext...");

        // Get reference assemblies directory from the assemblies being loaded
        var referenceAssembliesPath = GetReferenceAssembliesPath(assemblyPaths);

        // Create resolver that looks in:
        // 1. The directory containing the target assemblies
        // 2. The reference assemblies directory (same as target for version consistency)
        var resolver = new PathAssemblyResolver(
            GetResolverPaths(assemblyPaths, referenceAssembliesPath));

        // Create load context with System.Private.CoreLib as core assembly
        var loadContext = new MetadataLoadContext(resolver);

        _ctx.Log("AssemblyLoader", $"MetadataLoadContext created with {resolver.GetType().Name}");

        return loadContext;
    }

    /// <summary>
    /// Load all assemblies into the context.
    /// Deduplicates by assembly identity to avoid loading the same assembly twice.
    /// Skips mscorlib as it's automatically loaded by MetadataLoadContext.
    /// </summary>
    public IReadOnlyList<Assembly> LoadAssemblies(
        MetadataLoadContext loadContext,
        IReadOnlyList<string> assemblyPaths)
    {
        var assemblies = new List<Assembly>();
        var loadedIdentities = new HashSet<string>();

        foreach (var path in assemblyPaths)
        {
            try
            {
                // Get assembly name without loading it first
                var assemblyName = AssemblyName.GetAssemblyName(path);
                var identity = $"{assemblyName.Name}, Version={assemblyName.Version}";

                // Skip mscorlib - it's automatically loaded by MetadataLoadContext as core assembly
                if (assemblyName.Name == "mscorlib")
                {
                    _ctx.Log("AssemblyLoader", $"Skipping mscorlib (core assembly, automatically loaded)");
                    continue;
                }

                // Skip if already loaded
                if (loadedIdentities.Contains(identity))
                {
                    _ctx.Log("AssemblyLoader", $"Skipping duplicate: {assemblyName.Name} (already loaded)");
                    continue;
                }

                var assembly = loadContext.LoadFromAssemblyPath(path);
                assemblies.Add(assembly);
                loadedIdentities.Add(identity);
                _ctx.Log("AssemblyLoader", $"Loaded: {assembly.GetName().Name}");
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
    /// Get reference assemblies directory from the first assembly path.
    /// Uses the same directory as the assemblies being loaded to ensure version compatibility.
    /// </summary>
    private string GetReferenceAssembliesPath(IReadOnlyList<string> assemblyPaths)
    {
        // Use the directory containing the first assembly as the reference path
        // This ensures we're using the same .NET version for all type resolution
        if (assemblyPaths.Count > 0)
        {
            var firstAssemblyDir = Path.GetDirectoryName(assemblyPaths[0]);
            if (firstAssemblyDir != null && Directory.Exists(firstAssemblyDir))
            {
                _ctx.Log("AssemblyLoader", $"Using assembly directory as reference path: {firstAssemblyDir}");
                return firstAssemblyDir;
            }
        }

        // Fallback: use runtime directory (should rarely happen)
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (runtimeDir != null && Directory.Exists(runtimeDir))
        {
            _ctx.Log("AssemblyLoader", $"Fallback to runtime directory: {runtimeDir}");
            return runtimeDir;
        }

        throw new InvalidOperationException(
            "Could not determine reference assemblies directory from assembly paths.");
    }

    /// <summary>
    /// Get all paths that the resolver should search.
    /// Deduplicates by assembly name to avoid loading the same assembly twice.
    /// </summary>
    private IEnumerable<string> GetResolverPaths(
        IReadOnlyList<string> assemblyPaths,
        string referenceAssembliesPath)
    {
        var pathsByName = new Dictionary<string, string>();

        // Add reference assemblies directory
        if (Directory.Exists(referenceAssembliesPath))
        {
            foreach (var dll in Directory.GetFiles(referenceAssembliesPath, "*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (!pathsByName.ContainsKey(name))
                {
                    pathsByName[name] = dll;
                }
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
                    var name = Path.GetFileNameWithoutExtension(dll);
                    if (!pathsByName.ContainsKey(name))
                    {
                        pathsByName[name] = dll;
                    }
                }
            }
        }

        return pathsByName.Values;
    }
}
