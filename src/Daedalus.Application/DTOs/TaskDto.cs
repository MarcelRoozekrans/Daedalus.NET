namespace Daedalus.Application.DTOs;

/// <summary>A board task. <c>Status</c> is derived from its run when it has one (phase 2.8).</summary>
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
    Uri? PullRequestUrl);
