using ZeroAlloc.Results;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.ResumeTask;

/// <summary>
///     Handles ResumeTaskCommand by marking an abandoned task as pending again.
/// </summary>
public sealed class ResumeTaskCommandHandler(ITaskRepository taskRepository)
    : IRequestHandler<ResumeTaskCommand, Result<TaskDto>>
{
    public async ValueTask<Result<TaskDto>> Handle(ResumeTaskCommand command, CancellationToken ct)
    {
        if (command.TaskId == Guid.Empty)
        {
            return Result<TaskDto>.Failure("TaskId cannot be empty");
        }

        var taskResult = await taskRepository.GetByIdAsync(command.TaskId, ct);
        if (taskResult.IsFailure)
        {
            return Result<TaskDto>.Failure($"Task not found: {taskResult.Error}");
        }

        var task = taskResult.Value;

        var resumeResult = task.Resume(command.NewSessionId ?? Guid.NewGuid());
        if (resumeResult.IsFailure)
        {
            return Result<TaskDto>.Failure(resumeResult.Error);
        }

        var updateResult = await taskRepository.UpdateAsync(task, ct);
        if (updateResult.IsFailure)
        {
            return Result<TaskDto>.Failure($"Failed to update task: {updateResult.Error}");
        }

        return Result<TaskDto>.Success(TaskDtoMapper.ToDto(task));
    }
}
