using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Web.Auth;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Tasks;

/// <summary>
///     The tasks feature's I/O. Each method dispatches what it starts and what it got; an expected failure is an
///     action or a Result, never an exception. The effects never catch cancellation; ApiClient reports it as a
///     failed Result. A caller whose token is already cancelled dispatches nothing, and a failure that comes back after
///     it was cancelled is dropped: the page's <see cref="TasksClosed"/> ended the load. A
///     <see cref="SessionExpiry.SigningInAgain"/> failure only ends the load: the layout already says so. Any other
///     failure is logged as a warning, with the error text only.
/// </summary>
public sealed partial class TasksEffects(ApiClient api, IDispatcher dispatcher, IStore<TasksState> tasks, ILogger<TasksEffects> logger)
{
    /// <summary>The board reads one page of this many tasks, as the Tasks page always has.</summary>
    private const int BoardPage = 200;

    /// <summary>Fetches the board.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        // A page already gone dispatches nothing: a start would mark a board as asked for that no page shows.
        if (ct.IsCancellationRequested)
        {
            return;
        }

        await dispatcher.DispatchAsync(new TasksLoadStarted());
        var result = await api.GetTasksAsync(1, BoardPage, ct);
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new TasksLoaded(result.Value.Items, result.Value.Total));
        }
        else if (ct.IsCancellationRequested)
        {
            // The Tasks page is gone; its TasksClosed ended the load.
        }
        else if (SessionExpiry.IsSigningIn(result.Error))
        {
            await dispatcher.DispatchAsync(new TasksLoadAbandoned());
        }
        else
        {
            LogLoadFailed(logger, result.Error);
            await dispatcher.DispatchAsync(new TasksLoadFailed(result.Error));
        }
    }

    /// <summary>
    ///     Starts a manufacture run for the task, marking it as starting while the call is in flight so a second press
    ///     is refused here rather than at the server. A caller already gone starts nothing. A refusal is logged, unless
    ///     the session is being renewed or the caller is gone; the start's mark is cleared either way, because it
    ///     outlives the page.
    /// </summary>
    public async Task<Result<StartWorkflowRunResponse>> ManufactureAsync(Guid taskId, CancellationToken ct)
    {
        if (tasks.Value.Starting.Contains(taskId))
        {
            return Result<StartWorkflowRunResponse>.Failure("This task is already starting.");
        }

        if (ct.IsCancellationRequested)
        {
            return Result<StartWorkflowRunResponse>.Failure(ApiClient.Cancelled);
        }

        await dispatcher.DispatchAsync(new TaskStartRequested(taskId));
        try
        {
            var result = await api.ManufactureTaskAsync(taskId, ct);
            if (result.IsFailure && !SessionExpiry.IsSigningIn(result.Error) && !ct.IsCancellationRequested)
            {
                LogManufactureRefused(logger, result.Error);
            }

            return result;
        }
        finally
        {
            await dispatcher.DispatchAsync(new TaskStartFinished(taskId));
        }
    }

    /// <summary>
    ///     Re-fetches the board after a reconnect, when a load was ever asked for, because any task may have gained or
    ///     moved a run meanwhile. An empty board and a board whose load failed are re-fetched too; a board never asked
    ///     for is left alone. The projects are re-fetched by <see cref="Projects.ProjectsEffects.RefreshAsync"/>.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (tasks.Value.LoadRequested)
        {
            await LoadAsync(ct);
        }
    }

    /// <summary>
    ///     Re-fetches each listed task that belongs to the changed run, until the caller is gone. A task that cannot be
    ///     re-fetched keeps its row, and the failure is logged unless the session is being renewed.
    /// </summary>
    public async Task OnRunChangedAsync(WorkflowRunChanged change, CancellationToken ct)
    {
        foreach (var task in tasks.Value.Items.Where(t => t.WorkflowRunId == change.Id).ToList())
        {
            var result = await api.GetTaskAsync(task.Id, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (result.IsSuccess)
            {
                await dispatcher.DispatchAsync(new TaskUpdated(result.Value));
            }
            else if (!SessionExpiry.IsSigningIn(result.Error))
            {
                LogTaskFailed(logger, result.Error);
            }
        }
    }

    [LoggerMessage(EventId = 3000, Level = LogLevel.Warning, Message = "The tasks could not be loaded: {Error}")]
    private static partial void LogLoadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "A task could not be re-fetched after its run changed: {Error}")]
    private static partial void LogTaskFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Warning, Message = "A manufacture start was refused: {Reason}")]
    private static partial void LogManufactureRefused(ILogger logger, string reason);
}
