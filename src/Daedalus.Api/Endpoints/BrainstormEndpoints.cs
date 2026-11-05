using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Daedalus.Application.Services;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Daedalus.Api.Endpoints;

/// <summary>Interactive brainstorming sessions over <see cref="IBrainstormService"/>.</summary>
/// <remarks>
///     Ported from <c>BrainstormController</c> on the pattern of <see cref="ProjectsEndpoints"/> (R42, R45). The group
///     requires an authenticated user and each route adds its policy and, for four of them, a rate limit. Every
///     failure answers an <c>{ "error" }</c> body through <see cref="ApiJson.Of"/> (R45); the declared 400 and 404 are
///     <c>ProducesProblem</c> because MVC declared <c>ProblemDetails</c> and wrote the anonymous object. The
///     controller had no catch-all, so neither do the handlers (R47). A write whose session save lost a race to another
///     write answers 409 with the refusal, matched by <see cref="SaveConflict.IsConflict"/>, as projects do (R83).
/// </remarks>
public static partial class BrainstormEndpoints
{
    /// <summary>Maps <c>api/brainstorm</c>, authenticated, with a policy per route and a rate limit on four.</summary>
    public static RouteGroupBuilder MapBrainstormEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var group = app.MapApiGroup("api/brainstorm")
            .RequireAuthorization()
            .WithTags("Brainstorm")
            .WithMetadata(ProblemContentType.Json);

        group.MapPost("sessions", CreateSession)
            .RequireAuthorization("CodeAnalysis")
            .RequireRateLimiting("llm-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapGet("sessions/{sessionId:guid}", GetSession)
            .RequireAuthorization("CodeAnalysisRead")
            .ProducesProblem(StatusCodes.Status404NotFound, "application/json");

        group.MapGet("sessions", GetSessions)
            .RequireAuthorization("CodeAnalysisRead")
            .WithQueryParameter<Guid>("projectId");

        group.MapPost("sessions/{sessionId:guid}/messages", SendMessage)
            .RequireAuthorization("CodeAnalysis")
            .RequireRateLimiting("llm-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapPost("sessions/{sessionId:guid}/advance", AdvancePhase)
            .RequireAuthorization("CodeAnalysis")
            .RequireRateLimiting("llm-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapPost("sessions/{sessionId:guid}/abandon", AbandonSession)
            .RequireAuthorization("CodeAnalysis")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        group.MapPost("sessions/{sessionId:guid}/generate-tasks", GenerateTasks)
            .RequireAuthorization("TaskManagement")
            .RequireRateLimiting("write-operations")
            .ProducesProblem(StatusCodes.Status400BadRequest, "application/json")
            .ProducesProblem(StatusCodes.Status409Conflict, "application/json");

        return group;
    }

    /// <summary>Create a new brainstorm session for a project.</summary>
    public static async Task<Results<Ok<BrainstormSessionDto>, ApiJsonResult<ErrorBody>>> CreateSession(
        ApiBody<CreateBrainstormSessionDto> request, [FromServices] IBrainstormService brainstormService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var projectId = request.Value!.ProjectId;
        var logger = loggerFactory.CreateLogger(typeof(BrainstormEndpoints));
        Log.CreatingSession(logger, projectId);

        var result = await brainstormService.CreateSessionAsync(projectId, ct);

        return result.IsSuccess
            ? TypedResults.Ok(BrainstormDtoMapper.ToDto(result.Value))
            : WriteFailure(result.Error);
    }

    /// <summary>Get a brainstorm session by ID including all messages.</summary>
    public static async Task<Results<Ok<BrainstormSessionDto>, ApiJsonResult<ErrorBody>>> GetSession(
        Guid sessionId, [FromServices] IBrainstormService brainstormService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(BrainstormEndpoints));
        Log.RetrievingSession(logger, sessionId);

        var result = await brainstormService.GetSessionAsync(sessionId, ct);

        return result.IsSuccess
            ? TypedResults.Ok(BrainstormDtoMapper.ToDto(result.Value))
            : Error(result.Error, StatusCodes.Status404NotFound);
    }

    /// <summary>Get all brainstorm sessions for a project.</summary>
    public static async Task<Results<Ok<IReadOnlyList<BrainstormSessionSummaryDto>>, ApiJsonResult<ErrorBody>>> GetSessions(
        ApiQuery<Guid> projectId, [FromServices] IBrainstormService brainstormService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var id = projectId.ValueOr(Guid.Empty);
        var logger = loggerFactory.CreateLogger(typeof(BrainstormEndpoints));
        Log.RetrievingSessions(logger, id);

        var result = await brainstormService.GetSessionsByProjectAsync(id, ct);

        if (result.IsFailure)
        {
            return Error(result.Error, StatusCodes.Status400BadRequest);
        }

        IReadOnlyList<BrainstormSessionSummaryDto> summaries = result.Value
            .Select(BrainstormDtoMapper.ToSummaryDto)
            .ToList();

        return TypedResults.Ok(summaries);
    }

    /// <summary>Send a user message to a brainstorm session.</summary>
    public static async Task<Results<Ok<BrainstormMessageDto>, ApiJsonResult<ErrorBody>>> SendMessage(
        Guid sessionId, ApiBody<SendBrainstormMessageDto> request, [FromServices] IBrainstormService brainstormService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(BrainstormEndpoints));
        Log.SendingMessage(logger, sessionId);

        var result = await brainstormService.SendMessageAsync(sessionId, request.Value!.Content, ct);

        return result.IsSuccess
            ? TypedResults.Ok(BrainstormDtoMapper.ToDto(result.Value))
            : WriteFailure(result.Error);
    }

