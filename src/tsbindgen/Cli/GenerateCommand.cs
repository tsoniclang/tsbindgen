using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using tsbindgen.Config;
using tsbindgen.Reflection;
using tsbindgen.Snapshot;

namespace tsbindgen.Cli;

/// <summary>
/// CLI command for the two-phase pipeline: generate snapshots + views.
/// </summary>
public static class GenerateCommand
{
    public static Command Create()
    {
        var command = new Command("generate", "Generate TypeScript declarations from .NET assemblies (two-phase pipeline)");

        // Assembly input options
        var assemblyOption = new Option<string[]>(
            aliases: new[] { "--assembly", "-a" },
            description: "Path to a .NET assembly (.dll) to process (repeatable)")
        {
            AllowMultipleArgumentsPerToken = false,
            Arity = ArgumentArity.ZeroOrMore
        };

        var assemblyDirOption = new Option<string?>(
            aliases: new[] { "--assembly-dir", "-d" },
            description: "Directory containing assemblies to process");

        // Output option
        var outDirOption = new Option<string>(
            aliases: new[] { "--out-dir", "-o" },
            getDefaultValue: () => "out",
            description: "Output directory (default: out/)");

        // Filter options
        var namespacesOption = new Option<string[]>(
            aliases: new[] { "--namespaces", "-n" },
            description: "Comma-separated list of namespaces to include")
        {
            AllowMultipleArgumentsPerToken = true
        };

        // Naming transform options
        var namespaceNamesOption = new Option<string?>(
            name: "--namespace-names",
            description: "Transform namespace names (camelCase)");

        var classNamesOption = new Option<string?>(
            name: "--class-names",
            description: "Transform class names (camelCase)");

        var interfaceNamesOption = new Option<string?>(
            name: "--interface-names",
            description: "Transform interface names (camelCase)");

        var methodNamesOption = new Option<string?>(
            name: "--method-names",
            description: "Transform method names (camelCase)");

        var propertyNamesOption = new Option<string?>(
            name: "--property-names",
            description: "Transform property names (camelCase)");

        var enumMemberNamesOption = new Option<string?>(
            name: "--enum-member-names",
            description: "Transform enum member names (camelCase)");

        var verboseOption = new Option<bool>(
            aliases: new[] { "--verbose", "-v" },
            getDefaultValue: () => false,
            description: "Show detailed generation progress");

        var debugSnapshotOption = new Option<bool>(
            name: "--debug-snapshot",
            getDefaultValue: () => false,
            description: "Write snapshots to disk for debugging");

        var debugTypeListOption = new Option<bool>(
            name: "--debug-typelist",
            getDefaultValue: () => false,
            description: "Write TypeScript type lists for debugging/comparison");

        var useNewPipelineOption = new Option<bool>(
            name: "--use-new-pipeline",
            getDefaultValue: () => false,
            description: "Use Single-Phase Architecture pipeline (experimental)");

        command.AddOption(assemblyOption);
        command.AddOption(assemblyDirOption);
        command.AddOption(outDirOption);
        command.AddOption(namespacesOption);
        command.AddOption(namespaceNamesOption);
        command.AddOption(classNamesOption);
        command.AddOption(interfaceNamesOption);
        command.AddOption(methodNamesOption);
        command.AddOption(propertyNamesOption);
        command.AddOption(enumMemberNamesOption);
        command.AddOption(verboseOption);
        command.AddOption(debugSnapshotOption);
        command.AddOption(debugTypeListOption);
        command.AddOption(useNewPipelineOption);

        command.SetHandler(async (context) =>
        {
            var assemblies = context.ParseResult.GetValueForOption(assemblyOption) ?? Array.Empty<string>();
            var assemblyDir = context.ParseResult.GetValueForOption(assemblyDirOption);
            var outDir = context.ParseResult.GetValueForOption(outDirOption) ?? "out";
            var namespaces = context.ParseResult.GetValueForOption(namespacesOption) ?? Array.Empty<string>();
            var namespaceNames = context.ParseResult.GetValueForOption(namespaceNamesOption);
            var classNames = context.ParseResult.GetValueForOption(classNamesOption);
            var interfaceNames = context.ParseResult.GetValueForOption(interfaceNamesOption);
            var methodNames = context.ParseResult.GetValueForOption(methodNamesOption);
            var propertyNames = context.ParseResult.GetValueForOption(propertyNamesOption);
            var enumMemberNames = context.ParseResult.GetValueForOption(enumMemberNamesOption);
            var verbose = context.ParseResult.GetValueForOption(verboseOption);
            var debugSnapshot = context.ParseResult.GetValueForOption(debugSnapshotOption);
            var debugTypeList = context.ParseResult.GetValueForOption(debugTypeListOption);
            var useNewPipeline = context.ParseResult.GetValueForOption(useNewPipelineOption);

            await ExecuteAsync(
                assemblies,
                assemblyDir,
                outDir,
                namespaces,
                namespaceNames,
                classNames,
                interfaceNames,
                methodNames,
                propertyNames,
                enumMemberNames,
                verbose,
                debugSnapshot,
                debugTypeList,
                useNewPipeline);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string[] assemblyPaths,
        string? assemblyDir,
        string outDir,
        string[] namespaceFilter,
        string? namespaceNames,
        string? classNames,
        string? interfaceNames,
        string? methodNames,
        string? propertyNames,
        string? enumMemberNames,
        bool verbose,
        bool debugSnapshot,
        bool debugTypeList,
        bool useNewPipeline)
    {
        try
        {
            // Collect all assemblies to process
            var allAssemblies = new List<string>(assemblyPaths);

            if (assemblyDir != null)
            {
                if (!Directory.Exists(assemblyDir))
                {
                    Console.Error.WriteLine($"Error: Assembly directory not found: {assemblyDir}");
                    Environment.Exit(3);
                }

                var dllFiles = Directory.GetFiles(assemblyDir, "*.dll", SearchOption.TopDirectoryOnly);
                allAssemblies.AddRange(dllFiles);
            }

            if (allAssemblies.Count == 0)
            {
                Console.Error.WriteLine("Error: No assemblies specified. Use --assembly or --assembly-dir");
                Environment.Exit(2);
            }

            // Route to appropriate pipeline
            if (useNewPipeline)
            {
                await ExecuteNewPipelineAsync(
                    allAssemblies,
                    outDir,
                    namespaceFilter,
                    namespaceNames,
                    classNames,
                    interfaceNames,
                    methodNames,
                    propertyNames,
                    enumMemberNames,
                    verbose);
                return;
            }

            // Create configuration
            var config = new GeneratorConfig
            {
                NamespaceNames = ParseNameTransformOption(namespaceNames),
                ClassNames = ParseNameTransformOption(classNames),
                InterfaceNames = ParseNameTransformOption(interfaceNames),
                MethodNames = ParseNameTransformOption(methodNames),
                PropertyNames = ParseNameTransformOption(propertyNames),
                EnumMemberNames = ParseNameTransformOption(enumMemberNames)
            };

            // Phase 1: Generate snapshots
            Console.WriteLine($"Phase 1: Generating snapshots for {allAssemblies.Count} assemblies...");
            var snapshots = new List<AssemblySnapshot>();

            string? assembliesDir = null;
            if (debugSnapshot)
            {
                assembliesDir = Path.Combine(outDir, "assemblies");
                Directory.CreateDirectory(assembliesDir);
            }

            foreach (var assemblyPath in allAssemblies)
            {
                if (verbose)
                {
                    Console.WriteLine($"  Processing: {Path.GetFileName(assemblyPath)}");
                }

                try
                {
                    var snapshot = await GenerateSnapshotAsync(
                        assemblyPath,
                        config,
                        namespaceFilter,
                        verbose);

                    snapshots.Add(snapshot);

                    // Write snapshot to disk (debug only)
                    if (debugSnapshot && assembliesDir != null)
                    {
                        var snapshotFileName = $"{snapshot.AssemblyName}.snapshot.json";
                        var snapshotPath = Path.Combine(assembliesDir, snapshotFileName);
                        await SnapshotIO.WriteAssemblySnapshot(snapshot, snapshotPath);

                        if (verbose)
                        {
                            Console.WriteLine($"    → {snapshotFileName}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Error processing {Path.GetFileName(assemblyPath)}: {ex.Message}");
                    if (verbose)
                    {
                        Console.Error.WriteLine($"    {ex.StackTrace}");
                    }
                }
            }

            // Write assemblies manifest (debug only)
            if (debugSnapshot && assembliesDir != null)
            {
                var manifestEntries = snapshots.Select(s => new AssemblyManifestEntry(
                    s.AssemblyName,
                    $"{s.AssemblyName}.snapshot.json",
                    s.Namespaces.Sum(ns => ns.Types.Count),
                    s.Namespaces.Count)).ToList();

                var manifest = new AssemblyManifest(manifestEntries);
                var manifestPath = Path.Combine(assembliesDir, "assemblies-manifest.json");
                var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                await File.WriteAllTextAsync(manifestPath, manifestJson);
            }

            Console.WriteLine($"  Generated {snapshots.Count} snapshots");
            Console.WriteLine($"  Total types: {snapshots.Sum(s => s.Namespaces.Sum(ns => ns.Types.Count))}");
            Console.WriteLine();

            // Build GlobalInterfaceIndex for cross-assembly interface resolution
            Console.WriteLine("Building global interface index...");
            var globalInterfaceIndex = GlobalInterfaceIndex.Build(allAssemblies);
            Console.WriteLine($"  Indexed {globalInterfaceIndex.Count} public interfaces");

            // Phase 2: Aggregate by namespace
            Console.WriteLine("Phase 2: Aggregating by namespace...");
            var bundles = Aggregate.ByNamespace(snapshots);

            // Write namespace snapshots to disk (debug only)
            if (debugSnapshot)
            {
                var namespacesDir = Path.Combine(outDir, "namespaces");
                Directory.CreateDirectory(namespacesDir);

                foreach (var (nsName, bundle) in bundles)
                {
                    var nsFileName = $"{nsName}.snapshot.json";
                    var nsPath = Path.Combine(namespacesDir, nsFileName);

                    // Convert NamespaceBundle to NamespaceSnapshot for persistence
                    var nsSnapshot = new NamespaceSnapshot(
                        bundle.ClrName,
                        bundle.Types,
                        bundle.Imports.SelectMany(kvp => kvp.Value.Select(ns => new DependencyRef(ns, kvp.Key))).ToList(),
                        bundle.Diagnostics);

                    await SnapshotIO.WriteNamespaceSnapshot(nsSnapshot, nsPath);

                    if (verbose)
                    {
                        Console.WriteLine($"    → {nsFileName} ({bundle.Types.Count} types from {bundle.SourceAssemblies.Count} assemblies)");
                    }
                }
            }

            Console.WriteLine($"  Aggregated into {bundles.Count} namespace snapshots");
            Console.WriteLine();

            // Render TypeScript declarations
            Render.Pipeline.NamespacePipeline.Run(outDir, bundles, config, globalInterfaceIndex, verbose, debugTypeList);

            // TODO: Old view code moved to _old/Views
            /*
            // Phase 2: Generate views
            Console.WriteLine("Phase 2: Generating views...");

            // Aggregate by namespace
            var aggregator = new NamespaceAggregator(snapshots);
            var bundles = aggregator.AggregateByNamespace();

            Console.WriteLine($"  Aggregated into {bundles.Count} namespaces");

            // Render namespace bundles
            var bundleRenderer = new NamespaceBundleRenderer(outDir, config);
            await bundleRenderer.RenderAllAsync(bundles);
            Console.WriteLine($"  Generated namespace bundles → namespaces/");

            // Render ambient declarations
            var ambientRenderer = new AmbientRenderer(outDir, config);
            await ambientRenderer.RenderAsync(bundles);
            Console.WriteLine($"  Generated ambient declarations → ambient/global.d.ts");

            // Render module entry points
            var moduleRenderer = new ModuleRenderer(outDir);
            await moduleRenderer.RenderAllAsync(bundles);
            Console.WriteLine($"  Generated module entry points → modules/");
            */

            Console.WriteLine();
            Console.WriteLine("✓ Snapshot generation complete");
            Console.WriteLine($"  Output directory: {Path.GetFullPath(outDir)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (verbose)
            {
                Console.Error.WriteLine($"Stack trace:\n{ex.StackTrace}");
            }
            Environment.Exit(1);
        }
    }

    private static async Task<AssemblySnapshot> GenerateSnapshotAsync(
        string assemblyPath,
        GeneratorConfig config,
        string[] namespaceFilter,
        bool verbose)
    {
        // Validate assembly path
        if (!File.Exists(assemblyPath))
        {
            throw new FileNotFoundException($"Assembly file not found: {assemblyPath}");
        }

        var assemblyFileName = Path.GetFileName(assemblyPath);
        var isCoreLib = assemblyFileName.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase);

        Assembly assembly;
        MetadataContext? loader = null;

        try
        {
            if (isCoreLib)
            {
                // Use MetadataLoadContext for core library
                loader = new MetadataContext(assemblyPath);
                assembly = loader.LoadFromAssemblyPath(assemblyPath);
            }
            else
            {
                // Use standard reflection
                assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
            }

            // Reflect over assembly to create pure CLR snapshot
            return Reflect.Assembly(assembly, assemblyPath, config, namespaceFilter, verbose);
        }
        finally
        {
            loader?.Dispose();
        }
    }

    private static NameTransformOption ParseNameTransformOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return NameTransformOption.None;
        }

        return value.ToLowerInvariant() switch
        {
            "camelcase" => NameTransformOption.CamelCase,
            "camel-case" => NameTransformOption.CamelCase,
            "camel" => NameTransformOption.CamelCase,
            _ => throw new ArgumentException($"Unknown naming transform: '{value}'. Supported values: camelCase")
        };
    }

    /// <summary>
    /// Execute using Single-Phase Architecture pipeline (experimental).
    /// </summary>
    private static async Task ExecuteNewPipelineAsync(
        List<string> allAssemblies,
        string outDir,
        string[] namespaceFilter,
        string? namespaceNames,
        string? classNames,
        string? interfaceNames,
        string? methodNames,
        string? propertyNames,
        string? enumMemberNames,
        bool verbose)
    {
        Console.WriteLine("=== Using Single-Phase Architecture Pipeline (Experimental) ===");
        Console.WriteLine();

        // Build policy from CLI options
        var policy = Core.Policy.PolicyDefaults.Create();

        // Apply name transforms to policy if specified
        if (!string.IsNullOrWhiteSpace(namespaceNames) ||
            !string.IsNullOrWhiteSpace(classNames) ||
            !string.IsNullOrWhiteSpace(interfaceNames) ||
            !string.IsNullOrWhiteSpace(methodNames) ||
            !string.IsNullOrWhiteSpace(propertyNames))
        {
            // Parse name transform option
            var transform = ParseNameTransformOption(namespaceNames ?? classNames ?? interfaceNames ?? methodNames ?? propertyNames);

            // Update emission policy
            policy = policy with
            {
                Emission = policy.Emission with
                {
                    NameTransform = transform == NameTransformOption.CamelCase
                        ? Core.Policy.NameTransformStrategy.CamelCase
                        : Core.Policy.NameTransformStrategy.None
                }
            };
        }

        // Create logger that respects verbose flag
        Action<string>? logger = verbose ? Console.WriteLine : null;

        // Run single-phase pipeline
        var result = SinglePhase.SinglePhaseBuilder.Build(
            allAssemblies,
            outDir,
            policy,
            logger);

        // Report results
        Console.WriteLine();
        if (result.Success)
        {
            Console.WriteLine("✓ Single-phase generation complete");
            Console.WriteLine($"  Output directory: {Path.GetFullPath(outDir)}");
            Console.WriteLine($"  Namespaces: {result.Statistics.NamespaceCount}");
            Console.WriteLine($"  Types: {result.Statistics.TypeCount}");
            Console.WriteLine($"  Members: {result.Statistics.TotalMembers}");
        }
        else
        {
            Console.Error.WriteLine("✗ Single-phase generation failed");
            Console.Error.WriteLine($"  Errors: {result.Diagnostics.Count(d => d.Severity == Core.Diagnostics.DiagnosticSeverity.Error)}");

            foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == Core.Diagnostics.DiagnosticSeverity.Error))
            {
                Console.Error.WriteLine($"    {diagnostic.Code}: {diagnostic.Message}");
            }

            Environment.Exit(1);
        }

        await Task.CompletedTask;
    }
}
