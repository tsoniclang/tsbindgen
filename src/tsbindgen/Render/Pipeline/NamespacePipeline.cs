using System.Text.Json;
using tsbindgen.Config;
using tsbindgen.Render.Analysis;
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
        GeneratorConfig config,
        GlobalInterfaceIndex? globalInterfaceIndex = null)
    {
        // Create analysis context for on-demand name computation
        var ctx = new AnalysisContext(config, globalInterfaceIndex);

        var models = new Dictionary<string, NamespaceModel>();

        foreach (var (clrName, bundle) in bundles)
        {
            // Normalize (no longer creates TsAlias strings - names computed on-demand)
            var model = ModelTransform.Build(bundle, config);

            // Apply analysis passes (per-namespace, before cross-namespace passes)
            model = DiagnosticsSummary.Apply(model);

            models[clrName] = model;
        }

        // Apply InterfaceFlattener FIRST - flatten all interface hierarchies
        // This eliminates "extends" clauses, relying on TypeScript structural typing
        // Replaces: InterfaceReduction, InterfaceHierarchyNormalizer, InterfaceOverloadFanIn, InterfaceSurfaceSynthesizer
        var flattenedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in models)
        {
            var flattenedModel = InterfaceFlattener.Apply(model, models, ctx);
            flattenedModels[clrName] = flattenedModel;
        }

        // Apply ExplicitInterfaceImplementation to add missing interface members to classes
        var interfaceFixedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in flattenedModels)
        {
            var fixedModel = ExplicitInterfaceImplementation.Apply(model, flattenedModels, ctx);
            interfaceFixedModels[clrName] = fixedModel;
        }

        // Apply DiamondOverloadFix to resolve remaining diamond inheritance conflicts
        var diamondFixedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in interfaceFixedModels)
        {
            var fixedModel = DiamondOverloadFix.Apply(model, interfaceFixedModels, ctx);
            diamondFixedModels[clrName] = fixedModel;
        }

        // Apply BaseClassOverloadFix to add base class method overloads with substituted generics
        var baseClassFixedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in diamondFixedModels)
        {
            var fixedModel = BaseClassOverloadFix.Apply(model, diamondFixedModels, ctx);
            baseClassFixedModels[clrName] = fixedModel;
        }

        // Apply StaticMethodOverloadFix to resolve TS2417 errors
        var staticFixedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in baseClassFixedModels)
        {
            var fixedModel = StaticMethodOverloadFix.Apply(model, baseClassFixedModels, ctx);
            staticFixedModels[clrName] = fixedModel;
        }

        // Apply CovarianceConflictPartitioner to identify ALL covariance conflicts (TS2416)
        // This handles both interfaces AND base classes with unified detection
        var covariancePartitionedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in staticFixedModels)
        {
            var fixedModel = CovarianceConflictPartitioner.Apply(model, staticFixedModels, ctx);
            covariancePartitionedModels[clrName] = fixedModel;
        }

        // Apply IndexerShapeCatalog in TWO phases to break circular dependencies (A2)
        // Phase A: Annotate interface indexers across all namespaces
        // Phase B: Propagate to classes via interface inference

        // Phase A: Process all interfaces first
        var phaseAModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in covariancePartitionedModels)
        {
            var interfaceOnlyModel = IndexerShapeCatalog.ApplyPhaseA(model, covariancePartitionedModels, ctx);
            phaseAModels[clrName] = interfaceOnlyModel;
        }

        // Phase B: Process all classes with Phase A results available
        var indexerAnnotatedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in phaseAModels)
        {
            var fullyAnnotatedModel = IndexerShapeCatalog.ApplyPhaseB(model, phaseAModels, ctx);
            indexerAnnotatedModels[clrName] = fullyAnnotatedModel;
        }

        // Apply OverloadReturnConflictResolver BEFORE StructuralConformance
        // This creates the TypeScript-representable surface by resolving return-type conflicts.
        // Keeps one method per (name, params, static) bucket, marks explicit interface
        // implementations as ViewOnly when they conflict with public methods.
        var conflictResolvedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in indexerAnnotatedModels)
        {
            var resolvedModel = OverloadReturnConflictResolver.Apply(model, indexerAnnotatedModels, ctx);
            conflictResolvedModels[clrName] = resolvedModel;
        }

        // Apply StructuralConformance to decide keep/drop implements based on structural equality
        // Now uses the TS-representable surface (only EmitScope.Class methods) after conflict resolution.
        // The gate - only keep `implements I` if class is structurally equal to I after normalization.
        // ExplicitViews are emitted directly in TypeScriptEmit as readonly properties.
        var structurallyConformantModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in conflictResolvedModels)
        {
            var conformantModel = StructuralConformance.Apply(model, conflictResolvedModels, ctx);
            structurallyConformantModels[clrName] = conformantModel;
        }

        // Apply StructuralConformanceSynthesizer (fixes TS2420)
        // Adds missing interface members to classes with explicit naming
        var conformanceSynthesizedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in structurallyConformantModels)
        {
            var synthesizedModel = StructuralConformanceSynthesizer.Apply(model, structurallyConformantModels, ctx);
            conformanceSynthesizedModels[clrName] = synthesizedModel;
        }

        // Apply IndexerNormalizer (fixes TS2416)
        // Converts multi-param indexer properties to method-only overloads
        var indexerNormalizedModels = new Dictionary<string, NamespaceModel>();
        foreach (var (clrName, model) in conformanceSynthesizedModels)
        {
            var normalizedModel = IndexerNormalizer.Apply(model, conformanceSynthesizedModels, ctx);
            indexerNormalizedModels[clrName] = normalizedModel;
        }

        // Guards A & B are implemented in TypeScriptEmit.IsTypeDefinedInCurrentNamespace()
        // - Guard A: Emitted-symbol filter for implements (fixes TS2724)
        // - Guard B: Namespace/Type existence checks (fixes TS2307, TS2694)

        return indexerNormalizedModels;
    }

    /// <summary>
    /// Phase 4: Renders a single namespace to all artifacts (strings).
    /// </summary>
    public static NamespaceArtifacts RenderNamespace(NamespaceModel model, IReadOnlyDictionary<string, NamespaceModel> allModels, AnalysisContext ctx)
    {
        var dtsContent = TypeScriptEmit.Emit(model, allModels, ctx);
        var facadeDtsContent = FacadeEmit.Generate(model, ctx);
        var metadataContent = MetadataEmit.Emit(model, ctx);
        var bindingsContent = BindingEmit.Emit(model, ctx);
        var jsStubContent = ModuleStubEmit.Emit(model);

        // Serialize the post-analysis model for debugging
        var snapshotContent = JsonSerializer.Serialize(model, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Generate simplified type list for debugging/comparison
        var typeListContent = TypeScriptTypeListEmit.Emit(model, ctx);

        return new NamespaceArtifacts(
            model.ClrName,
            model.TsAlias,
            dtsContent,
            facadeDtsContent,
            metadataContent,
            bindingsContent,
            jsStubContent,
            snapshotContent,
            typeListContent);
    }

    /// <summary>
    /// Runs the complete Phase 3-4 pipeline: builds models, renders artifacts, writes files.
    /// </summary>
    public static void Run(
        string outputDir,
        IReadOnlyDictionary<string, NamespaceBundle> bundles,
        GeneratorConfig config,
        GlobalInterfaceIndex? globalInterfaceIndex,
        bool verbose,
        bool debugTypeList = false)
    {
        Console.WriteLine("Phase 3: Transforming to TypeScript models...");
        Console.WriteLine("Phase 4: Rendering TypeScript declarations...");

        // Build models
        var models = BuildModels(bundles, config, globalInterfaceIndex);

        // Create analysis context for Phase 4 emission
        var ctx = new AnalysisContext(config, globalInterfaceIndex);

        // Create output directory
        var namespacesDir = Path.Combine(outputDir, "namespaces");
        Directory.CreateDirectory(namespacesDir);

        // Render each namespace
        var totalTypes = 0;
        var totalDiagnostics = 0;

        foreach (var model in models.Values)
        {
            var artifacts = RenderNamespace(model, models, ctx);

            // Create namespace directory structure
            var nsDir = Path.Combine(namespacesDir, model.TsAlias);
            var internalDir = Path.Combine(nsDir, "internal");
            Directory.CreateDirectory(nsDir);
            Directory.CreateDirectory(internalDir);

            // Write internal files (with _1, _2 suffixes)
            File.WriteAllText(Path.Combine(internalDir, "index.d.ts"), artifacts.DtsContent);
            File.WriteAllText(Path.Combine(internalDir, "index.js"), artifacts.JsStubContent);

            // Write facade (clean names)
            File.WriteAllText(Path.Combine(nsDir, "index.d.ts"), artifacts.FacadeDtsContent);

            // Write metadata and other files at namespace root
            File.WriteAllText(Path.Combine(nsDir, "metadata.json"), artifacts.MetadataContent);

            if (artifacts.BindingsContent != null)
            {
                File.WriteAllText(Path.Combine(nsDir, "bindings.json"), artifacts.BindingsContent);
            }

            // Write post-analysis snapshot for debugging
            File.WriteAllText(Path.Combine(nsDir, "snapshot.json"), artifacts.SnapshotContent);

            // Write TypeScript type list for debugging/comparison (optional)
            if (debugTypeList)
            {
                File.WriteAllText(Path.Combine(nsDir, "typelist.json"), artifacts.TypeListContent);
            }

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
