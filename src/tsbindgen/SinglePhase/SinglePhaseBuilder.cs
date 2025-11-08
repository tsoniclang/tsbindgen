using tsbindgen.Core.Policy;
using tsbindgen.SinglePhase.Load;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Shape;
using tsbindgen.SinglePhase.Plan;
using tsbindgen.SinglePhase.Emit;

namespace tsbindgen.SinglePhase;

/// <summary>
/// Main orchestrator for the single-phase build pipeline.
/// Coordinates: Load → Normalize → Shape → Plan → Emit
/// </summary>
public static class SinglePhaseBuilder
{
    /// <summary>
    /// Build TypeScript declarations from .NET assemblies.
    /// </summary>
    /// <param name="assemblyPaths">Paths to assemblies to process</param>
    /// <param name="outputDirectory">Where to write generated files</param>
    /// <param name="policy">Generation policy (uses defaults if null)</param>
    /// <param name="logger">Optional logger for progress messages</param>
    /// <returns>Build result with statistics and diagnostics</returns>
    public static BuildResult Build(
        IReadOnlyList<string> assemblyPaths,
        string outputDirectory,
        GenerationPolicy? policy = null,
        Action<string>? logger = null)
    {
        // Create build context with all shared services
        var ctx = BuildContext.Create(policy, logger);

        ctx.Log("=== Single-Phase Build Started ===");
        ctx.Log($"Assemblies: {assemblyPaths.Count}");
        ctx.Log($"Output: {outputDirectory}");

        try
        {
            // Phase 1: Load
            ctx.Log("\n--- Phase 1: Load ---");
            var graph = LoadPhase(ctx, assemblyPaths);
            var stats = graph.GetStatistics();
            ctx.Log($"Loaded: {stats.NamespaceCount} namespaces, {stats.TypeCount} types, {stats.TotalMembers} members");

            // Phase 2: Normalize (currently minimal - just build indices)
            ctx.Log("\n--- Phase 2: Normalize ---");
            graph.BuildIndices();
            ctx.Log("Built symbol indices");

            // Phase 3: Shape
            ctx.Log("\n--- Phase 3: Shape ---");
            ShapePhase(ctx, graph);
            ctx.Log("Applied all shaping transformations");

            // Phase 4: Plan
            ctx.Log("\n--- Phase 4: Plan ---");
            var plan = PlanPhase(ctx, graph);
            ctx.Log($"Planned emission order for {plan.NamespaceCount} namespaces");

            // Phase 5: Emit
            ctx.Log("\n--- Phase 5: Emit ---");
            EmitPhase(ctx, graph, plan, outputDirectory);
            ctx.Log($"Emitted all files to {outputDirectory}");

            // Gather results
            var diagnostics = ctx.Diagnostics.GetAll();
            var hasErrors = ctx.Diagnostics.HasErrors();

            ctx.Log($"\n=== Build {(hasErrors ? "FAILED" : "SUCCEEDED")} ===");
            ctx.Log($"Diagnostics: {diagnostics.Count} total");

            return new BuildResult
            {
                Success = !hasErrors,
                Statistics = stats,
                Diagnostics = diagnostics,
                RenameDecisions = ctx.Renamer.GetAllDecisions()
            };
        }
        catch (Exception ex)
        {
            ctx.Log($"\n!!! Build Exception: {ex.Message}");
            ctx.Diagnostics.Error("BUILD_EXCEPTION", $"Build failed with exception: {ex.Message}");

            return new BuildResult
            {
                Success = false,
                Statistics = new SymbolGraphStatistics
                {
                    NamespaceCount = 0,
                    TypeCount = 0,
                    MethodCount = 0,
                    PropertyCount = 0,
                    FieldCount = 0,
                    EventCount = 0
                },
                Diagnostics = ctx.Diagnostics.GetAll(),
                RenameDecisions = ctx.Renamer.GetAllDecisions()
            };
        }
    }

    /// <summary>
    /// Phase 1: Load assemblies and build symbol graph.
    /// </summary>
    private static SymbolGraph LoadPhase(BuildContext ctx, IReadOnlyList<string> assemblyPaths)
    {
        // Load assemblies using MetadataLoadContext
        var loader = new AssemblyLoader(ctx);
        var loadContext = loader.CreateLoadContext(assemblyPaths);

        // Read all types and members via reflection
        var reader = new ReflectionReader(ctx);
        var graph = reader.ReadAssemblies(loadContext, assemblyPaths);

        return graph;
    }

