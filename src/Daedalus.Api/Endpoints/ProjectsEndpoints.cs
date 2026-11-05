using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.CreateProject;
using Daedalus.Application.Commands.DeleteProject;
using Daedalus.Application.Commands.UpdateProject;
using Daedalus.Application.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Endpoints;

/// <summary>Project reads over <see cref="IProjectQueryService"/> and project writes over <see cref="IApplicationCommands"/>.</summary>
/// <remarks>
///     Ported from <c>ProjectsController</c> on the pattern of <see cref="TaskExecutionEndpoints"/> (R42, R45). The
///     controller carried a bare <c>[Authorize]</c> at class level and a policy per action, so the group requires an
///     authenticated user and each route adds its policy. Every failure answers an <c>{ "error" }</c> body, including
///     the 404 and the 400, through <see cref="ApiJson.Of"/> (R45); the declared 400, 404 and 409 are
///     <c>ProducesProblem</c> because MVC declared <c>ProblemDetails</c> and wrote the anonymous object. The 201's
///     <c>Location</c> keeps the class-name casing MVC's <c>CreatedAtAction</c> produced, <c>/api/Projects/{id}</c> (R21).
/// </remarks>
public static partial class ProjectsEndpoints
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 10;

    /// <summary>Maps <c>api/projects</c>, authenticated, with a policy per route and a rate limit on the writes.</summary>
    public static RouteGroupBuilder MapProjectsEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/projects")
            .RequireAuthorization()
            .WithTags("Projects")
            .WithMetadata(ProblemContentType.Json);

        group.MapGet(string.Empty, GetAllProjects)
            .RequireAuthorization("ProjectRead")
            .WithQueryParameter("page", DefaultPage)
            .WithQueryParameter("pageSize", DefaultPageSize);

        group.MapGet("{id:guid}", GetProjectById)
            .RequireAuthorization("ProjectRead")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        group.MapGet("{id:guid}/with-tasks", GetProjectWithTasks)
            .RequireAuthorization("ProjectRead")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        group.MapPost(string.Empty, CreateProject)
            .RequireAuthorization("ProjectManagement")
            .RequireRateLimiting("write-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json");

        group.MapPut("{id:guid}", UpdateProject)
            .RequireAuthorization("ProjectManagement")
            .RequireRateLimiting("write-operations")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapDelete("{id:guid}", DeleteProject)
            .RequireAuthorization("ProjectManagement")
            .RequireRateLimiting("write-operations")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        return group;
    }

    /// <summary>Get all projects with pagination.</summary>
    public static async Task<Results<Ok<PagedResultDto<ProjectDto>>, ApiJsonResult<ErrorBody>>> GetAllProjects(
        ApiQuery<int> page, ApiQuery<int> pageSize, [FromServices] IProjectQueryService projectService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await projectService.GetAllAsync(
                page.ValueOr(DefaultPage), pageSize.ValueOr(DefaultPageSize), ct));
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingProjects(loggerFactory.CreateLogger(typeof(ProjectsEndpoints)), ex);
            return InternalServerError();
        }
    }

    /// <summary>Get a specific project by ID.</summary>
    public static async Task<Results<Ok<ProjectDto>, ApiJsonResult<ErrorBody>>> GetProjectById(
        Guid id, [FromServices] IProjectQueryService projectService, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            var project = await projectService.GetByIdAsync(id, ct);
            return project is null ? ProjectNotFound(id) : TypedResults.Ok(project);
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingProject(loggerFactory.CreateLogger(typeof(ProjectsEndpoints)), id, ex);
            return InternalServerError();
        }
    }

    /// <summary>Get a project with its tasks.</summary>
    public static async Task<Results<Ok<ProjectDto>, ApiJsonResult<ErrorBody>>> GetProjectWithTasks(
        Guid id, [FromServices] IProjectQueryService projectService, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            var project = await projectService.GetWithTasksAsync(id, ct);
            return project is null ? ProjectNotFound(id) : TypedResults.Ok(project);
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingProjectWithTasks(loggerFactory.CreateLogger(typeof(ProjectsEndpoints)), id, ex);
            return InternalServerError();
        }
    }

    /// <summary>Create a new project.</summary>
    public static async Task<Results<Created<ProjectDto>, ApiJsonResult<ErrorBody>>> CreateProject(
        ApiBody<CreateProjectDto> request, HttpContext context, [FromServices] IApplicationCommands commands,
        CancellationToken ct)
    {
        var body = request.Value!;
        var command = new CreateProjectCommand(
            body.ProjectName,
            body.Description ?? string.Empty,
            body.Version ?? "1.0",
            body.RepositoryUrl,
            body.DefaultBranch);

        var result = await commands.CreateProjectAsync(command, ct);

        return result.IsSuccess
            ? TypedResults.Created(ApiLocation.For(context.Request, $"/api/Projects/{result.Value.Id}"), result.Value)
            : Error(result.Error, StatusCodes.Status400BadRequest);
    }

    /// <summary>Update an existing project. 409 when the save lost a race to another write of the project (R74).</summary>
    public static async Task<Results<Ok<ProjectDto>, ApiJsonResult<ErrorBody>>> UpdateProject(
        Guid id, ApiBody<UpdateProjectDto> request, [FromServices] IApplicationCommands commands, CancellationToken ct)
    {
        var body = request.Value!;
        var command = new UpdateProjectCommand(id, body.ProjectName, body.Description, body.Version);

        var result = await commands.UpdateProjectAsync(command, ct);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        if (SaveConflict.IsConflict(result.Error))
        {
            return Error(result.Error, StatusCodes.Status409Conflict);
        }

        return Error(result.Error, IsNotFound(result.Error) ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
    }

    /// <summary>Delete a project and its tasks. 409 while any of its tasks has a live manufacture run (amendment A7).</summary>
    public static async Task<Results<NoContent, ApiJsonResult<ErrorBody>>> DeleteProject(
        Guid id, [FromServices] IApplicationCommands commands, CancellationToken ct)
    {
        var result = await commands.DeleteProjectAsync(new DeleteProjectCommand(id), ct);

        if (result.IsSuccess)
        {
            return TypedResults.NoContent();
        }

        if (TaskRunGuard.IsConflict(result.Error))
        {
            return Error(result.Error, StatusCodes.Status409Conflict);
        }

        return Error(result.Error, IsNotFound(result.Error) ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
    }

    private static bool IsNotFound(string error) => error.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static ApiJsonResult<ErrorBody> Error(string error, int statusCode) =>
        ApiJson.Of(new ErrorBody(error), statusCode);

    private static ApiJsonResult<ErrorBody> ProjectNotFound(Guid id) =>
        Error($"Project with ID {id} not found", StatusCodes.Status404NotFound);

    private static ApiJsonResult<ErrorBody> InternalServerError() =>
        Error("Internal server error", StatusCodes.Status500InternalServerError);

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Error retrieving projects")]
        public static partial void ErrorRetrievingProjects(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Error retrieving project {ProjectId}")]
        public static partial void ErrorRetrievingProject(ILogger logger, Guid projectId, Exception ex);

        [LoggerMessage(EventId = 102, Level = LogLevel.Error, Message = "Error retrieving project {ProjectId} with tasks")]
        public static partial void ErrorRetrievingProjectWithTasks(ILogger logger, Guid projectId, Exception ex);
    }
}
