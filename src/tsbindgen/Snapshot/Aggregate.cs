namespace tsbindgen.Snapshot;

/// <summary>
/// Pure functions for aggregating assembly snapshots by namespace.
/// Merges types from multiple assemblies into namespace bundles.
/// </summary>
public static class Aggregate
{

    /// <summary>
    /// Aggregates assembly snapshots by namespace.
    /// Returns a dictionary of namespace name → namespace bundle.
    /// </summary>
    public static Dictionary<string, NamespaceBundle> ByNamespace(IReadOnlyList<AssemblySnapshot> snapshots)
    {
        var bundles = new Dictionary<string, NamespaceBundle>();

        foreach (var snapshot in snapshots)
        {
            foreach (var ns in snapshot.Namespaces)
            {
                if (!bundles.TryGetValue(ns.ClrName, out var bundle))
                {
                    bundle = new NamespaceBundle(
                        ns.ClrName,
                        new List<TypeSnapshot>(),
                        new Dictionary<string, HashSet<string>>(),
                        new List<Diagnostic>(),
                        new HashSet<string>());

                    bundles[ns.ClrName] = bundle;
                }

                // Merge types
                foreach (var type in ns.Types)
                {
                    bundle.Types.Add(type);
                }

                // Merge imports (assembly → namespaces) from Phase 1 snapshots
                // Phase 1 has already extracted all dependencies with proper stripping
                foreach (var import in ns.Imports)
                {
                    if (!bundle.Imports.TryGetValue(import.Assembly, out var namespaces))
                    {
                        namespaces = new HashSet<string>();
                        bundle.Imports[import.Assembly] = namespaces;
                    }
                    namespaces.Add(import.Namespace);
                }

                // Merge diagnostics
                foreach (var diagnostic in ns.Diagnostics)
                {
                    bundle.Diagnostics.Add(diagnostic);
                }

                // Track source assemblies
                bundle.SourceAssemblies.Add(snapshot.AssemblyName);
            }
        }

        return bundles;
    }
}

/// <summary>
/// A namespace bundle aggregated from multiple assemblies.
/// </summary>
public sealed record NamespaceBundle(
    string ClrName,
    List<TypeSnapshot> Types,
    Dictionary<string, HashSet<string>> Imports, // assembly → namespaces
    List<Diagnostic> Diagnostics,
    HashSet<string> SourceAssemblies);
