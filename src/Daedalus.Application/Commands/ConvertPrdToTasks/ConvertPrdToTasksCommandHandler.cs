using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Commands.ConvertPrdToTasks;

/// <summary>
///     Handles conversion of PRD items to tasks and persists them to the database.
/// </summary>
public sealed partial class ConvertPrdToTasksCommandHandler(
    ITaskRepository taskRepository,
    ILogger<ConvertPrdToTasksCommandHandler> logger) : ICommandHandler<ConvertPrdToTasksCommand, Result<List<TaskDto>>>
{
    public async Task<Result<List<TaskDto>>> Handle(ConvertPrdToTasksCommand command,
        CancellationToken cancellationToken)
    {
        if (command.PrdItems.Count == 0)
        {
            return Result.Failure<List<TaskDto>>("No PRD items to convert");
        }

        try
        {
            var createdTasks = new List<TaskDto>();

            // Create tasks sequentially to maintain dependency relationships
            for (var index = 0; index < command.PrdItems.Count; index++)
            {
                var prdItem = command.PrdItems[index];
                var taskId = $"TASK-{index + 1:D3}";

                LogConvertingPrdItem(logger, taskId);

                var taskResult = Task.Create(
                    Guid.NewGuid(),
                    command.ProjectId,
                    taskId,
                    prdItem.Title,
                    prdItem.Description,
                    prdItem.Priority,
                    prdItem.Phase,
                    prdItem.ParallelGroup,
                    prdItem.Complexity,
                    BuildTaskPrompt(prdItem),
                    BuildCompletionPromise(prdItem),
                    DetermineMaxIterations(prdItem.Complexity));

                if (taskResult.IsFailure)
                {
                    logger.LogError("Failed to create task {TaskId}: {Error}", taskId, taskResult.Error);
                    return Result.Failure<List<TaskDto>>(taskResult.Error);
                }

                var task = taskResult.Value;

                // Add dependencies from PRD
                if (prdItem.Dependencies.Count > 0)
                {
                    foreach (var dependency in prdItem.Dependencies)
                    {
                        task.AddDependency(dependency);
                    }
                }

                // Persist the task
                var persistResult = await taskRepository.AddAsync(task, cancellationToken);
                if (persistResult.IsFailure)
                {
                    logger.LogError("Failed to persist task {TaskId}: {Error}", taskId, persistResult.Error);
                    return Result.Failure<List<TaskDto>>(persistResult.Error);
                }

                createdTasks.Add(TaskDtoMapper.ToDto(persistResult.Value));
                LogTaskCreatedSuccessfully(logger, taskId);
            }

            var taskCount = createdTasks.Count;
            LogConversionCompleted(logger, taskCount);
            return Result.Success(createdTasks);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error converting PRD items to tasks for project {ProjectId}", command.ProjectId);
            return Result.Failure<List<TaskDto>>($"Error converting PRD items: {ex.Message}");
        }
    }

    private static string BuildTaskPrompt(PrdItemForConversionDto prdItem)
    {
        return $"""
                Implement the following requirement:

                Title: {prdItem.Title}
                Description: {prdItem.Description}
                Acceptance Criteria: {prdItem.AcceptanceCriteria}
                Priority: {prdItem.Priority}
                Complexity: {prdItem.Complexity}
                """;
    }

    private static string BuildCompletionPromise(PrdItemForConversionDto prdItem) =>
        $"Implementation complete: {prdItem.Title}";

    private static int DetermineMaxIterations(Complexity complexity)
    {
        return complexity switch
        {
            Complexity.High => 10,
            Complexity.Medium => 7,
            Complexity.Low => 5,
            _ => 5
        };
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Converting PRD item to task: {TaskId}")]
    private static partial void LogConvertingPrdItem(
        ILogger logger,
        string taskId);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Information,
        Message = "Task {TaskId} created successfully")]
    private static partial void LogTaskCreatedSuccessfully(
        ILogger logger,
        string taskId);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Information,
        Message = "Successfully converted {Count} PRD items to tasks")]
    private static partial void LogConversionCompleted(
        ILogger logger,
        int count);
}
