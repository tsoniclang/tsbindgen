using System.Text.Json;
using tsbindgen.Config;
using tsbindgen.Render.Analysis;
using tsbindgen.Render;
using tsbindgen.Render.Output;
using tsbindgen.Render.Transform;
using tsbindgen.Snapshot;

namespace tsbindgen.Render.Pipeline;

/// <summary>
/// Orchestrates Phase 3-4 pipeline: Transform → Analyze → Emit → Write.
/// Phase 3: Transform (creates TsAlias) + Analysis passes
/// Phase 4: Emit (.d.ts, metadata, bindings, stubs) + Write to disk
/// </summary>
public static class NamespacePipeline
{
    /// <summary>
    /// Phase 3: Builds NamespaceModels from NamespaceBundles.
    /// Applies name transformations (creates TsAlias) and analysis passes.
    /// </summary>
    public static IReadOnlyDictionary<string, NamespaceModel> BuildModels(
        IReadOnlyDictionary<string, NamespaceBundle> bundles,
        GeneratorConfig config)
    {
        var models = new Dictionary<string, NamespaceModel>();

        foreach (var (clrName, bundle) in bundles)
        {
            // Normalize
            var model = ModelTransform.Build(bundle, config);

            // Apply analysis passes (except InterfaceReduction which needs all models)
            model = DiamondAnalysis.Apply(model);
            model = CovarianceAdjustments.Apply(model);
            model = OverloadAdjustments.Apply(model);
            model = ExplicitInterfaceReview.Apply(model);
            model = DiagnosticsSummary.Apply(model);

            models[clrName] = model;
        }

        // Apply InterfaceReduction after all models are built (needs cross-namespace lookups)
        var reducedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in models)
        {
            var reducedModel = InterfaceReduction.Apply(model, models);
            reducedModels[clrName] = reducedModel;
        }

        return reducedModels;
    }

    /// <summary>
    /// Phase 4: Renders a single namespace to all artifacts (strings).
    /// </summary>
    public static NamespaceArtifacts RenderNamespace(NamespaceModel model, IReadOnlyDictionary<string, NamespaceModel> allModels)
    {
        var dtsContent = TypeScriptEmit.Emit(model, allModels);
        var metadataContent = MetadataEmit.Emit(model);
        var bindingsContent = BindingEmit.Emit(model);
        var jsStubContent = ModuleStubEmit.Emit(model);

        // Serialize the post-analysis model for debugging
        var snapshotContent = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return new NamespaceArtifacts(
            model.ClrName,
            model.TsAlias,
            dtsContent,
            metadataContent,
            bindingsContent,
            jsStubContent,
            snapshotContent);
    }

    /// <summary>
    /// Runs the complete Phase 3-4 pipeline: builds models, renders artifacts, writes files.
    /// </summary>
    public static void Run(
        string outputDir,
        IReadOnlyDictionary<string, NamespaceBundle> bundles,
        GeneratorConfig config,
        bool verbose)
    {
        Console.WriteLine("Phase 3: Transforming to TypeScript models...");
        Console.WriteLine("Phase 4: Rendering TypeScript declarations...");

        // Build models
        var models = BuildModels(bundles, config);

        // Create output directory
        var namespacesDir = Path.Combine(outputDir, "namespaces");
        Directory.CreateDirectory(namespacesDir);

        // Render each namespace
        var totalTypes = 0;
        var totalDiagnostics = 0;

        foreach (var model in models.Values)
        {
            var artifacts = RenderNamespace(model, models);

            // Create namespace directory
            var nsDir = Path.Combine(namespacesDir, model.TsAlias);
            Directory.CreateDirectory(nsDir);

            // Write files
            File.WriteAllText(Path.Combine(nsDir, "index.d.ts"), artifacts.DtsContent);
            File.WriteAllText(Path.Combine(nsDir, "metadata.json"), artifacts.MetadataContent);
            File.WriteAllText(Path.Combine(nsDir, "index.js"), artifacts.JsStubContent);

            if (artifacts.BindingsContent != null)
            {
                File.WriteAllText(Path.Combine(nsDir, "bindings.json"), artifacts.BindingsContent);
            }

            // Write post-analysis snapshot for debugging
            File.WriteAllText(Path.Combine(nsDir, "snapshot.json"), artifacts.SnapshotContent);

            totalTypes += model.Types.Count;
            totalDiagnostics += model.Diagnostics.Count;

            if (verbose)
            {
                var bindingsNote = artifacts.BindingsContent != null ? " (with bindings)" : "";
                Console.WriteLine($"    → {model.TsAlias} ({model.Types.Count} types){bindingsNote}");
            }
        }

        Console.WriteLine($"  Generated {models.Count} namespace declarations");
        Console.WriteLine($"  Total types: {totalTypes}");
        Console.WriteLine($"  Total diagnostics: {totalDiagnostics}");
        Console.WriteLine();
    }
}
