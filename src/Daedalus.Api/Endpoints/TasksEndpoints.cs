using Daedalus.Agents.Security;
using Daedalus.Api.Agents;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.CreateTask;
using Daedalus.Application.Commands.DeleteTask;
using Daedalus.Application.Commands.UpdateTask;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Endpoints;

/// <summary>API endpoints for accessing task data.</summary>
/// <remarks>
///     Ported from the MVC controller of <c>api/tasks</c> on the pattern of <see cref="ProjectsEndpoints"/> (R42, R45, R46). The
///     controller carried a bare <c>[Authorize]</c> at class level and a policy per action, so the group requires an
///     authenticated user and each route adds its policy. It had no <c>[Produces]</c>, so the group carries no
///     <see cref="ProblemContentType"/>: problem bodies stay <c>application/problem+json</c>, and every declared
///     response lists MVC's three output media types, <c>application/json</c>, <c>text/json</c> and
///     <c>text/plain</c> (R51). The <c>{ "error" }</c> bodies go out through <see cref="ApiJson.Of"/> as
///     <c>application/json</c>, as MVC wrote the anonymous objects; <c>Problem</c>, <c>Unauthorized()</c> and the
///     unattached-run problems go out through <see cref="ApiProblem.Of"/>.
/// </remarks>
public static partial class TasksEndpoints
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 10;

    /// <summary>The media types MVC's output formatters declared for every response of a controller with no <c>[Produces]</c>.</summary>
    private static readonly string[] MvcFormats = ["application/json", "text/json", "text/plain"];

    /// <summary>Maps <c>api/tasks</c>, authenticated, with a policy per route and a rate limit on the writes.</summary>
    public static RouteGroupBuilder MapTasksEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/tasks")
            .RequireAuthorization()
            .WithTags("Tasks");

        group.MapGet(string.Empty, GetAllTasks)
            .RequireAuthorization("TaskRead")
            .WithQueryParameter("page", DefaultPage)
            .WithQueryParameter("pageSize", DefaultPageSize)
            .Declares<PagedResultDto<TaskDto>>(StatusCodes.Status200OK);

        group.MapGet("{id:guid}", GetTaskById)
            .RequireAuthorization("TaskRead")
            .Declares<TaskDto>(StatusCodes.Status200OK)
            .Declares<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapPost(string.Empty, CreateTask)
            .RequireAuthorization("TaskManagement")
            .RequireRateLimiting("write-operations")
            .Declares<TaskDto>(StatusCodes.Status201Created)
            .Declares<ProblemDetails>(StatusCodes.Status400BadRequest);

        group.MapPut("{id:guid}", UpdateTask)
            .RequireAuthorization("TaskManagement")
            .RequireRateLimiting("write-operations")
            .Declares<TaskDto>(StatusCodes.Status200OK)
            .Declares<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Declares<ProblemDetails>(StatusCodes.Status409Conflict)
            .Declares<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapDelete("{id:guid}", DeleteTask)
            .RequireAuthorization("TaskManagement")
            .RequireRateLimiting("write-operations")
            .Produces(StatusCodes.Status204NoContent)
            .Declares<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Declares<ProblemDetails>(StatusCodes.Status409Conflict)
            .Declares<ProblemDetails>(StatusCodes.Status404NotFound);

        group.MapPost("{id:guid}/manufacture", Manufacture)
            .RequireAuthorization("WorkflowResume")
            .RequireRateLimiting("write-operations")
            .Declares<StartWorkflowRunResponse>(StatusCodes.Status201Created)
            .Declares<ProblemDetails>(StatusCodes.Status400BadRequest)
            .Declares<ProblemDetails>(StatusCodes.Status401Unauthorized)
            .Declares<ProblemDetails>(StatusCodes.Status404NotFound)
            .Declares<ProblemDetails>(StatusCodes.Status409Conflict)
            .Declares<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)
            .Declares<ProblemDetails>(StatusCodes.Status500InternalServerError)
            .Declares<ProblemDetails>(StatusCodes.Status503ServiceUnavailable);

        return group;
    }

    /// <summary>Get all tasks with pagination.</summary>
    public static async Task<Results<Ok<PagedResultDto<TaskDto>>, ApiJsonResult<ErrorBody>>> GetAllTasks(
        ApiQuery<int> page, ApiQuery<int> pageSize, [FromServices] ITaskQueryService taskService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await taskService.GetAllAsync(
                page.ValueOr(DefaultPage), pageSize.ValueOr(DefaultPageSize), ct));
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingTasks(loggerFactory.CreateLogger(typeof(TasksEndpoints)), ex);
            return InternalServerError();
        }
    }

    /// <summary>Get a specific task by ID.</summary>
    public static async Task<Results<Ok<TaskDto>, ApiJsonResult<ErrorBody>>> GetTaskById(
        Guid id, [FromServices] ITaskQueryService taskService, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            var task = await taskService.GetByIdAsync(id, ct);
            if (task is null)
            {
                return TaskNotFound(id);
            }

            return TypedResults.Ok(task);
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingTask(loggerFactory.CreateLogger(typeof(TasksEndpoints)), id, ex);
            return InternalServerError();
        }
    }

    /// <summary>Create a new task.</summary>
    public static async Task<Results<Created<TaskDto>, ApiProblemResult>> CreateTask(
        ApiBody<CreateTaskDto> request, HttpContext context, [FromServices] IApplicationCommands commands,
        CancellationToken ct)
    {
        var dto = request.Value!;
        var command = new CreateTaskCommand(
            dto.ProjectId,
            dto.TaskId ?? $"TASK-{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}",
            dto.Title,
            dto.Description,
            (Priority)dto.Priority,
            dto.Phase ?? "Phase-1",
            dto.ParallelGroup,
            (Complexity)dto.EstimatedComplexity,
            dto.Prompt);

        var result = await commands.CreateTaskAsync(command, ct);

        return result.IsSuccess
            ? TypedResults.Created(ApiLocation.For(context.Request, $"/api/Tasks/{result.Value.Id}"), result.Value)
            : ApiProblem.Of(context, StatusCodes.Status400BadRequest, result.Error);
    }

    /// <summary>Update a task's metadata.</summary>
    public static async Task<Results<Ok<TaskDto>, ApiJsonResult<ErrorBody>>> UpdateTask(
        Guid id, ApiBody<UpdateTaskDto> request, [FromServices] IApplicationCommands commands, CancellationToken ct)
    {
        var dto = request.Value!;
        var command = new UpdateTaskCommand(
            id,
            dto.Title,
            dto.Description,
            dto.Priority.HasValue ? (Priority)dto.Priority.Value : null,
            dto.Phase,
            dto.ParallelGroup,
            dto.EstimatedComplexity.HasValue ? (Complexity)dto.EstimatedComplexity.Value : null,
            dto.Prompt);

        var result = await commands.UpdateTaskAsync(command, ct);

        if (result.IsSuccess)
        {
            return TypedResults.Ok(result.Value);
        }

        if (TaskRunGuard.IsConflict(result.Error))
        {
            return Error(result.Error, StatusCodes.Status409Conflict);
        }

        return Error(result.Error, IsNotFound(result.Error) ? StatusCodes.Status404NotFound : StatusCodes.Status400BadRequest);
    }

    /// <summary>Delete a task.</summary>
    public static async Task<Results<NoContent, ApiJsonResult<ErrorBody>>> DeleteTask(
        Guid id, [FromServices] IApplicationCommands commands, CancellationToken ct)
    {
        var result = await commands.DeleteTaskAsync(new DeleteTaskCommand(id), ct);

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

    /// <summary>
    ///     Starts a manufacture run for the task (phase 2.8). Same authorization as <c>POST /api/workflow-runs</c>. It answers:
    ///     <list type="bullet">
    ///         <item>404 when the task does not exist;</item>
    ///         <item>422 when its project's repository is not allow-listed, or a dependency is not Completed;</item>
    ///         <item>409 while its current run is live, or when the task changed after it was read and the run that
    ///         started could not be attached; that run is cancelled, and the body says whether it was;</item>
    ///         <item>201 with the run id once the run is started and attached.</item>
    ///     </list>
    ///     A starter failure maps as <c>POST /api/workflow-runs</c> maps it.
    /// </summary>
    public static async Task<Results<Created<StartWorkflowRunResponse>, ApiProblemResult>> Manufacture(
        Guid id, HttpContext context, [FromServices] TaskManufactureService manufacture, CancellationToken ct)
    {
        if (!HttpSecurityContextFactory.TryCreate(context.User, out var caller))
        {
            return ApiProblem.Of(context, StatusCodes.Status401Unauthorized);
        }

        var startedBy = RunPrincipals.From(caller, context.User.FindFirst("preferred_username")?.Value);
        var result = await manufacture.StartAsync(id, startedBy, ct);
        if (result.IsSuccess)
        {
            return TypedResults.Created($"/api/workflow-runs/{result.Value}", new StartWorkflowRunResponse(result.Value));
        }

        var failure = result.Error;
        switch (failure)
        {
            case { Kind: TaskManufactureFailureKind.TaskNotFound }:
                return ApiProblem.Of(context, StatusCodes.Status404NotFound, failure.Message);
            case { Kind: TaskManufactureFailureKind.RepositoryNotAllowed or TaskManufactureFailureKind.DependencyNotCompleted }:
                return ApiProblem.Of(context, StatusCodes.Status422UnprocessableEntity, failure.Message);
            case { Kind: TaskManufactureFailureKind.RunLive }:
                return ApiProblem.Of(context, StatusCodes.Status409Conflict, failure.Message);
            case { Kind: TaskManufactureFailureKind.StartFailed, Start: { } start }:
                return ManufactureStartProblem.From(context, start);
            case { Kind: TaskManufactureFailureKind.AttachConflict, RunId: { } lostRun }:
                return UnattachedRunProblem(context, StatusCodes.Status409Conflict, failure, lostRun);
            case { Kind: TaskManufactureFailureKind.AttachFailed, RunId: { } unattached }:
                return UnattachedRunProblem(context, StatusCodes.Status500InternalServerError, failure, unattached);
            default:
                return ApiProblem.Of(context, StatusCodes.Status500InternalServerError, failure.Message);
        }
    }

    /// <summary>
    ///     A problem for a run that started but is not attached: the detail names the run and the cancel outcome, and the
    ///     <c>runId</c> and <c>runCancelled</c> extensions carry both for a client.
    /// </summary>
    private static ApiProblemResult UnattachedRunProblem(
        HttpContext context, int statusCode, TaskManufactureFailure failure, Guid runId) =>
        ApiProblem.Of(context, statusCode, failure.Message, extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["runId"] = runId,
            ["runCancelled"] = failure.RunCancelled,
        });

    /// <summary>
    ///     Declares <paramref name="statusCode"/> with <typeparamref name="T"/> in MVC's three output media types, as MVC's
    ///     API explorer described every response of a controller with no <c>[Produces]</c> (R51).
    /// </summary>
    private static RouteHandlerBuilder Declares<T>(this RouteHandlerBuilder builder, int statusCode) =>
        builder.Produces<T>(statusCode, MvcFormats[0], MvcFormats[1..]);

    private static bool IsNotFound(string error) => error.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static ApiJsonResult<ErrorBody> Error(string error, int statusCode) =>
        ApiJson.Of(new ErrorBody(error), statusCode);

    private static ApiJsonResult<ErrorBody> TaskNotFound(Guid id) =>
        Error($"Task with ID {id} not found", StatusCodes.Status404NotFound);

    private static ApiJsonResult<ErrorBody> InternalServerError() =>
        Error("Internal server error", StatusCodes.Status500InternalServerError);

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Error retrieving tasks")]
        public static partial void ErrorRetrievingTasks(ILogger logger, Exception ex);

        [LoggerMessage(EventId = 101, Level = LogLevel.Error, Message = "Error retrieving task {TaskId}")]
        public static partial void ErrorRetrievingTask(ILogger logger, Guid taskId, Exception ex);
    }
}
