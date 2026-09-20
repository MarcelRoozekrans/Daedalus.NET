using ZeroAlloc.Results;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using ZeroAlloc.Mediator;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Application.Commands.AbandonTask;

/// <summary>
///     Handles AbandonTaskCommand by marking a task as abandoned and setting its result reason.
/// </summary>
public sealed class AbandonTaskCommandHandler(ITaskRepository taskRepository)
    : IRequestHandler<AbandonTaskCommand, Result<TaskDto>>
{
    /// <summary>
    ///     Abandons a task and returns its updated DTO representation.
    /// </summary>
    public async ValueTask<Result<TaskDto>> Handle(AbandonTaskCommand command, CancellationToken cancellationToken)
    {
        // Validate command
        if (command.TaskId == Guid.Empty)
        {
            return Result<TaskDto>.Failure("TaskId cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result<TaskDto>.Failure("Reason cannot be empty");
        }

        // Fetch the task
        var taskResult = await taskRepository.GetByIdAsync(command.TaskId, cancellationToken);
        if (taskResult.IsFailure)
        {
            return Result<TaskDto>.Failure($"Task not found: {taskResult.Error}");
        }

        var task = taskResult.Value;

        // Validate task can be abandoned
        if (task.Status == TaskStatus.Completed || task.Status == TaskStatus.Abandoned)
        {
            return Result<TaskDto>.Failure($"Task cannot be abandoned: current status is {task.Status}");
        }

        // Update task status - use the domain method
        var abandonResult = task.Abandon();
        if (abandonResult.IsFailure)
        {
            return Result<TaskDto>.Failure(abandonResult.Error);
        }

        // Persist changes
        var updateResult = await taskRepository.UpdateAsync(task, cancellationToken);
        if (updateResult.IsFailure)
        {
            return Result<TaskDto>.Failure($"Failed to update task: {updateResult.Error}");
        }

        // Map to DTO and return
        var taskDto = TaskDtoMapper.ToDto(task);
        return Result<TaskDto>.Success(taskDto);
    }
}
