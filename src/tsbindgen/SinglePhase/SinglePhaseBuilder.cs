using tsbindgen.Core.Policy;
using tsbindgen.SinglePhase.Load;
using tsbindgen.SinglePhase.Model;
using tsbindgen.SinglePhase.Normalize;
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
    /// <param name="verboseLogging">Enable verbose logging (all categories)</param>
    /// <param name="logCategories">Specific log categories to enable</param>
    /// <returns>Build result with statistics and diagnostics</returns>
    public static BuildResult Build(
        IReadOnlyList<string> assemblyPaths,
        string outputDirectory,
        GenerationPolicy? policy = null,
        Action<string>? logger = null,
        bool verboseLogging = false,
        HashSet<string>? logCategories = null)
    {
        // Create build context with all shared services
        var ctx = BuildContext.Create(policy, logger, verboseLogging, logCategories);

        ctx.Log("Build", "=== Single-Phase Build Started ===");
        ctx.Log("Build", $"Assemblies: {assemblyPaths.Count}");
        ctx.Log("Build", $"Output: {outputDirectory}");

        try
        {
            // Phase 1: Load
            ctx.Log("Build", "\n--- Phase 1: Load ---");
            var graph = LoadPhase(ctx, assemblyPaths);
            var stats = graph.GetStatistics();
            ctx.Log("Build", $"Loaded: {stats.NamespaceCount} namespaces, {stats.TypeCount} types, {stats.TotalMembers} members");

            // Phase 2: Normalize (build indices)
            ctx.Log("Build", "\n--- Phase 2: Normalize ---");
            graph = graph.WithIndices();
            ctx.Log("Build", "Built symbol indices");

            // Phase 3: Shape
            ctx.Log("Build", "\n--- Phase 3: Shape ---");
            graph = ShapePhase(ctx, graph);
            ctx.Log("Build", "Applied all shaping transformations");

            // Phase 3.5: Name Reservation (after Shape, before Plan)
            ctx.Log("Build", "\n--- Phase 3.5: Name Reservation ---");
            graph = Normalize.NameReservation.ReserveAllNames(ctx, graph);
            ctx.Log("Build", "Reserved all TypeScript names through Renamer");

            // Phase 4: Plan
            ctx.Log("Build", "\n--- Phase 4: Plan ---");
            var plan = PlanPhase(ctx, graph);
            ctx.Log("Build", $"Planned emission order for {plan.NamespaceCount} namespaces");

            // Phase 5: Emit
            ctx.Log("Build", "\n--- Phase 5: Emit ---");
            EmitPhase(ctx, plan, outputDirectory);
            ctx.Log("Build", $"Emitted all files to {outputDirectory}");

            // Gather results
            var diagnostics = ctx.Diagnostics.GetAll();
            var hasErrors = ctx.Diagnostics.HasErrors();

            ctx.Log("Build", $"\n=== Build {(hasErrors ? "FAILED" : "SUCCEEDED")} ===");
            ctx.Log("Build", $"Diagnostics: {diagnostics.Count} total");

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
            ctx.Log("Build", $"\n!!! Build Exception: {ex.Message}");
            ctx.Log("Build", $"Stack trace:\n{ex.StackTrace}");
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

        // Substitute closed generic interface members
        InterfaceMemberSubstitution.SubstituteClosedInterfaces(ctx, graph);

        return graph;
    }

    /// <summary>
    /// Phase 3: Shape the symbol graph for TypeScript emission.
    /// Applies all transformations: interface inlining, synthesis, diamond resolution, etc.
    /// PURE - returns updated graph.
    /// </summary>
    private static SymbolGraph ShapePhase(BuildContext ctx, SymbolGraph graph)
    {
        // The shaping passes will be applied in sequence
        // Each pass may consult or update the Renamer

        // 1. Build interface indices BEFORE flattening (need original hierarchy)
        GlobalInterfaceIndex.Build(ctx, graph);
        InterfaceDeclIndex.Build(ctx, graph);

        // 2. Structural conformance analysis (synthesizes ViewOnly members) - PURE - returns new graph
        //    Must run before flattening so FindDeclaringInterface can walk hierarchy
        graph = StructuralConformance.Analyze(ctx, graph);

        // 3. Interface inlining (flatten interfaces - AFTER indices and conformance) - PURE - returns new graph
        graph = InterfaceInliner.Inline(ctx, graph);

        // 4. Explicit interface implementation synthesis - PURE - returns new graph
        graph = ExplicitImplSynthesizer.Synthesize(ctx, graph);

        // 5. Diamond inheritance resolution
        graph = DiamondResolver.Resolve(ctx, graph);

        // 6. Base overload addition
        graph = BaseOverloadAdder.AddOverloads(ctx, graph);

        // 7. Static-side analysis
        StaticSideAnalyzer.Analyze(ctx, graph);

        // 8. Indexer planning (PURE - returns new graph)
        graph = IndexerPlanner.Plan(ctx, graph);

        // 9. Hidden member (C# 'new') planning
        HiddenMemberPlanner.Plan(ctx, graph);

        // 10. Final indexers pass (PURE - ensures no indexer properties leak)
        graph = FinalIndexersPass.Run(ctx, graph);

        // 11. Constraint closure
        graph = ConstraintCloser.Close(ctx, graph);

        // 12. Return-type conflict resolution
        graph = OverloadReturnConflictResolver.Resolve(ctx, graph);

        // 13. View planning (explicit interface views) - PURE - returns new graph
        graph = ViewPlanner.Plan(ctx, graph);

        return graph;
    }

    /// <summary>
    /// Phase 4: Plan imports and emission order.
    /// </summary>
    private static EmissionPlan PlanPhase(BuildContext ctx, SymbolGraph graph)
    {
        // Build import graph
        var importGraph = ImportGraph.Build(ctx, graph);

        // Plan imports and aliases
        var imports = ImportPlanner.PlanImports(ctx, graph, importGraph);

        // Determine stable emission order
        var orderPlanner = new EmitOrderPlanner(ctx);
        var order = orderPlanner.PlanOrder(graph);

        // Unify method overloads (before PhaseGate validation)
        ctx.Log("Build", "\n--- Phase 4.5: Overload Unification ---");
        graph = OverloadUnifier.UnifyOverloads(ctx, graph);

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
/// Plan object for emission phase.
/// </summary>
public sealed record EmissionPlan
{
    public required SymbolGraph Graph { get; init; }
    public required ImportPlan Imports { get; init; }
    public required EmitOrder EmissionOrder { get; init; }

    public int NamespaceCount => Graph.Namespaces.Length;
}
