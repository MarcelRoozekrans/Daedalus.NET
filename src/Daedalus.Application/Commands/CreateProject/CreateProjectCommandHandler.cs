using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Commands.CreateProject;

/// <summary>
///     Handler for creating a new project.
/// </summary>
public sealed partial class CreateProjectCommandHandler(
    IProjectRepository projectRepository,
    ILogger<CreateProjectCommandHandler> logger) : ICommandHandler<CreateProjectCommand, Result<ProjectDto>>
{
    public async Task<Result<ProjectDto>> Handle(CreateProjectCommand command, CancellationToken cancellationToken)
    {
        try
        {
            // Use factory method for proper domain encapsulation
            var createResult = Project.Create(
                Guid.NewGuid(),
                command.ProjectName,
                command.Description ?? string.Empty,
                command.Version ?? "1.0");

            if (createResult.IsFailure)
            {
                return Result.Failure<ProjectDto>(createResult.Error);
            }

            var project = createResult.Value;
            var addResult = await projectRepository.AddAsync(project, cancellationToken)
                .ConfigureAwait(false);

            if (addResult.IsFailure)
            {
                LogCreateProjectFailed(logger, addResult.Error);
                return Result.Failure<ProjectDto>(addResult.Error);
            }

            var dto = new ProjectDto(
                project.Id,
                project.ProjectName,
                project.Description,
                project.Version,
                project.CreatedAt,
                project.ModifiedAt,
                new List<TaskDto>());

            LogProjectCreated(logger, project.Id);
            return Result.Success(dto);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error creating project");
            return Result.Failure<ProjectDto>($"Error creating project: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Failed to create project: {Error}")]
    private static partial void LogCreateProjectFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 101, Level = LogLevel.Information, Message = "Project {ProjectId} created successfully")]
    private static partial void LogProjectCreated(ILogger logger, Guid projectId);
}
