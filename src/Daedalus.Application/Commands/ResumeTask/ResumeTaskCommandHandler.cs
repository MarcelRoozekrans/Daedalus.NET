using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;

namespace Daedalus.Application.Commands.ResumeTask;

/// <summary>
///     Handles ResumeTaskCommand by marking an abandoned task as pending again.
/// </summary>
public sealed class ResumeTaskCommandHandler(ITaskRepository taskRepository)
    : ICommandHandler<ResumeTaskCommand, Result<TaskDto>>
{
    public async Task<Result<TaskDto>> Handle(ResumeTaskCommand command, CancellationToken cancellationToken)
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

        var resumeResult = task.Resume(command.NewSessionId ?? Guid.NewGuid());
        if (resumeResult.IsFailure)
        {
            return Result.Failure<TaskDto>(resumeResult.Error);
        }

        var updateResult = await taskRepository.UpdateAsync(task, cancellationToken);
        if (updateResult.IsFailure)
        {
            return Result.Failure<TaskDto>($"Failed to update task: {updateResult.Error}");
        }

        return Result.Success(TaskDtoMapper.ToDto(task));
    }
}
