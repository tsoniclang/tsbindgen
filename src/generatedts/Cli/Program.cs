using System.CommandLine;
using System.Reflection;
using GenerateDts.Config;
using GenerateDts.Reflection;
using GenerateDts.Pipeline;
using GenerateDts.Emit;
using GenerateDts.Metadata;
using GenerateDts.Diagnostics;
using GenerateDts.Analysis;

namespace GenerateDts.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var assemblyPathArg = new Argument<string>(
            name: "assembly-path",
            description: "Path to the .NET assembly (.dll) to process");

        var namespacesOption = new Option<string[]>(
            aliases: new[] { "--namespaces", "-n" },
            description: "Comma-separated list of namespaces to include")
        {
            AllowMultipleArgumentsPerToken = true
        };

        var outDirOption = new Option<string>(
            aliases: new[] { "--out-dir", "-o" },
            getDefaultValue: () => ".",
            description: "Output directory for generated .d.ts file");

        var logOption = new Option<string?>(
            aliases: new[] { "--log", "-l" },
            description: "Path to write JSON log file");

        var configOption = new Option<string?>(
            aliases: new[] { "--config", "-c" },
            description: "Path to configuration JSON file");

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

        var bindingNamesOption = new Option<string?>(
            name: "--binding-names",
            description: "Transform binding names (camelCase, overrides class/method/property flags)");

        var verboseOption = new Option<bool>(
            aliases: new[] { "--verbose", "-v" },
            getDefaultValue: () => false,
            description: "Show warning messages during generation");

        var rootCommand = new RootCommand("Generate TypeScript declarations from .NET assemblies")
        {
            assemblyPathArg,
            namespacesOption,
            outDirOption,
            logOption,
            configOption,
            namespaceNamesOption,
            classNamesOption,
            interfaceNamesOption,
            methodNamesOption,
            propertyNamesOption,
            enumMemberNamesOption,
            bindingNamesOption,
            verboseOption
        };

        rootCommand.SetHandler(async (context) =>
            {
                var assemblyPath = context.ParseResult.GetValueForArgument(assemblyPathArg);
                var namespaces = context.ParseResult.GetValueForOption(namespacesOption) ?? Array.Empty<string>();
                var outDir = context.ParseResult.GetValueForOption(outDirOption) ?? ".";
                var logPath = context.ParseResult.GetValueForOption(logOption);
                var configPath = context.ParseResult.GetValueForOption(configOption);
                var namespaceNames = context.ParseResult.GetValueForOption(namespaceNamesOption);
                var classNames = context.ParseResult.GetValueForOption(classNamesOption);
                var interfaceNames = context.ParseResult.GetValueForOption(interfaceNamesOption);
                var methodNames = context.ParseResult.GetValueForOption(methodNamesOption);
                var propertyNames = context.ParseResult.GetValueForOption(propertyNamesOption);
                var enumMemberNames = context.ParseResult.GetValueForOption(enumMemberNamesOption);
                var bindingNames = context.ParseResult.GetValueForOption(bindingNamesOption);
                var verbose = context.ParseResult.GetValueForOption(verboseOption);

                await GenerateDeclarationsAsync(
                    assemblyPath,
                    namespaces,
                    outDir,
                    logPath,
                    configPath,
                    namespaceNames,
                    classNames,
                    interfaceNames,
                    methodNames,
                    propertyNames,
                    enumMemberNames,
                    bindingNames,
                    verbose);
            });

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task GenerateDeclarationsAsync(
        string assemblyPath,
        string[] namespaces,
        string outDir,
        string? logPath,
        string? configPath,
        string? namespaceNames,
        string? classNames,
        string? interfaceNames,
        string? methodNames,
        string? propertyNames,
        string? enumMemberNames,
        string? bindingNames,
        bool verbose)
    {
        try
        {
            // Validate assembly path
            if (!File.Exists(assemblyPath))
            {
                Console.Error.WriteLine($"Error: Assembly file not found: {assemblyPath}");
                Environment.Exit(3);
            }

            // Load configuration if provided
            var config = configPath != null && File.Exists(configPath)
                ? await GeneratorConfig.LoadAsync(configPath)
                : new GeneratorConfig();

            // Apply naming transform options from CLI
            config.NamespaceNames = ParseNameTransformOption(namespaceNames);
            config.ClassNames = ParseNameTransformOption(classNames);
            config.InterfaceNames = ParseNameTransformOption(interfaceNames);
            config.MethodNames = ParseNameTransformOption(methodNames);
            config.PropertyNames = ParseNameTransformOption(propertyNames);
            config.EnumMemberNames = ParseNameTransformOption(enumMemberNames);
            config.BindingNames = ParseNameTransformOption(bindingNames);

            // Load assembly
            Console.WriteLine($"Loading assembly: {assemblyPath}");

            var assemblyFileName = Path.GetFileName(assemblyPath);
            var isCoreLib = assemblyFileName.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase);

            Assembly assembly;
            MetadataAssemblyLoader? loader = null;

            if (isCoreLib)
            {
                // Use MetadataLoadContext for core library (cannot be loaded via standard reflection)
                loader = new MetadataAssemblyLoader(assemblyPath);
                assembly = loader.LoadFromAssemblyPath(assemblyPath);
                Console.WriteLine("  (using MetadataLoadContext for System.Private.CoreLib)");
            }
            else
            {
                // Use standard reflection for other assemblies
                assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
            }

            // Check if this is a type-forwarding assembly
            if (TypeForwardingResolver.IsTypeForwardingAssembly(assembly))
            {
                Console.WriteLine("  Detected type-forwarding assembly");

                // Get the specific types that are forwarded
                var forwardedTypes = TypeForwardingResolver.GetForwardedTypes(assembly);

                if (forwardedTypes.Count > 0)
                {
                    Console.WriteLine($"  Types forwarded to {forwardedTypes.Count} target assembly(ies):");
                    int totalTypes = 0;
                    foreach (var (targetName, types) in forwardedTypes)
                    {
                        Console.WriteLine($"    - {targetName}: {types.Count} types");
                        totalTypes += types.Count;
                    }

                    // Process only the forwarded types (they're already loaded as Type objects)
                    Console.WriteLine($"  Processing {totalTypes} forwarded types");

                    // We'll use the first target's types for processing
                    // (most type-forwarding assemblies forward to a single target)
                    var (primaryTarget, typesToProcess) = forwardedTypes.First();

                    // Filter the assembly's processing to only include these specific types
                    // The AssemblyProcessor will handle this via GetExportedTypes()
                    // which for type-forwarding assemblies should return the forwarded types
                }
                else
                {
                    Console.WriteLine("  Warning: No forwarding targets found");
                }
            }

            try
            {
                // Process assembly
                var processor = new AssemblyProcessor(config, namespaces, verbose);
                var processedAssembly = processor.ProcessAssembly(assembly);

                // Apply naming transforms if any are configured
                Dictionary<string, BindingEntry>? bindings = null;
                if (config.NamespaceNames != NameTransformOption.None ||
                    config.ClassNames != NameTransformOption.None ||
                    config.InterfaceNames != NameTransformOption.None ||
                    config.MethodNames != NameTransformOption.None ||
                    config.PropertyNames != NameTransformOption.None ||
                    config.EnumMemberNames != NameTransformOption.None)
                {
                    var applicator = new NameTransformApplicator(config);
                    processedAssembly = applicator.Apply(processedAssembly);
                    bindings = applicator.GetBindings();
                }

                // Get dependency tracker for import generation
                var dependencyTracker = processor.GetDependencyTracker();

                // Render declarations with dependencies
                var renderer = new DeclarationRenderer();
                var declarations = renderer.RenderDeclarations(processedAssembly, dependencyTracker);

                // Process metadata
                var metadata = processor.ProcessAssemblyMetadata(assembly);

                // Determine output file name
                var assemblyName = Path.GetFileNameWithoutExtension(assemblyPath);
                var outputFileName = $"{assemblyName}.d.ts";
                var outputPath = Path.Combine(outDir, outputFileName);
                var metadataFileName = $"{assemblyName}.metadata.json";
                var metadataPath = Path.Combine(outDir, metadataFileName);

                // Ensure output directory exists
                Directory.CreateDirectory(outDir);

                // Write TypeScript declarations
                await File.WriteAllTextAsync(outputPath, declarations, System.Text.Encoding.UTF8);
                Console.WriteLine($"Generated: {outputPath}");

                // Write metadata file
                var metadataWriter = new MetadataWriter();
                await metadataWriter.WriteMetadataAsync(metadata, metadataPath);
                Console.WriteLine($"Generated: {metadataPath}");

                // Write bindings manifest if transforms were applied
                if (bindings != null && bindings.Count > 0)
                {
                    var bindingsFileName = $"{assemblyName}.bindings.json";
                    var bindingsPath = Path.Combine(outDir, bindingsFileName);
                    var bindingsJson = System.Text.Json.JsonSerializer.Serialize(
                        bindings,
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(bindingsPath, bindingsJson, System.Text.Encoding.UTF8);
                    Console.WriteLine($"Generated: {bindingsPath}");
                }

                // Write log if requested
                if (logPath != null)
                {
                    var logger = new GenerationLogger();
                    var logData = logger.CreateLog(processedAssembly);
                    await File.WriteAllTextAsync(logPath, logData, System.Text.Encoding.UTF8);
                    Console.WriteLine($"Log written: {logPath}");
                }
            }
            finally
            {
                loader?.Dispose();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.Error.WriteLine($"  {ex.InnerException.Message}");
            }
            Environment.Exit(1);
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
}
