using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Daedalus.Application.Commands.DeleteProject;

/// <summary>
///     Deletes a project and, through the cascade, its tasks, unless one of those tasks has a live manufacture run
///     (amendment A7). The refusal is a <see cref="TaskRunGuard"/> live-run conflict, which maps to 409.
/// </summary>
public sealed partial class DeleteProjectCommandHandler(
    IProjectRepository projectRepository,
    ITaskRepository taskRepository,
    IWorkflowRunStatusReader runs,
    ILogger<DeleteProjectCommandHandler> logger) : IRequestHandler<DeleteProjectCommand, Result>
{
    public async ValueTask<Result> Handle(DeleteProjectCommand command, CancellationToken ct)
    {
        try
        {
            var projectResult = await projectRepository.GetByIdAsync(command.Id, ct)
                .ConfigureAwait(false);

            if (projectResult.IsFailure)
            {
                return Result.Failure(projectResult.Error);
            }

            var tasksResult = await taskRepository.GetByProjectIdAsync(command.Id, ct).ConfigureAwait(false);
            if (tasksResult.IsFailure)
            {
                return Result.Failure(tasksResult.Error);
            }

            foreach (var task in tasksResult.Value)
            {
                var run = await runs.ReadRunAsync(task, ct).ConfigureAwait(false);
                if (run.IsLive)
                {
                    return Result.Failure(TaskRunGuard.LiveRunInProject(task, run));
                }
            }

            var deleteResult = await projectRepository.DeleteAsync(command.Id, ct)
                .ConfigureAwait(false);

            if (deleteResult.IsFailure)
            {
                LogDeleteProjectFailed(logger, deleteResult.Error);
                return deleteResult;
            }

            LogProjectDeleted(logger, command.Id);
            return Result.Success();
        }
        catch (Exception ex)
        {
            LogDeleteProjectUnexpected(logger, ex, command.Id);
            return Result.Failure($"Error deleting project {command.Id}. The cause is logged.");
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Failed to delete project: {Error}")]
    private static partial void LogDeleteProjectFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Project {ProjectId} deleted successfully")]
    private static partial void LogProjectDeleted(ILogger logger, Guid projectId);

    [LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "Unexpected error deleting project {ProjectId}")]
    private static partial void LogDeleteProjectUnexpected(ILogger logger, Exception ex, Guid projectId);
}
