using System.CommandLine;
using System.Reflection;

namespace GenerateDts;

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

        var rootCommand = new RootCommand("Generate TypeScript declarations from .NET assemblies")
        {
            assemblyPathArg,
            namespacesOption,
            outDirOption,
            logOption,
            configOption
        };

        rootCommand.SetHandler(
            async (assemblyPath, namespaces, outDir, logPath, configPath) =>
            {
                await GenerateDeclarationsAsync(
                    assemblyPath,
                    namespaces,
                    outDir,
                    logPath,
                    configPath);
            },
            assemblyPathArg,
            namespacesOption,
            outDirOption,
            logOption,
            configOption);

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task GenerateDeclarationsAsync(
        string assemblyPath,
        string[] namespaces,
        string outDir,
        string? logPath,
        string? configPath)
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

            // Load assembly
            Console.WriteLine($"Loading assembly: {assemblyPath}");
            var assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));

            // Process assembly
            var processor = new AssemblyProcessor(config, namespaces);
            var typeInfo = processor.ProcessAssembly(assembly);

            // Render declarations
            var renderer = new DeclarationRenderer();
            var declarations = renderer.RenderDeclarations(typeInfo);

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

            // Write log if requested
            if (logPath != null)
            {
                var logger = new GenerationLogger();
                var logData = logger.CreateLog(typeInfo);
                await File.WriteAllTextAsync(logPath, logData, System.Text.Encoding.UTF8);
                Console.WriteLine($"Log written: {logPath}");
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
}
