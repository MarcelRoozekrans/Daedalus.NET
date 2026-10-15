namespace Daedalus.Web.Services;

/// <summary>
///     A board task as the tasks API returns it. <c>RunStatus</c> and <c>AwaitingSignal</c> are the task's run's own,
///     mapped by <see cref="RunStatusLabels"/> as the Runs page maps them; <c>RunStatus</c> is null for a task with no run.
/// </summary>
public record TaskDto(
    Guid Id,
    string TaskId,
    Guid ProjectId,
    string Title,
    string Description,
    int Priority,
    string Phase,
    int ParallelGroup,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> FilesToModify,
    int EstimatedComplexity,
    string Prompt,
    string CompletionPromise,
    int MaxIterations,
    int Status,
    Guid? CurrentSessionId,
    string? Result,
    int IterationCount,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string? Learnings,
    DateTime? LearningsUpdatedAt,
    IReadOnlyList<TaskExecutionDto> Executions,
    Guid? WorkflowRunId,
    Uri? PullRequestUrl,
    string? RunStatus,
    string? AwaitingSignal);
