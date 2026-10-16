using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Costs;

/// <summary>
///     The Costs reducers. Pure: no I/O, no clock. A read's answer is kept only when no newer read has started since its
///     own, and an estimate's likewise; see <see cref="CostsState"/>.
/// </summary>
public static class CostsReducers
{
    /// <summary>Marks the newest read and clears the last error. A start seen out of order never moves the sequence back.</summary>
    [Reducer]
    public static CostsState On(CostsState s, CostsLoadStarted a) =>
        s with { Sequence = Math.Max(s.Sequence, a.Sequence), Error = null };

    /// <summary>
    ///     Keeps what the read answered, and moves the estimator to the first priced model, as the page always did; with
    ///     no pricing, the estimator keeps its model.
    /// </summary>
    [Reducer]
    public static CostsState On(CostsState s, CostsLoaded a)
    {
        if (a.Sequence < s.Sequence)
        {
            return s;
        }

        var estimator = a.Pricing.Count > 0 ? s.Estimator with { ModelId = a.Pricing[0].ModelId } : s.Estimator;
        return s with
        {
            Summary = a.Summary,
            Projects = a.Projects,
            Pricing = a.Pricing,
            Error = a.Error,
            Estimator = estimator,
        };
    }

    /// <summary>Takes the estimator's new inputs; the estimate on show stays until the new one lands.</summary>
    [Reducer]
    public static CostsState On(CostsState s, CostEstimateStarted a) =>
        s with { EstimateSequence = Math.Max(s.EstimateSequence, a.Sequence), Estimator = a.Estimator };

    [Reducer]
    public static CostsState On(CostsState s, CostEstimated a) =>
        a.Sequence < s.EstimateSequence ? s : s with { Estimate = a.Estimate };

    /// <summary>A failed estimate shows none, as the page always did.</summary>
    [Reducer]
    public static CostsState On(CostsState s, CostEstimateFailed a) =>
        a.Sequence < s.EstimateSequence ? s : s with { Estimate = null };

    [Reducer]
    public static CostsState On(CostsState s, CostTaskCostsLoaded a) =>
        s with { TaskCosts = s.TaskCosts.SetItem(a.ProjectId, a.Tasks) };

    /// <summary>Clears the page's fields and keeps both sequences; see <see cref="CostsState"/>.</summary>
    [Reducer]
    public static CostsState On(CostsState s, CostsClosed a) =>
        CostsState.Empty with { Sequence = s.Sequence, EstimateSequence = s.EstimateSequence };
}
