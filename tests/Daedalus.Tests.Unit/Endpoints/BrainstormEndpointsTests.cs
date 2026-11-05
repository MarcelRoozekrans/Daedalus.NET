using System.Reflection;
using Daedalus.Api.Endpoints;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;
using static Daedalus.Tests.Unit.Endpoints.EndpointTestContext;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Endpoints;

/// <summary>
///     <see cref="BrainstormEndpoints"/> answers what <c>BrainstormController</c> answered: a successful service result
///     is a 200 carrying the mapped DTO (a 204 for an abandon), a failed one is a 400 <c>{ "error" }</c> (a 404 for a
///     missing session), and each call logs its information event before it calls the service.
/// </summary>
public sealed class BrainstormEndpointsTests
{
    private const string Category = "Daedalus.Api.Endpoints.BrainstormEndpoints";

    private static readonly Guid SessionId = Guid.NewGuid();
    private static readonly Guid ProjectId = Guid.NewGuid();

    private readonly IBrainstormService _service = Substitute.For<IBrainstormService>();

    [Fact]
    public async Task CreateSession_WithAProjectId_ReturnsTheMappedSessionAndLogs()
    {
        var session = BrainstormSession.Create(ProjectId).Value;
        _service.CreateSessionAsync(ProjectId, Arg.Any<CancellationToken>()).Returns(Result<BrainstormSession>.Success(session));
        var (factory, logger) = CreateLogging();
        var request = await BindCreate($"{{\"projectId\":\"{ProjectId}\"}}");

        var result = await BrainstormEndpoints.CreateSession(request, _service, factory, CancellationToken.None);

        // Red: answer Error("x", 400) in place of Ok in the success branch of CreateSession, or pass Guid.Empty in place
        //      of request.Value.ProjectId to CreateSessionAsync; the stub is not met and the result is not an Ok.
        var dto = result.Result.Should().BeOfType<Ok<BrainstormSessionDto>>().Subject.Value!;
        // Red: map BrainstormSession.Create(Guid.NewGuid()).Value in place of result.Value in CreateSession.
        dto.Id.Should().Be(session.Id);
        dto.ProjectId.Should().Be(ProjectId);
        // Red: drop LogCreatingSession, or change its EventId 200 to 207.
        LoggedEventIds(logger).Should().Equal(200);
        // Red: pass the category "Other" to CreateLogger in CreateSession.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task CreateSession_WhenTheServiceFails_Answers400CarryingTheError()
    {
        _service.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result<BrainstormSession>.Failure("Project not found"));
        var request = await BindCreate($"{{\"projectId\":\"{ProjectId}\"}}");

        var result = await BrainstormEndpoints.CreateSession(request, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: ignore result.IsSuccess in CreateSession; the result is Ok.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        // Red: answer 422 in place of 400 in the failure branch of CreateSession.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: change the error text passed to the ErrorBody in CreateSession.
        json.Value.Should().Be(new ErrorBody("Project not found"));
    }

    [Fact]
    public async Task GetSession_WithAnExistingId_ReturnsTheMappedSessionAndLogs()
    {
        var session = BrainstormSession.Create(ProjectId).Value;
        _service.GetSessionAsync(session.Id, Arg.Any<CancellationToken>()).Returns(Result<BrainstormSession>.Success(session));
        var (factory, logger) = CreateLogging();

        var result = await BrainstormEndpoints.GetSession(session.Id, _service, factory, CancellationToken.None);

        // Red: answer Error("x", 400) in place of Ok in the success branch of GetSession, or map
        //      BrainstormSession.Create(Guid.NewGuid()).Value in place of result.Value; the Id differs.
        result.Result.Should().BeOfType<Ok<BrainstormSessionDto>>().Which.Value!.Id.Should().Be(session.Id);
        // Red: drop LogRetrievingSession, or change its EventId 201 to 207.
        LoggedEventIds(logger).Should().Equal(201);
        // Red: pass the category "Other" to CreateLogger in GetSession.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GetSession_WhenTheServiceFails_Answers404CarryingTheError()
    {
        _service.GetSessionAsync(SessionId, Arg.Any<CancellationToken>())
            .Returns(Result<BrainstormSession>.Failure("Session not found"));

        var result = await BrainstormEndpoints.GetSession(SessionId, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: answer Error(result.Error, 200) in place of the 404 in the failure branch of GetSession; the status differs.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        // Red: answer 400 in place of 404 in the failure branch of GetSession.
        json.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        // Red: change the error text passed to the ErrorBody in GetSession.
        json.Value.Should().Be(new ErrorBody("Session not found"));
    }

    [Fact]
    public async Task GetSessions_ReturnsTheMappedSummariesAndLogs()
    {
        var first = BrainstormSession.Create(ProjectId).Value;
        var second = BrainstormSession.Create(ProjectId).Value;
        _service.GetSessionsByProjectAsync(ProjectId, Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<BrainstormSession>>.Success([first, second]));
        var (factory, logger) = CreateLogging();
        var projectId = await BindProjectId($"?projectId={ProjectId}");

        var result = await BrainstormEndpoints.GetSessions(projectId, _service, factory, CancellationToken.None);

        // Red: pass Guid.Empty in place of projectId.ValueOr in GetSessions; the stub for the project is not met and the result is not an Ok.
        var summaries = result.Result.Should().BeOfType<Ok<IReadOnlyList<BrainstormSessionSummaryDto>>>().Subject.Value!;
        // Red: append .Reverse() to the Select of ToSummaryDto in GetSessions; the order of the sessions flips.
        summaries.Select(summary => summary.Id).Should().Equal(first.Id, second.Id);
        // Red: drop LogRetrievingSessions, or change its EventId 202 to 207.
        LoggedEventIds(logger).Should().Equal(202);
        // Red: pass the category "Other" to CreateLogger in GetSessions.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GetSessions_WithoutAProjectId_AsksForTheEmptyGuid()
    {
        _service.GetSessionsByProjectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<BrainstormSession>>.Success([]));
        var projectId = await BindProjectId(string.Empty);

        await BrainstormEndpoints.GetSessions(projectId, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: use projectId.ValueOr(Guid.NewGuid()) in GetSessions; the service is asked for another project.
        await _service.Received(1).GetSessionsByProjectAsync(Guid.Empty, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSessions_WhenTheServiceFails_Answers400CarryingTheError()
    {
        _service.GetSessionsByProjectAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<BrainstormSession>>.Failure("Storage unavailable"));
        var projectId = await BindProjectId($"?projectId={ProjectId}");

        var result = await BrainstormEndpoints.GetSessions(projectId, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: drop the IsFailure check in GetSessions; reading result.Value of a failure throws.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        // Red: answer 404 in place of 400 in the failure branch of GetSessions.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: change the error text passed to the ErrorBody in GetSessions.
        json.Value.Should().Be(new ErrorBody("Storage unavailable"));
    }

    [Fact]
    public async Task SendMessage_WithValidContent_ReturnsTheMappedMessageAndLogs()
    {
        var message = BrainstormMessage.Create(SessionId, MessageRole.Assistant, "Response", BrainstormPhase.Clarification).Value;
        _service.SendMessageAsync(SessionId, "Hello", Arg.Any<CancellationToken>())
            .Returns(Result<BrainstormMessage>.Success(message));
        var (factory, logger) = CreateLogging();
        var request = await BindSend("{\"content\":\"Hello\"}");

        var result = await BrainstormEndpoints.SendMessage(SessionId, request, _service, factory, CancellationToken.None);

        // Red: answer Error("x", 400) in place of Ok in the success branch of SendMessage, or pass "" in place of
        //      request.Value.Content to SendMessageAsync; the stub is not met and the result is not an Ok.
        var dto = result.Result.Should().BeOfType<Ok<BrainstormMessageDto>>().Subject.Value!;
        // Red: map a BrainstormMessage with another content in place of result.Value in SendMessage.
        dto.Content.Should().Be("Response");
        // Red: drop LogSendingMessage, or change its EventId 203 to 207.
        LoggedEventIds(logger).Should().Equal(203);
        // Red: pass the category "Other" to CreateLogger in SendMessage.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task SendMessage_WhenTheServiceFails_Answers400CarryingTheError()
    {
        _service.SendMessageAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<BrainstormMessage>.Failure("Session is closed"));
        var request = await BindSend("{\"content\":\"Hello\"}");

        var result = await BrainstormEndpoints.SendMessage(
            SessionId, request, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: ignore result.IsSuccess in SendMessage; the result is Ok.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        // Red: answer 422 in place of 400 in the failure branch of SendMessage.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: change the error text passed to the ErrorBody in SendMessage.
        json.Value.Should().Be(new ErrorBody("Session is closed"));
    }

    [Fact]
    public async Task AdvancePhase_WhenSignaled_ReturnsTheMappedSessionAndLogs()
    {
        var session = BrainstormSession.Create(ProjectId).Value;
        _service.AdvancePhaseAsync(session.Id, Arg.Any<CancellationToken>()).Returns(Result<BrainstormSession>.Success(session));
        var (factory, logger) = CreateLogging();

        var result = await BrainstormEndpoints.AdvancePhase(session.Id, _service, factory, CancellationToken.None);

        // Red: answer Error("x", 400) in place of Ok in the success branch of AdvancePhase, or map
        //      BrainstormSession.Create(Guid.NewGuid()).Value in place of result.Value; the Id differs.
        result.Result.Should().BeOfType<Ok<BrainstormSessionDto>>().Which.Value!.Id.Should().Be(session.Id);
        // Red: drop LogAdvancingPhase, or change its EventId 204 to 207.
        LoggedEventIds(logger).Should().Equal(204);
        // Red: pass the category "Other" to CreateLogger in AdvancePhase.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task AdvancePhase_WhenTheServiceFails_Answers400CarryingTheError()
    {
        _service.AdvancePhaseAsync(SessionId, Arg.Any<CancellationToken>())
            .Returns(Result<BrainstormSession>.Failure("Phase not complete"));

        var result = await BrainstormEndpoints.AdvancePhase(SessionId, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: ignore result.IsSuccess in AdvancePhase; the result is Ok.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        // Red: answer 422 in place of 400 in the failure branch of AdvancePhase.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: change the error text passed to the ErrorBody in AdvancePhase.
        json.Value.Should().Be(new ErrorBody("Phase not complete"));
    }

    [Fact]
    public async Task AbandonSession_Answers204AndLogs()
    {
        _service.AbandonSessionAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        var (factory, logger) = CreateLogging();

        var result = await BrainstormEndpoints.AbandonSession(SessionId, _service, factory, CancellationToken.None);

        // Red: answer Error("x", 400) in place of NoContent in the success branch of AbandonSession.
        result.Result.Should().BeOfType<NoContent>();
        // Red: drop LogAbandoningSession, or change its EventId 205 to 207.
        LoggedEventIds(logger).Should().Equal(205);
        // Red: pass the category "Other" to CreateLogger in AbandonSession.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task AbandonSession_WhenTheServiceFails_Answers400CarryingTheError()
    {
        _service.AbandonSessionAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Result.Failure("Already abandoned"));

        var result = await BrainstormEndpoints.AbandonSession(SessionId, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: ignore result.IsSuccess in AbandonSession; the result is NoContent.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        // Red: answer 404 in place of 400 in the failure branch of AbandonSession.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: change the error text passed to the ErrorBody in AbandonSession.
        json.Value.Should().Be(new ErrorBody("Already abandoned"));
    }

    [Fact]
    public async Task GenerateTasks_ReturnsTheTasksAndLogs()
    {
        List<TaskDto> tasks = [];
        _service.GenerateTasksAsync(SessionId, Arg.Any<CancellationToken>()).Returns(Result<List<TaskDto>>.Success(tasks));
        var (factory, logger) = CreateLogging();

        var result = await BrainstormEndpoints.GenerateTasks(SessionId, _service, factory, CancellationToken.None);

        // Red: answer Error("x", 400) in place of Ok in the success branch of GenerateTasks.
        result.Result.Should().BeOfType<Ok<List<TaskDto>>>().Which.Value.Should().BeSameAs(tasks);
        // Red: drop LogGeneratingTasks, or change its EventId 206 to 207.
        LoggedEventIds(logger).Should().Equal(206);
        // Red: pass the category "Other" to CreateLogger in GenerateTasks.
        factory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GenerateTasks_WhenTheServiceFails_Answers400CarryingTheError()
    {
        _service.GenerateTasksAsync(SessionId, Arg.Any<CancellationToken>())
            .Returns(Result<List<TaskDto>>.Failure("No plan yet"));

        var result = await BrainstormEndpoints.GenerateTasks(SessionId, _service, CreateLogging().Factory, CancellationToken.None);

        // Red: ignore result.IsSuccess in GenerateTasks; the result is Ok.
        var json = result.Result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        // Red: answer 404 in place of 400 in the failure branch of GenerateTasks.
        json.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        // Red: change the error text passed to the ErrorBody in GenerateTasks.
        json.Value.Should().Be(new ErrorBody("No plan yet"));
    }

    public static TheoryData<string> WriteRoutes => new()
    {
        "CreateSession", "SendMessage", "AdvancePhase", "AbandonSession", "GenerateTasks",
    };

    /// <summary>
    ///     Owner decision of 2026-11-05, ruling R83: a write whose session save lost a race to another write answers 409
    ///     with the refusal, as projects and repositories do; GenerateTasks keeps its prefix, which says the tasks were
    ///     created. Red: drop the <c>SaveConflict.IsConflict</c> branch in the endpoints' failure mapping; each route
    ///     answers 400.
    /// </summary>
    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_write_that_lost_a_race_answers_409_with_the_refusal(string route)
    {
        const string Refusal = "The brainstorm session was modified by another operation. Please retry.";
        var generated = $"Created 2 tasks, but the session could not be saved: {Refusal}";
        _service.CreateSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result<BrainstormSession>.Failure(Refusal));
        _service.SendMessageAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<BrainstormMessage>.Failure(Refusal));
        _service.AdvancePhaseAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result<BrainstormSession>.Failure(Refusal));
        _service.AbandonSessionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Result.Failure(Refusal));
        _service.GenerateTasksAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Result<List<TaskDto>>.Failure(generated));
        var factory = CreateLogging().Factory;

        IResult result = route switch
        {
            "CreateSession" => (await BrainstormEndpoints.CreateSession(
                await BindCreate($"{{\"projectId\":\"{ProjectId}\"}}"), _service, factory, CancellationToken.None)).Result,
            "SendMessage" => (await BrainstormEndpoints.SendMessage(
                SessionId, await BindSend("{\"content\":\"Hello\"}"), _service, factory, CancellationToken.None)).Result,
            "AdvancePhase" => (await BrainstormEndpoints.AdvancePhase(SessionId, _service, factory, CancellationToken.None)).Result,
            "AbandonSession" => (await BrainstormEndpoints.AbandonSession(SessionId, _service, factory, CancellationToken.None)).Result,
            "GenerateTasks" => (await BrainstormEndpoints.GenerateTasks(SessionId, _service, factory, CancellationToken.None)).Result,
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
        };

        var json = result.Should().BeOfType<ApiJsonResult<ErrorBody>>().Subject;
        json.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        json.Value.Should().Be(new ErrorBody(string.Equals(route, "GenerateTasks", StringComparison.Ordinal) ? generated : Refusal));
    }

    private static async Task<ApiBody<CreateBrainstormSessionDto>> BindCreate(string body) =>
        await Bind<CreateBrainstormSessionDto>(body);

    private static async Task<ApiBody<SendBrainstormMessageDto>> BindSend(string body) =>
        await Bind<SendBrainstormMessageDto>(body);

    private static async Task<ApiBody<T>> Bind<T>(string body)
        where T : class
    {
        await using var services = Services();
        var context = WithJsonBody(services, body);
        var parameter = typeof(BrainstormEndpoints).GetMethods()
            .SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(ApiBody<T>));
        return await BindAsync<ApiBody<T>>(context, parameter);
    }

    private static async Task<ApiQuery<Guid>> BindProjectId(string queryString)
    {
        await using var services = Services();
        var context = Create(services);
        context.Request.QueryString = new QueryString(queryString);
        var parameter = typeof(BrainstormEndpoints).GetMethod(nameof(BrainstormEndpoints.GetSessions))!
            .GetParameters().First(p => p.ParameterType == typeof(ApiQuery<Guid>));
        return await BindAsync<ApiQuery<Guid>>(context, parameter);
    }

    private static (ILoggerFactory Factory, ILogger Logger) CreateLogging()
    {
        var logger = Substitute.For<ILogger>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var factory = Substitute.For<ILoggerFactory>();
        factory.CreateLogger(Arg.Any<string>()).Returns(logger);
        return (factory, logger);
    }

    private static int[] LoggedEventIds(ILogger logger) =>
        [.. logger.ReceivedCalls()
            .Where(call => string.Equals(call.GetMethodInfo().Name, nameof(ILogger.Log), StringComparison.Ordinal))
            .Select(call => call.GetArguments()[1] is EventId id ? id.Id : throw new InvalidOperationException("No EventId."))];
}
