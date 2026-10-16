using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Auth;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Runs;

/// <summary>
///     The runs feature's I/O. Each method dispatches what it starts and what it got; an expected failure is an
///     action, never an exception. The effects never catch cancellation; ApiClient reports it as a failed Result.
///     A caller whose token is already cancelled dispatches nothing: its page is gone, and a start would re-open what
///     the page's close cleared. A failure that comes back after the token was cancelled is dropped too, because the
///     page's <see cref="RunListClosed"/> or <see cref="RunClosed"/> already ended the load. A
///     <see cref="SessionExpiry.SigningInAgain"/> failure only ends the load: the layout already says so. Any other
///     failure is logged as a warning, with the error text only.
/// </summary>
public sealed partial class RunsEffects(ApiClient api, IDispatcher dispatcher, IStore<RunsState> runs, ILogger<RunsEffects> logger)
{
    /// <summary>The awaiting-approval count reads one page of this many parked runs.</summary>
    private const int AwaitingApprovalPage = 100;

    /// <summary>Fetches the list page <paramref name="query"/> names.</summary>
    public async Task LoadListAsync(RunListQuery query, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        await dispatcher.DispatchAsync(new RunListRequested(query));
        var result = await api.GetRunsAsync(query, ct);
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new RunListLoaded(query, result.Value));
        }
        else if (ct.IsCancellationRequested)
        {
            // The list page is gone; its RunListClosed ended the load, and a page opened since owns what shows now.
        }
        else if (SessionExpiry.IsSigningIn(result.Error))
        {
            await dispatcher.DispatchAsync(new RunListAbandoned(query));
        }
        else
        {
            LogListFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new RunListFailed(query, result.Error));
        }
    }

    /// <summary>Opens the run and fetches it.</summary>
    public async Task LoadRunAsync(Guid id, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        await dispatcher.DispatchAsync(new RunSelected(id));
        await dispatcher.DispatchAsync(new RunLoadStarted(id));
        var result = await api.GetRunAsync(id, ct);
        if (result.IsFailure)
        {
            if (ct.IsCancellationRequested)
            {
                // The run's page is gone; its RunClosed ended the load.
                return;
            }

            if (SessionExpiry.IsSigningIn(result.Error))
            {
                await dispatcher.DispatchAsync(new RunLoadAbandoned(id));
                return;
            }

            LogRunFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new RunLoadFailed(id, result.Error));
        }
        else if (result.Value is { Run: { } run })
        {
            await dispatcher.DispatchAsync(new RunLoaded(run));
        }
        else if (result.Value.Forbidden)
        {
            await dispatcher.DispatchAsync(new RunForbidden(id));
        }
        else
        {
            await dispatcher.DispatchAsync(new RunNotFound(id));
        }
    }

    /// <summary>
    ///     Counts the runs parked at the human gate. A failed fetch keeps the last count and is logged, unless the session
    ///     is being renewed or the caller is gone.
    /// </summary>
    public async Task LoadAwaitingApprovalAsync(CancellationToken ct)
    {
        // No token check first: the count dispatches only an answer, and a cancelled call gets none.
        var result = await api.GetRunsAsync(new RunListQuery("Awaiting", null, null, Take: AwaitingApprovalPage), ct);
        if (result.IsSuccess)
        {
            var count = result.Value.Runs.Count(r => string.Equals(r.AwaitingSignal, RunSignals.HumanApproval, StringComparison.Ordinal));
            await dispatcher.DispatchAsync(new AwaitingApprovalLoaded(count, result.Value.Next is not null));
        }
        else if (!SessionExpiry.IsSigningIn(result.Error) && !ct.IsCancellationRequested)
        {
            LogCountFailed(logger, result.Error);
        }
    }

    /// <summary>
    ///     Sends the open run's gate draft as an approval, then re-fetches the run either way. On success the draft
    ///     stays busy until the run is seen off the gate, here or on a later change; the fetch here covers a page that
    ///     is offline and so gets no change. A refusal stays on the draft with its reason, because a refusal usually
    ///     means the run moved on; a <see cref="SessionExpiry.SigningInAgain"/> failure only frees the draft. A cancelled
    ///     call dispatches and fetches nothing: the page that made it is gone, and its <see cref="RunClosed"/> cleared
    ///     the draft.
    /// </summary>
    public async Task<Result> ApproveAsync(CancellationToken ct)
    {
        var state = runs.Value;
        if (state.SelectedId is not { } id)
        {
            return Result.Failure("No run is open.");
        }

        if (ct.IsCancellationRequested)
        {
            return Result.Failure(ApiClient.Cancelled);
        }

        await dispatcher.DispatchAsync(new GateSubmitStarted());
        var result = await api.ApproveRunAsync(id, [.. state.Gate.Dropped], state.Gate.ApplyStandingInstructions, ct);
        if (ct.IsCancellationRequested)
        {
            return result;
        }

        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new GateSubmitSucceeded());
        }
        else if (SessionExpiry.IsSigningIn(result.Error))
        {
            await dispatcher.DispatchAsync(new GateSubmitAbandoned());
        }
        else
        {
            LogWriteRefused(logger, RunWrite.Approve, result.Error);
            await dispatcher.DispatchAsync(new GateSubmitFailed(result.Error));
        }

        await LoadRunAsync(id, ct);
        return result;
    }

    /// <summary>Cancels the run; a refusal is logged and re-fetches it, unless the call itself was cancelled.</summary>
    public async Task<Result> CancelAsync(Guid id, string? reason, CancellationToken ct)
    {
        var result = await api.CancelRunAsync(id, reason, ct);
        await SettleAsync(RunWrite.Cancel, id, result, ct);
        return result;
    }

    /// <summary>Retries the run's failed node; a refusal is logged and re-fetches it, unless the call itself was cancelled.</summary>
    public async Task<Result> RetryAsync(Guid id, CancellationToken ct)
    {
        var result = await api.RetryRunAsync(id, ct);
        await SettleAsync(RunWrite.Retry, id, result, ct);
        return result;
    }

    /// <summary>
    ///     After a refused write, re-fetches the run, because a refusal usually means it moved on; a cancelled call
    ///     fetches nothing, through <see cref="LoadRunAsync"/>'s own check. A refusal is logged unless the session is
    ///     being renewed or the caller is gone.
    /// </summary>
    private async Task SettleAsync(RunWrite write, Guid id, Result result, CancellationToken ct)
    {
        if (result.IsSuccess)
        {
            return;
        }

        if (!SessionExpiry.IsSigningIn(result.Error) && !ct.IsCancellationRequested)
        {
            LogWriteRefused(logger, write, result.Error);
        }

        await LoadRunAsync(id, ct);
    }

    /// <summary>
    ///     Re-fetches what <paramref name="change"/> could alter on screen: the open run when it is the one, the list
    ///     when the run is on it or the first page is shown, and the awaiting-approval count. The list is judged on the
    ///     state after the run's fetch, so a list closed meanwhile is not fetched again.
    /// </summary>
    public async Task OnRunChangedAsync(WorkflowRunChanged change, CancellationToken ct)
    {
        if (runs.Value.SelectedId == change.Id)
        {
            await LoadRunAsync(change.Id, ct);
        }

        var state = runs.Value;
        if (state.ListRequested && (state.Query.After is null || (state.List?.Runs.Any(r => r.Id == change.Id) ?? false)))
        {
            await LoadListAsync(state.Query, ct);
        }

        await LoadAwaitingApprovalAsync(ct);
    }

    /// <summary>
    ///     Re-fetches everything on screen, after a reconnect lost whatever changed meanwhile. The list is judged on the
    ///     state after the run's fetch, so a list closed meanwhile is not fetched again.
    /// </summary>
    public async Task RefreshAllAsync(CancellationToken ct)
    {
        if (runs.Value.SelectedId is { } id)
        {
            await LoadRunAsync(id, ct);
        }

        var state = runs.Value;
        if (state.ListRequested)
        {
            await LoadListAsync(state.Query, ct);
        }

        await LoadAwaitingApprovalAsync(ct);
    }

    [LoggerMessage(EventId = 3110, Level = LogLevel.Warning, Message = "The runs could not be loaded: {Error}")]
    private static partial void LogListFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3111, Level = LogLevel.Warning, Message = "A run could not be loaded: {Error}")]
    private static partial void LogRunFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3112, Level = LogLevel.Warning, Message = "The awaiting-approval count could not be loaded: {Error}")]
    private static partial void LogCountFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3113, Level = LogLevel.Warning, Message = "A run write ({Write}) was refused: {Reason}")]
    private static partial void LogWriteRefused(ILogger logger, RunWrite write, string reason);
}

/// <summary>A write on a run, as a refusal's log names it.</summary>
public enum RunWrite
{
    /// <summary>The gate draft was sent as an approval.</summary>
    Approve,

    /// <summary>The run was cancelled.</summary>
    Cancel,

    /// <summary>The run's failed node was retried.</summary>
    Retry,
}
