namespace Daedalus.Application.Abstractions;

/// <summary>The state of a manufacture run, as a task reads it.</summary>
public enum WorkflowRunState
{
    /// <summary>The run cannot be read here: the workflow engine is off on this host, or the run does not exist.</summary>
    Unknown = 0,

    /// <summary>The run is running a node.</summary>
    Running = 1,

    /// <summary>The run is parked at its human approval gate.</summary>
    Awaiting = 2,

    /// <summary>The run finished and published.</summary>
    Succeeded = 3,

    /// <summary>The run failed.</summary>
    Failed = 4,

    /// <summary>The run was cancelled.</summary>
    Cancelled = 5,
}

/// <summary>A run's state, and its pull request once it has published one.</summary>
/// <param name="State">The run's state.</param>
/// <param name="PullRequestUrl">The run's pull request, or null before publish.</param>
public sealed record WorkflowRunStatus(WorkflowRunState State, Uri? PullRequestUrl)
{
    /// <summary>The status of a run this host cannot read.</summary>
    public static WorkflowRunStatus Unknown { get; } = new(WorkflowRunState.Unknown, null);

    /// <summary>Whether the run is not yet terminal: <see cref="WorkflowRunState.Running"/> or <see cref="WorkflowRunState.Awaiting"/>.</summary>
    public bool IsLive => State is WorkflowRunState.Running or WorkflowRunState.Awaiting;
}

/// <summary>
///     Reads a manufacture run's status for a task (phase 2.8, amendment A2). Every host resolves an implementation: a
///     host whose workflow engine is off gets one that answers <see cref="WorkflowRunStatus.Unknown"/>, and a task then
///     shows its stored status. Lives in Application so <c>ProjectQueryService</c> in Infrastructure can use it.
/// </summary>
public interface IWorkflowRunStatusReader
{
    /// <summary>The status of run <paramref name="runId"/>, or <see cref="WorkflowRunStatus.Unknown"/>.</summary>
    ValueTask<WorkflowRunStatus> ReadAsync(Guid runId, CancellationToken ct);
}
