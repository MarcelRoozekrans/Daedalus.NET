using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Commands.UpdateProject;

/// <summary>
///     Handler for updating an existing project.
/// </summary>
public sealed partial class UpdateProjectCommandHandler(
    IProjectRepository projectRepository,
    ILogger<UpdateProjectCommandHandler> logger) : ICommandHandler<UpdateProjectCommand, Result<ProjectDto>>
{
    public async Task<Result<ProjectDto>> Handle(UpdateProjectCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var projectResult = await projectRepository.GetByIdAsync(command.Id, cancellationToken)
                .ConfigureAwait(false);

            if (projectResult.IsFailure)
            {
                return Result.Failure<ProjectDto>($"Project {command.Id} not found");
            }

            var project = projectResult.Value;

            // Use domain method to update metadata with validation
            var projectName = !string.IsNullOrWhiteSpace(command.ProjectName)
                ? command.ProjectName.Trim()
                : project.ProjectName;

            var description = command.Description is not null
                ? command.Description.Trim()
                : project.Description;

            var metadataResult = project.UpdateMetadata(projectName, description);
            if (metadataResult.IsFailure)
            {
                return Result.Failure<ProjectDto>(metadataResult.Error);
            }

            // Update version if provided
            if (!string.IsNullOrWhiteSpace(command.Version))
            {
                var versionResult = project.UpdateVersion(command.Version);
                if (versionResult.IsFailure)
                {
                    return Result.Failure<ProjectDto>(versionResult.Error);
                }
            }

            var updateResult = await projectRepository.UpdateAsync(project, cancellationToken)
                .ConfigureAwait(false);

            if (updateResult.IsFailure)
            {
                LogUpdateProjectFailed(logger, updateResult.Error);
                return Result.Failure<ProjectDto>(updateResult.Error);
            }

            var dto = new ProjectDto(
                project.Id,
                project.ProjectName,
                project.Description,
                project.Version,
                project.RepositoryUrl,
                project.DefaultBranch,
                project.CreatedAt,
                project.ModifiedAt,
                new List<TaskDto>());

            LogProjectUpdated(logger, project.Id);
            return Result.Success(dto);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error updating project {ProjectId}", command.Id);
            return Result.Failure<ProjectDto>($"Error updating project: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Failed to update project: {Error}")]
    private static partial void LogUpdateProjectFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Project {ProjectId} updated successfully")]
    private static partial void LogProjectUpdated(ILogger logger, Guid projectId);
}
