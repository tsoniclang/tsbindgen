using tsbindgen.SinglePhase.Model;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Builds dependency graph for imports.
/// </summary>
public static class ImportGraph
{
    public static object Build(BuildContext ctx, SymbolGraph graph)
    {
        ctx.Log("ImportGraph: TODO - implement import graph");
        // TODO: Implement import graph building
        return new object();
    }
}
