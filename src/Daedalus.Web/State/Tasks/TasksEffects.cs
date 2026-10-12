using Daedalus.Application.DTOs.WorkflowRuns;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Tasks;

/// <summary>
///     The tasks feature's I/O. Each method dispatches what it starts and what it got; an expected failure is an
///     action or a Result, never an exception. Cancellation propagates.
/// </summary>
public sealed class TasksEffects(ApiClient api, IDispatcher dispatcher, IStore<TasksState> tasks)
{
    /// <summary>The board reads one page of this many tasks, as the Tasks page always has.</summary>
    private const int BoardPage = 200;

    /// <summary>Fetches the board.</summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        await dispatcher.DispatchAsync(new TasksLoadStarted());
        var result = await api.GetTasksAsync(1, BoardPage, ct);
        if (result.IsSuccess)
        {
            await dispatcher.DispatchAsync(new TasksLoaded(result.Value.Items, result.Value.Total));
        }
        else
        {
            await dispatcher.DispatchAsync(new TasksLoadFailed(result.Error));
        }
    }

    /// <summary>
    ///     Starts a manufacture run for the task, marking it as starting while the call is in flight so a second press
    ///     is refused here rather than at the server.
    /// </summary>
    public async Task<Result<StartWorkflowRunResponse>> ManufactureAsync(Guid taskId, CancellationToken ct)
    {
        if (tasks.Value.Starting.Contains(taskId))
        {
            return Result<StartWorkflowRunResponse>.Failure("This task is already starting.");
        }

        await dispatcher.DispatchAsync(new TaskStartRequested(taskId));
        try
        {
            return await api.ManufactureTaskAsync(taskId, ct);
        }
        finally
        {
            await dispatcher.DispatchAsync(new TaskStartFinished(taskId));
        }
    }

    /// <summary>
    ///     Re-fetches the board after a reconnect, when it has been loaded, because any task may have gained or moved a
    ///     run meanwhile. A board never loaded is left alone.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct)
    {
        if (tasks.Value.Items.Count > 0)
        {
            await LoadAsync(ct);
        }
    }

    /// <summary>Re-fetches each listed task that belongs to the changed run.</summary>
    public async Task OnRunChangedAsync(WorkflowRunChanged change, CancellationToken ct)
    {
        foreach (var task in tasks.Value.Items.Where(t => t.WorkflowRunId == change.Id).ToList())
        {
            var result = await api.GetTaskAsync(task.Id, ct);
            if (result.IsSuccess)
            {
                await dispatcher.DispatchAsync(new TaskUpdated(result.Value));
            }
        }
    }
}
