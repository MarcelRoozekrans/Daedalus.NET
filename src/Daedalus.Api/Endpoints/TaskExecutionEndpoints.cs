using Daedalus.Api.Services;
using Daedalus.Application.DTOs;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Endpoints;

/// <summary>
///     Read access to the executions of one task or one agent session, a page at a time. Both routes call
///     straight through to <see cref="ITaskExecutionQueryService"/>; paging decisions belong to the service.
/// </summary>
/// <remarks>
///     Ported from <c>TaskExecutionsController</c> on the pattern of <see cref="AgentEndpoints"/> (R38). The controller
///     carried a bare <c>[Authorize]</c> at class level and the <c>TaskRead</c> policy per action, so the group
///     requires an authenticated user and each route adds the policy; the snapshot renders both. The 500 is a
///     <see cref="JsonHttpResult{TValue}"/>, which adds no response metadata, so the declared responses stay the
///     controller's single 200 (R42).
/// </remarks>
public static partial class TaskExecutionEndpoints
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 10;

    /// <summary>Maps <c>api/taskexecutions</c>, authenticated, with policy <c>TaskRead</c> on both routes.</summary>
    public static RouteGroupBuilder MapTaskExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/taskexecutions")
            .RequireAuthorization()
            .WithTags("TaskExecutions")
            .WithMetadata(ProblemContentType.Json);

        group.MapGet("task/{taskId:guid}", GetByTaskId)
            .RequireAuthorization("TaskRead")
            .WithQueryParameter("page", DefaultPage)
            .WithQueryParameter("pageSize", DefaultPageSize);

        group.MapGet("session/{sessionId:guid}", GetBySessionId)
            .RequireAuthorization("TaskRead")
            .WithQueryParameter("page", DefaultPage)
            .WithQueryParameter("pageSize", DefaultPageSize);

        return group;
    }

    /// <summary>Get all executions for a specific task.</summary>
    public static async Task<Results<Ok<PagedResultDto<TaskExecutionDto>>, JsonHttpResult<ErrorBody>>> GetByTaskId(
        Guid taskId, ApiQuery<int> page, ApiQuery<int> pageSize,
        [FromServices] ITaskExecutionQueryService executionService, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await executionService.GetByTaskIdAsync(
                taskId, page.ValueOr(DefaultPage), pageSize.ValueOr(DefaultPageSize), ct));
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingExecutionsForTask(CreateLogger(loggerFactory), taskId, ex);
            return InternalServerError();
        }
    }

    /// <summary>Get all executions for a specific session.</summary>
    public static async Task<Results<Ok<PagedResultDto<TaskExecutionDto>>, JsonHttpResult<ErrorBody>>> GetBySessionId(
        Guid sessionId, ApiQuery<int> page, ApiQuery<int> pageSize,
        [FromServices] ITaskExecutionQueryService executionService, [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await executionService.GetBySessionIdAsync(
                sessionId, page.ValueOr(DefaultPage), pageSize.ValueOr(DefaultPageSize), ct));
        }
        catch (Exception ex)
        {
            Log.ErrorRetrievingExecutionsForSession(CreateLogger(loggerFactory), sessionId, ex);
            return InternalServerError();
        }
    }

    // A static class cannot be a type argument of ILogger<T>, so the category is created from the class name (R42).
    private static ILogger CreateLogger(ILoggerFactory loggerFactory) =>
        loggerFactory.CreateLogger(typeof(TaskExecutionEndpoints).FullName!);

    private static JsonHttpResult<ErrorBody> InternalServerError() =>
        TypedResults.Json(new ErrorBody("Internal server error"), statusCode: StatusCodes.Status500InternalServerError);

    private static partial class Log
    {
        [LoggerMessage(EventId = 100, Level = LogLevel.Error, Message = "Error retrieving executions for task {TaskId}")]
        public static partial void ErrorRetrievingExecutionsForTask(ILogger logger, Guid taskId, Exception ex);

        [LoggerMessage(EventId = 101, Level = LogLevel.Error,
            Message = "Error retrieving executions for session {SessionId}")]
        public static partial void ErrorRetrievingExecutionsForSession(ILogger logger, Guid sessionId, Exception ex);
    }
}
