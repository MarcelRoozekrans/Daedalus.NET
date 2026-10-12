using Daedalus.Application.DTOs.WorkflowRuns;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Runs;

/// <summary>
///     The runs feature's I/O. Each method dispatches what it starts and what it got; an expected failure is an
///     action, never an exception. The effects never catch cancellation; ApiClient reports it as a failed Result.
/// </summary>
public sealed class RunsEffects(ApiClient api, IDispatcher dispatcher, IStore<RunsState> runs)
{
    /// <summary>The awaiting-approval count reads one page of this many parked runs.</summary>
    private const int AwaitingApprovalPage = 100;

    /// <summary>Fetches the list page <paramref name="query"/> names.</summary>
    public async Task LoadListAsync(RunListQuery query, CancellationToken ct)
    {
        await dispatcher.DispatchAsync(new RunListRequested(query));
        var result = await api.GetRunsAsync(query, ct);
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new RunListLoaded(query, result.Value));
        }
        else
        {
            await dispatcher.DispatchAsync(new RunListFailed(query, result.Error));
        }
    }

    /// <summary>Opens the run and fetches it.</summary>
    public async Task LoadRunAsync(Guid id, CancellationToken ct)
    {
        await dispatcher.DispatchAsync(new RunSelected(id));
        await dispatcher.DispatchAsync(new RunLoadStarted(id));
        var result = await api.GetRunAsync(id, ct);
        if (result.IsFailure)
        {
            await dispatcher.DispatchAsync(new RunLoadFailed(id, result.Error));
        }
        else if (result.Value is { } run)
        {
            await dispatcher.DispatchAsync(new RunLoaded(run));
        }
        else
        {
            await dispatcher.DispatchAsync(new RunNotFound(id));
        }
    }

    /// <summary>Counts the runs parked at the human gate. A failed fetch keeps the last count.</summary>
    public async Task LoadAwaitingApprovalAsync(CancellationToken ct)
    {
        var result = await api.GetRunsAsync(new RunListQuery("Awaiting", null, null, Take: AwaitingApprovalPage), ct);
        if (result.IsSuccess)
        {
            var count = result.Value.Runs.Count(r => string.Equals(r.AwaitingSignal, RunSignals.HumanApproval, StringComparison.Ordinal));
            await dispatcher.DispatchAsync(new AwaitingApprovalLoaded(count, result.Value.Next is not null));
        }
    }

    /// <summary>
    ///     Sends the open run's gate draft as an approval. A refusal stays on the draft with its reason, and the run is
    ///     re-fetched, because a refusal usually means it moved on.
    /// </summary>
    public async Task<Result> ApproveAsync(CancellationToken ct)
    {
        var state = runs.Value;
        if (state.SelectedId is not { } id)
        {
            return Result.Failure("No run is open.");
        }

        await dispatcher.DispatchAsync(new GateSubmitStarted());
        var result = await api.ApproveRunAsync(id, [.. state.Gate.Dropped], state.Gate.ApplyStandingInstructions, ct);
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new GateSubmitSucceeded());
        }
        else
        {
            await dispatcher.DispatchAsync(new GateSubmitFailed(result.Error));
            await LoadRunAsync(id, ct);
        }

        return result;
    }

    /// <summary>Cancels the run; a refusal re-fetches it.</summary>
    public async Task<Result> CancelAsync(Guid id, string? reason, CancellationToken ct)
    {
        var result = await api.CancelRunAsync(id, reason, ct);
        if (result.IsFailure)
        {
            await LoadRunAsync(id, ct);
        }

        return result;
    }

    /// <summary>Retries the run's failed node; a refusal re-fetches it.</summary>
    public async Task<Result> RetryAsync(Guid id, CancellationToken ct)
    {
        var result = await api.RetryRunAsync(id, ct);
        if (result.IsFailure)
        {
            await LoadRunAsync(id, ct);
        }

        return result;
    }

    /// <summary>
    ///     Re-fetches what <paramref name="change"/> could alter on screen: the open run when it is the one, the list
    ///     when the run is on it or the first page is shown, and the awaiting-approval count.
    /// </summary>
    public async Task OnRunChangedAsync(WorkflowRunChanged change, CancellationToken ct)
    {
        var state = runs.Value;
        if (state.SelectedId == change.Id)
        {
            await LoadRunAsync(change.Id, ct);
        }

        if (state.List is not null && (state.Query.After is null || state.List.Runs.Any(r => r.Id == change.Id)))
        {
            await LoadListAsync(state.Query, ct);
        }

        await LoadAwaitingApprovalAsync(ct);
    }

    /// <summary>Re-fetches everything on screen, after a reconnect lost whatever changed meanwhile.</summary>
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        var state = runs.Value;
        if (state.SelectedId is { } id)
        {
            await LoadRunAsync(id, ct);
        }

        if (state.List is not null)
        {
            await LoadListAsync(state.Query, ct);
        }

        await LoadAwaitingApprovalAsync(ct);
    }
}