    /// <summary>
    /// Phase 3: Shape the symbol graph for TypeScript emission.
    /// Applies all transformations: interface inlining, synthesis, diamond resolution, etc.
    /// </summary>
    private static void ShapePhase(BuildContext ctx, SymbolGraph graph)
    {
        // The shaping passes will be applied in sequence
        // Each pass may consult or update the Renamer

        // 1. Interface inlining
        InterfaceInliner.Inline(ctx, graph);

        // 2. Explicit interface implementation synthesis
        ExplicitImplSynthesizer.Synthesize(ctx, graph);

        // 3. Diamond inheritance resolution
        DiamondResolver.Resolve(ctx, graph);

        // 4. Base overload addition
        BaseOverloadAdder.AddOverloads(ctx, graph);

        // 5. Static-side analysis
        StaticSideAnalyzer.Analyze(ctx, graph);

        // 6. Indexer planning
        IndexerPlanner.Plan(ctx, graph);

        // 7. Hidden member (C# 'new') planning
        HiddenMemberPlanner.Plan(ctx, graph);

        // 8. Constraint closure
        ConstraintCloser.Close(ctx, graph);

        // 9. Global interface index building (cross-assembly)
        GlobalInterfaceIndex.Build(ctx, graph);

        // 10. Return-type conflict resolution
        OverloadReturnConflictResolver.Resolve(ctx, graph);

        // 11. Structural conformance analysis
        StructuralConformance.Analyze(ctx, graph);

        // 12. View planning (explicit interface views)
        ViewPlanner.Plan(ctx, graph);
    }

    /// <summary>
    /// Phase 4: Plan imports and emission order.
    /// </summary>
    private static EmissionPlan PlanPhase(BuildContext ctx, SymbolGraph graph)
    {
        // Build import graph
        var importGraph = ImportGraph.Build(ctx, graph);

        // Plan imports and aliases
        var importPlanner = new ImportPlanner(ctx);
        var imports = importPlanner.PlanImports(graph, importGraph);

        // Determine stable emission order
        var orderPlanner = new EmitOrderPlanner(ctx);
        var order = orderPlanner.PlanOrder(graph);

        // Validate before proceeding
        PhaseGate.Validate(ctx, graph, imports);

        return new EmissionPlan
        {
            Graph = graph,
            Imports = imports,
            EmissionOrder = order
        };
    }

    /// <summary>
    /// Phase 5: Emit all output files.
    /// </summary>
    private static void EmitPhase(
        BuildContext ctx,
        SymbolGraph graph,
        EmissionPlan plan,
        string outputDirectory)
    {
        // Emit internal/index.d.ts for each namespace
        InternalIndexEmitter.Emit(ctx, plan, outputDirectory);

        // Emit facade/index.d.ts for each namespace
        FacadeEmitter.Emit(ctx, plan, outputDirectory);

        // Emit metadata.json for each namespace
        MetadataEmitter.Emit(ctx, plan, outputDirectory);

        // Emit bindings.json for each namespace
        BindingEmitter.Emit(ctx, plan, outputDirectory);

        // Emit index.js stubs for each namespace
        ModuleStubEmitter.Emit(ctx, plan, outputDirectory);
    }
}

/// <summary>
/// Result of a build operation.
/// </summary>
public sealed record BuildResult
{
    public required bool Success { get; init; }
    public required SymbolGraphStatistics Statistics { get; init; }
    public required IReadOnlyList<Core.Diagnostics.Diagnostic> Diagnostics { get; init; }
    public required IReadOnlyCollection<Core.Renaming.RenameDecision> RenameDecisions { get; init; }
}

/// <summary>
/// Internal plan object for emission phase.
/// </summary>
internal sealed record EmissionPlan
{
    public required SymbolGraph Graph { get; init; }
    public required object Imports { get; init; } // Will be properly typed later
    public required object EmissionOrder { get; init; } // Will be properly typed later

    public int NamespaceCount => Graph.Namespaces.Count;
}
