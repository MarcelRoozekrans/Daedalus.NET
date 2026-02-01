using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Daedalus.Application.Services;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Application.Commands.UpdateTask;

/// <summary>
///     Handles UpdateTaskCommand by updating task metadata while preserving execution history.
/// </summary>
public sealed class UpdateTaskCommandHandler(ITaskRepository taskRepository)
    : ICommandHandler<UpdateTaskCommand, Result<TaskDto>>
{
    public async Task<Result<TaskDto>> Handle(UpdateTaskCommand command, CancellationToken cancellationToken)
    {
        if (command.TaskId == Guid.Empty)
        {
            return Result.Failure<TaskDto>("TaskId cannot be empty");
        }

        var taskResult = await taskRepository.GetByIdAsync(command.TaskId, cancellationToken);
        if (taskResult.IsFailure)
        {
            return Result.Failure<TaskDto>($"Task not found: {taskResult.Error}");
        }

        var task = taskResult.Value;

        // Only allow updates on pending tasks
        if (task.Status != TaskStatus.Pending)
        {
            return Result.Failure<TaskDto>(
                $"Cannot update task: current status is {task.Status}. Only pending tasks can be updated.");
        }

        // Apply partial updates using domain method where applicable
        if (command.Title is not null || command.Description is not null ||
            command.Priority.HasValue || command.Phase is not null ||
            command.EstimatedComplexity.HasValue)
        {
            var updateResult = task.UpdateMetadata(
                command.Title ?? task.Title,
                command.Description ?? task.Description,
                command.Priority ?? task.Priority,
                command.Phase ?? task.Phase,
                command.EstimatedComplexity ?? task.EstimatedComplexity);

            if (updateResult.IsFailure)
            {
                return Result.Failure<TaskDto>(updateResult.Error);
            }
        }

        // Apply execution config updates via domain method
        if (command.ParallelGroup.HasValue || command.Prompt is not null ||
            command.CompletionPromise is not null || command.MaxIterations.HasValue)
        {
            var configResult = task.UpdateExecutionConfig(
                parallelGroup: command.ParallelGroup,
                prompt: command.Prompt,
                completionPromise: command.CompletionPromise,
                maxIterations: command.MaxIterations);

            if (configResult.IsFailure)
            {
                return Result.Failure<TaskDto>(configResult.Error);
            }
        }

        var updateDbResult = await taskRepository.UpdateAsync(task, cancellationToken);
        if (updateDbResult.IsFailure)
        {
            return Result.Failure<TaskDto>($"Failed to update task: {updateDbResult.Error}");
        }

        return Result.Success(TaskDtoMapper.ToDto(task));
    }
}
