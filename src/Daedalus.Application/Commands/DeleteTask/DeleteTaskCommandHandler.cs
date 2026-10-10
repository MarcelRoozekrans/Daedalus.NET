using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Daedalus.Application.Commands.DeleteTask;

/// <summary>
///     Deletes a task, unless its manufacture run is live.
/// </summary>
public sealed class DeleteTaskCommandHandler(ITaskRepository taskRepository, IWorkflowRunStatusReader runs)
    : IRequestHandler<DeleteTaskCommand, Result>
{
    public async ValueTask<Result> Handle(DeleteTaskCommand command, CancellationToken ct)
    {
        if (command.TaskId == Guid.Empty)
        {
            return Result.Failure("TaskId cannot be empty");
        }

        var taskResult = await taskRepository.GetByIdAsync(command.TaskId, ct);
        if (taskResult.IsFailure)
        {
            return Result.Failure($"Task not found: {taskResult.Error}");
        }

        var task = taskResult.Value;

        var run = await runs.ReadRunAsync(task, ct);
        if (run.IsLive)
        {
            return Result.Failure(TaskRunGuard.LiveRun(task, run));
        }

        return await taskRepository.DeleteAsync(task.Id, ct);
    }
}
