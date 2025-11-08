using tsbindgen.SinglePhase.Model;

namespace tsbindgen.SinglePhase.Plan;

/// <summary>
/// Plans stable, deterministic emission order.
/// </summary>
public sealed class EmitOrderPlanner
{
    private readonly BuildContext _ctx;

    public EmitOrderPlanner(BuildContext ctx)
    {
        _ctx = ctx;
    }

    public object PlanOrder(SymbolGraph graph)
    {
        _ctx.Log("EmitOrderPlanner: TODO - implement emission order planning");
        // TODO: Implement deterministic ordering
        return new object();
    }
}
