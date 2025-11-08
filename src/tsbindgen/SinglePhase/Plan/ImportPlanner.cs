using tsbindgen.SinglePhase.Model;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Plans import statements and aliasing.
/// </summary>
public sealed class ImportPlanner
{
    private readonly BuildContext _ctx;

    public ImportPlanner(BuildContext ctx)
    {
        _ctx = ctx;
    }

    public object PlanImports(SymbolGraph graph, object importGraph)
    {
        _ctx.Log("ImportPlanner: TODO - implement import planning");
        // TODO: Implement import planning
        return new object();
    }
}
