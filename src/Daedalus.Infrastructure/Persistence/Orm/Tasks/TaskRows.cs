using Daedalus.Domain.Entities;
using DomainTask = Daedalus.Domain.Entities.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Infrastructure.Persistence.Orm.Tasks;

/// <summary>One <c>"Tasks"</c> row, every column and its <c>xmin</c>, as <see cref="TaskSql"/> reads it.</summary>
internal sealed record TaskRow(
    Guid Id,
    string TaskId,
    Guid ProjectId,
    string Title,
    string Description,
    Priority Priority,
    TaskStatus Status,
    string Phase,
    int ParallelGroup,
    Complexity EstimatedComplexity,
    string Prompt,
    string CompletionPromise,
    int MaxIterations,
    Guid? CurrentSessionId,
    string? Result,
    int IterationCount,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    string Learnings,
    DateTime? LearningsUpdatedAt,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> FilesToModify,
    Guid? WorkflowRunId,
    uint RowVersion)
{
    /// <summary>The task the row holds, as stored: <see cref="DomainTask.Rehydrate"/> validates nothing.</summary>
    /// <param name="executions">The task's execution records, empty for a read that loads none.</param>
    /// <returns>The domain task.</returns>
    public DomainTask ToDomain(IEnumerable<TaskExecution> executions) =>
        DomainTask.Rehydrate(
            Id, TaskId, ProjectId, Title, Description, Priority, Status, Phase, ParallelGroup, EstimatedComplexity,
            Prompt, CompletionPromise, MaxIterations, CurrentSessionId, Result, IterationCount, CreatedAt, CompletedAt,
            Learnings, LearningsUpdatedAt, Dependencies, FilesToModify, WorkflowRunId, RowVersion, executions);
}

/// <summary>One <c>"TaskExecutions"</c> row, every column, as <see cref="TaskSql"/> reads it.</summary>
internal sealed record TaskExecutionRow(
    Guid Id,
    Guid TaskId,
    Guid SessionId,
    int IterationNumber,
    string Prompt,
    string LlmResponse,
    bool CompletionPromiseFound,
    DateTime ExecutedAt,
    TimeSpan ExecutionDuration,
    string? Error,
    int InputTokens,
    string? ModelId,
    int OutputTokens)
{
    /// <summary>The execution record the row holds, as stored.</summary>
    /// <returns>The domain execution record.</returns>
    public TaskExecution ToDomain() =>
        TaskExecution.Rehydrate(
            Id, TaskId, SessionId, IterationNumber, Prompt, LlmResponse, CompletionPromiseFound, ExecutedAt,
            ExecutionDuration, Error, InputTokens, ModelId, OutputTokens);
}

/// <summary>The task a manufacture run is attached to, as <see cref="TaskSql.RunLinksAsync"/> reads it.</summary>
internal sealed record TaskRunLinkRow(Guid? RunId, Guid Id, string TaskId, string Title);
