using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.CreateProject;
using Daedalus.Application.Commands.DeleteProject;
using Daedalus.Application.Commands.UpdateProject;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Daedalus.Api.Controllers;

/// <summary>API endpoints for accessing project data.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Authorize]
[Produces("application/json")]
public sealed partial class ProjectsController(
    IProjectQueryService projectService,
    ICommandHandlerFactory commandFactory,
    ILogger<ProjectsController> logger)
    : ControllerBase
{
    [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Error retrieving projects")]
    private static partial void LogErrorRetrievingProjects(ILogger logger, Exception ex);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Error retrieving project {ProjectId}")]
    private static partial void LogErrorRetrievingProject(ILogger logger, Guid projectId, Exception ex);

    [LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "Error retrieving project {ProjectId} with tasks")]
    private static partial void LogErrorRetrievingProjectWithTasks(ILogger logger, Guid projectId, Exception ex);

    /// <summary>Get all projects with pagination.</summary>
    [Authorize(Policy = "ProjectRead")]
    [HttpGet]
    [ProducesResponseType(typeof(PagedResultDto<ProjectDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAllProjects([FromQuery] int page = 1, [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        try
        {
            var result = await projectService.GetAllAsync(page, pageSize, ct);
            return Ok(result);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingProjects(logger, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get a specific project by ID.</summary>
    [Authorize(Policy = "ProjectRead")]
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProjectById(Guid id, CancellationToken ct = default)
    {
        try
        {
            var project = await projectService.GetByIdAsync(id, ct);
            if (project is null)
            {
                return NotFound(new { error = $"Project with ID {id} not found" });
            }

            return Ok(project);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingProject(logger, id, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Get a project with its tasks.</summary>
    [Authorize(Policy = "ProjectRead")]
    [HttpGet("{id:guid}/with-tasks")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProjectWithTasks(Guid id, CancellationToken ct = default)
    {
        try
        {
            var project = await projectService.GetWithTasksAsync(id, ct);
            if (project is null)
            {
                return NotFound(new { error = $"Project with ID {id} not found" });
            }

            return Ok(project);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingProjectWithTasks(logger, id, ex);
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "Internal server error" });
        }
    }

    /// <summary>Create a new project.</summary>
    [Authorize(Policy = "ProjectManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpPost]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateProject([FromBody] CreateProjectDto request,
        CancellationToken ct = default)
    {
        var command = new CreateProjectCommand(
            request.ProjectName,
            request.Description ?? string.Empty,
            request.Version ?? "1.0");

        var handler = commandFactory.GetHandler<CreateProjectCommand, Result<ProjectDto>>(command);
        var result = await handler.Handle(command, ct);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetProjectById), new { id = result.Value.Id }, result.Value)
            : BadRequest(new { error = result.Error });
    }

    /// <summary>Update an existing project.</summary>
    [Authorize(Policy = "ProjectManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ProjectDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateProject(Guid id, [FromBody] UpdateProjectDto request,
        CancellationToken ct = default)
    {
        var command = new UpdateProjectCommand(
            id,
            request.ProjectName,
            request.Description,
            request.Version);

        var handler = commandFactory.GetHandler<UpdateProjectCommand, Result<ProjectDto>>(command);
        var result = await handler.Handle(command, ct);

        if (result.IsSuccess)
        {
            return Ok(result.Value);
        }

        return result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? NotFound(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }

    /// <summary>Delete a project.</summary>
    [Authorize(Policy = "ProjectManagement")]
    [EnableRateLimiting("write-operations")]
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProject(Guid id, CancellationToken ct = default)
    {
        var command = new DeleteProjectCommand(id);
        var handler = commandFactory.GetHandler<DeleteProjectCommand, Result>(command);
        var result = await handler.Handle(command, ct);

        if (result.IsSuccess)
        {
            return NoContent();
        }

        return result.Error.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? NotFound(new { error = result.Error })
            : BadRequest(new { error = result.Error });
    }
}
