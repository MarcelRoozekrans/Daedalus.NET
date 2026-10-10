using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using DomainTask = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Mappers;

/// <summary>
///     Shared mapper for converting domain Task entities to DTOs.
///     Centralizes mapping logic to avoid duplication across handlers and services.
/// </summary>
public static class TaskDtoMapper
{
    /// <summary>
    ///     Maps a Task domain entity to its full DTO representation.
    /// </summary>
    public static TaskDto ToDto(DomainTask task, WorkflowRunStatus run)
    {
        var executionDtos = task.Executions
            .Select(e => new TaskExecutionDto(
                e.Id,
                e.TaskId,
                e.SessionId,
                e.IterationNumber,
                e.Prompt,
                e.LlmResponse,
                e.CompletionPromiseFound,
                e.ExecutedAt,
                e.ExecutionDuration,
                e.Error,
                e.InputTokens,
                e.OutputTokens,
                e.ModelId))
            .ToList();

        return new TaskDto(
            task.Id,
            task.TaskId,
            task.ProjectId,
            task.Title,
            task.Description,
            (int)task.Priority,
            task.Phase,
            task.ParallelGroup,
            task.Dependencies,
            task.FilesToModify,
            (int)task.EstimatedComplexity,
            task.Prompt,
            task.CompletionPromise,
            task.MaxIterations,
            (int)TaskStatusDerivation.Derive(task, run),
            task.CurrentSessionId,
            task.Result,
            task.IterationCount,
            task.CreatedAt,
            task.CompletedAt,
            task.Learnings,
            task.LearningsUpdatedAt,
            executionDtos,
            task.WorkflowRunId,
            run.PullRequestUrl);
    }

    /// <summary>
    ///     Maps a Task domain entity to a DTO without execution history (lightweight).
    /// </summary>
    public static TaskDto ToDtoWithoutExecutions(DomainTask task, WorkflowRunStatus run)
    {
        return new TaskDto(
            task.Id,
            task.TaskId,
            task.ProjectId,
            task.Title,
            task.Description,
            (int)task.Priority,
            task.Phase,
            task.ParallelGroup,
            task.Dependencies,
            task.FilesToModify,
            (int)task.EstimatedComplexity,
            task.Prompt,
            task.CompletionPromise,
            task.MaxIterations,
            (int)TaskStatusDerivation.Derive(task, run),
            task.CurrentSessionId,
            task.Result,
            task.IterationCount,
            task.CreatedAt,
            task.CompletedAt,
            task.Learnings,
            task.LearningsUpdatedAt,
            [],
            task.WorkflowRunId,
            run.PullRequestUrl);
    }

    /// <summary>
    ///     Maps a TaskExecution domain entity to its DTO representation.
    /// </summary>
    public static TaskExecutionDto ToExecutionDto(TaskExecution execution)
    {
        return new TaskExecutionDto(
            execution.Id,
            execution.TaskId,
            execution.SessionId,
            execution.IterationNumber,
            execution.Prompt,
            execution.LlmResponse,
            execution.CompletionPromiseFound,
            execution.ExecutedAt,
            execution.ExecutionDuration,
            execution.Error,
            execution.InputTokens,
            execution.OutputTokens,
            execution.ModelId);
    }
}
