using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Application.Commands.DeleteTask;

/// <summary>
///     Handles DeleteTaskCommand by removing the task from persistence.
///     Only pending or abandoned tasks can be deleted.
/// </summary>
public sealed class DeleteTaskCommandHandler(ITaskRepository taskRepository)
    : ICommandHandler<DeleteTaskCommand, Result>
{
    public async Task<Result> Handle(DeleteTaskCommand command, CancellationToken cancellationToken)
    {
        if (command.TaskId == Guid.Empty)
        {
            return Result.Failure("TaskId cannot be empty");
        }

        var taskResult = await taskRepository.GetByIdAsync(command.TaskId, cancellationToken);
        if (taskResult.IsFailure)
        {
            return Result.Failure($"Task not found: {taskResult.Error}");
        }

        var task = taskResult.Value;

        if (task.Status is TaskStatus.InProgress)
        {
            return Result.Failure("Cannot delete an in-progress task. Abandon it first.");
        }

        return await taskRepository.DeleteAsync(task.Id, cancellationToken);
    }
}
