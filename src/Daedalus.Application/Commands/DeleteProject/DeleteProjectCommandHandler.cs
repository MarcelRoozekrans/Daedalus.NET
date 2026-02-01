using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Commands.DeleteProject;

/// <summary>
///     Handler for deleting a project.
/// </summary>
public sealed partial class DeleteProjectCommandHandler(
    IProjectRepository projectRepository,
    ILogger<DeleteProjectCommandHandler> logger) : ICommandHandler<DeleteProjectCommand, Result>
{
    public async Task<Result> Handle(DeleteProjectCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var projectResult = await projectRepository.GetByIdAsync(command.Id, cancellationToken)
                .ConfigureAwait(false);

            if (projectResult.IsFailure)
            {
                return Result.Failure($"Project {command.Id} not found");
            }

            var deleteResult = await projectRepository.DeleteAsync(command.Id, cancellationToken)
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
            logger.LogError(ex, "Unexpected error deleting project {ProjectId}", command.Id);
            return Result.Failure($"Error deleting project: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Failed to delete project: {Error}")]
    private static partial void LogDeleteProjectFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Project {ProjectId} deleted successfully")]
    private static partial void LogProjectDeleted(ILogger logger, Guid projectId);
}
