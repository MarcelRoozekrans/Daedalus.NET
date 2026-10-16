using Daedalus.Web.Auth;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Costs;

/// <summary>
///     The Costs page's I/O: its three reads, the estimate and an expanded project's tasks. An expected failure is an
///     action, never an exception. A caller already gone dispatches nothing, and a read whose caller went while it was in
///     flight stores nothing. A session being renewed stores nothing and logs nothing. Every other failure is logged as a
///     warning, with its text only.
/// </summary>
public sealed partial class CostsEffects(
    ApiClient api, IDispatcher dispatcher, IStore<CostsState> costs, ILogger<CostsEffects> logger)
{
    /// <summary>
    ///     Reads the summary, the projects and the pricing together, then estimates for what the estimator then asks: the
    ///     first priced model. Only the summary's failure is shown; the tables show empty, as the page always did.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        // Read and dispatched with no await between, so the next read takes the number after this one's.
        var sequence = costs.Value.Sequence + 1;
        await dispatcher.DispatchAsync(new CostsLoadStarted(sequence));

        var summaryTask = api.GetCostSummaryAsync(ct);
        var projectsTask = api.GetCostsByProjectAsync(ct);
        var pricingTask = api.GetModelPricingAsync(ct);
        await Task.WhenAll(summaryTask, projectsTask, pricingTask);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var summary = await summaryTask;
        var projects = await projectsTask;
        var pricing = await pricingTask;
        var error = Failure(summary.IsFailure ? summary.Error : null, "summary");
        _ = Failure(projects.IsFailure ? projects.Error : null, "projects");
        _ = Failure(pricing.IsFailure ? pricing.Error : null, "pricing");

        await dispatcher.DispatchAsync(new CostsLoaded(
            sequence,
            summary.IsSuccess ? summary.Value : null,
            projects.IsSuccess ? projects.Value : [],
            pricing.IsSuccess ? pricing.Value : [],
            error));

        // With no pricing the estimator has no model, and an estimate for none can only fail.
        if (costs.Value.Estimator.ModelId.Length > 0)
        {
            await EstimateAsync(costs.Value.Estimator, ct);
        }
    }

    /// <summary>
    ///     Estimates for <paramref name="estimator"/>, which the estimator then shows as its inputs. A failure shows no
    ///     estimate; a session being renewed leaves the one on show.
    /// </summary>
    public async Task EstimateAsync(CostEstimator estimator, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        var sequence = costs.Value.EstimateSequence + 1;
        await dispatcher.DispatchAsync(new CostEstimateStarted(sequence, estimator));
        var result = await api.EstimateCostAsync(estimator.ModelId, estimator.Turns, estimator.PromptTokens, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new CostEstimated(sequence, result.Value));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error))
        {
            LogEstimateFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new CostEstimateFailed(sequence));
        }
    }

    /// <summary>
    ///     Reads project <paramref name="projectId"/>'s tasks, once: a project already read is not read again. A failure
    ///     is not kept, so the next expand asks again.
    /// </summary>
    public async Task LoadTaskCostsAsync(Guid projectId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || costs.Value.TaskCosts.ContainsKey(projectId))
        {
            return;
        }

        var result = await api.GetCostsByProjectIdAsync(projectId, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new CostTaskCostsLoaded(projectId, result.Value));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error))
        {
            LogTaskCostsFailed(logger, result.Error);
        }
    }

    /// <summary>
    ///     The error to keep for a failed read of <paramref name="what"/>, logged; <see langword="null"/> for no failure
    ///     and for a session being renewed, which is neither kept nor logged.
    /// </summary>
    private string? Failure(string? error, string what)
    {
        if (error is null || SessionExpiry.IsSigningIn(error))
        {
            return null;
        }

        LogLoadFailed(logger, what, error);
        return error;
    }

    [LoggerMessage(EventId = 3500, Level = LogLevel.Warning, Message = "The cost {What} could not be loaded: {Error}")]
    private static partial void LogLoadFailed(ILogger logger, string what, string error);

    [LoggerMessage(EventId = 3501, Level = LogLevel.Warning, Message = "The cost estimate could not be loaded: {Error}")]
    private static partial void LogEstimateFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3502, Level = LogLevel.Warning, Message = "A project's task costs could not be loaded: {Error}")]
    private static partial void LogTaskCostsFailed(ILogger logger, string error);
}