    /// <summary>Advance the brainstorm session to the next phase.</summary>
    public static async Task<Results<Ok<BrainstormSessionDto>, ApiJsonResult<ErrorBody>>> AdvancePhase(
        Guid sessionId, [FromServices] IBrainstormService brainstormService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(BrainstormEndpoints));
        Log.AdvancingPhase(logger, sessionId);

        var result = await brainstormService.AdvancePhaseAsync(sessionId, ct);

        return result.IsSuccess
            ? TypedResults.Ok(BrainstormDtoMapper.ToDto(result.Value))
            : WriteFailure(result.Error);
    }

    /// <summary>Abandon a brainstorm session.</summary>
    public static async Task<Results<NoContent, ApiJsonResult<ErrorBody>>> AbandonSession(
        Guid sessionId, [FromServices] IBrainstormService brainstormService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(BrainstormEndpoints));
        Log.AbandoningSession(logger, sessionId);

        var result = await brainstormService.AbandonSessionAsync(sessionId, ct);

        return result.IsSuccess
            ? TypedResults.NoContent()
            : WriteFailure(result.Error);
    }

    /// <summary>Generate tasks from a brainstorm session's implementation plan.</summary>
    public static async Task<Results<Ok<List<TaskDto>>, ApiJsonResult<ErrorBody>>> GenerateTasks(
        Guid sessionId, [FromServices] IBrainstormService brainstormService,
        [FromServices] ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(BrainstormEndpoints));
        Log.GeneratingTasks(logger, sessionId);

        var result = await brainstormService.GenerateTasksAsync(sessionId, ct);

        return result.IsSuccess
            ? TypedResults.Ok(result.Value)
            : WriteFailure(result.Error);
    }

    /// <summary>A failed write: 409 when its session save lost a race to another write (R83), else 400.</summary>
    private static ApiJsonResult<ErrorBody> WriteFailure(string error) =>
        Error(error, SaveConflict.IsConflict(error) ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest);

    private static ApiJsonResult<ErrorBody> Error(string error, int statusCode) =>
        ApiJson.Of(new ErrorBody(error), statusCode);

    private static partial class Log
    {
        [LoggerMessage(EventId = 200, Level = LogLevel.Information,
            Message = "Creating brainstorm session for project {ProjectId}")]
        public static partial void CreatingSession(ILogger logger, Guid projectId);

        [LoggerMessage(EventId = 201, Level = LogLevel.Information,
            Message = "Retrieving brainstorm session {SessionId}")]
        public static partial void RetrievingSession(ILogger logger, Guid sessionId);

        [LoggerMessage(EventId = 202, Level = LogLevel.Information,
            Message = "Retrieving brainstorm sessions for project {ProjectId}")]
        public static partial void RetrievingSessions(ILogger logger, Guid projectId);

        [LoggerMessage(EventId = 203, Level = LogLevel.Information,
            Message = "Sending message to brainstorm session {SessionId}")]
        public static partial void SendingMessage(ILogger logger, Guid sessionId);

        [LoggerMessage(EventId = 204, Level = LogLevel.Information,
            Message = "Advancing phase for brainstorm session {SessionId}")]
        public static partial void AdvancingPhase(ILogger logger, Guid sessionId);

        [LoggerMessage(EventId = 205, Level = LogLevel.Information,
            Message = "Abandoning brainstorm session {SessionId}")]
        public static partial void AbandoningSession(ILogger logger, Guid sessionId);

        [LoggerMessage(EventId = 206, Level = LogLevel.Information,
            Message = "Generating tasks for brainstorm session {SessionId}")]
        public static partial void GeneratingTasks(ILogger logger, Guid sessionId);
    }
}
