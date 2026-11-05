using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Daedalus.Agents.Sessions;
using Daedalus.Api.Endpoints;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Controllers;

/// <summary>
///     <see cref="AgentSessionsEndpoints"/> over the real <see cref="PostgresAgentSessionStore"/> (fixture database) and a
///     fake <see cref="IAgentRuntime"/>: HTTP mapping of Thalos results, ownership on reads, and — the one that catches
///     response buffering — SSE events reaching the body before the turn completes. The handlers are called directly,
///     with the query and body parameters bound through their own binders, as the request delegate binds them.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AgentSessionsEndpointsIntegrationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Category = "Daedalus.Api.Endpoints.AgentSessionsEndpoints";
    private static readonly AgentId Agent = AgentId.New();
    private static readonly DateTimeOffset T0 = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly ServiceProvider _services = new ServiceCollection().AddLogging().AddDaedalusEndpoints().BuildServiceProvider();
    private readonly IAgentRuntime _runtime = Substitute.For<IAgentRuntime>();
    private readonly FakeTimeProvider _clock = new(T0);
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private PostgresAgentSessionStore _store = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _store = new PostgresAgentSessionStore(fixture.Db, _clock);
        _logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(_logger);
    }

    public async Task DisposeAsync() => await _services.DisposeAsync();

    [Fact]
    public async Task CreateSession_returns_201_with_location()
    {
        var sessionId = SessionId.New();
        _runtime.CreateSessionAsync(Agent, Arg.Is<ISecurityContext>(c => c.Id == "alice"), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<SessionId, AgentError>.Success(sessionId));

        var result = await CreateSessionAsync("alice", Agent.ToString());

        // Red: return AgentError.Validation("x").ToProblem(context) when result.IsSuccess, ahead of the Created in CreateSession.
        var created = result.Result.Should().BeOfType<Created<CreateAgentSessionResponseDto>>().Subject;
        created.StatusCode.Should().Be(StatusCodes.Status201Created);
        // Red: "/api/agents/Sessions/{sessionId}" in place of "/api/agents/sessions/{sessionId}" in the ApiLocation.For
        //      path of CreateSession.
        created.Location.Should().Be($"http://localhost/api/agents/sessions/{sessionId}");
        // Red: new CreateAgentSessionResponseDto(string.Empty) as the Created body in CreateSession.
        created.Value.Should().BeEquivalentTo(new CreateAgentSessionResponseDto(sessionId.ToString()));
    }

    [Fact]
    public async Task CreateSession_logs_the_new_session()
    {
        _runtime.CreateSessionAsync(Agent, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<SessionId, AgentError>.Success(SessionId.New()));

        await CreateSessionAsync("alice", Agent.ToString());

        // Red: change EventId 300 to 303 on Log.SessionCreated.
        LoggedEvent().Id.Should().Be(300);
        // Red: pass the category "Other" to CreateLogger in CreateSession.
        _loggerFactory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task CreateSession_with_invalid_agent_id_returns_400_and_never_calls_the_runtime()
    {
        var result = await CreateSessionAsync("alice", "not-an-id");

        // Red: append && agentId.Length == 0 to the AgentId.TryParse refusal in CreateSession.
        AssertProblem(result.Result, StatusCodes.Status400BadRequest, "Validation");
        await _runtime.DidNotReceiveWithAnyArgs().CreateSessionAsync(default, null!, default);
    }

    [Fact]
    public async Task Unauthenticated_caller_gets_401_before_anything_else()
    {
        var result = await CreateSessionAsync(null, Agent.ToString());

        // Red: answer StatusCodes.Status403Forbidden in the TryCreate refusal of CreateSession.
        AssertStatus(result.Result, StatusCodes.Status401Unauthorized);
        await _runtime.DidNotReceiveWithAnyArgs().CreateSessionAsync(default, null!, default);
    }

    [Fact]
    public async Task Unauthenticated_caller_gets_401_on_the_other_problem_endpoints()
    {
        // Red: answer StatusCodes.Status403Forbidden in the TryCreate refusal of ListSessions.
        AssertStatus((await ListSessionsAsync(null)).Result, StatusCodes.Status401Unauthorized);
        // Red: answer StatusCodes.Status403Forbidden in the TryCreate refusal of GetSession.
        AssertStatus((await GetSessionAsync(null, SessionId.New().ToString())).Result, StatusCodes.Status401Unauthorized);
        // Red: answer StatusCodes.Status403Forbidden in the TryCreate refusal of RunTurn.
        AssertStatus((await RunTurnAsync(null, SessionId.New().ToString(), "hi")).Result, StatusCodes.Status401Unauthorized);
        // Red: answer StatusCodes.Status403Forbidden in the TryCreate refusal of CloseSession.
        AssertStatus((await CloseSessionAsync(null, SessionId.New().ToString())).Result, StatusCodes.Status401Unauthorized);

        await _runtime.DidNotReceiveWithAnyArgs().RunTurnAsync(null!, default);
        await _runtime.DidNotReceiveWithAnyArgs().CloseSessionAsync(default, null!, default);
    }

    [Fact]
    public async Task GetSession_of_other_user_returns_404()
    {
        var session = (await _store.CreateAsync(Agent, "alice", CancellationToken.None)).Value;

        var result = await GetSessionAsync("bob", session.Id.ToString());

        // Red: drop the IsOwnerOrAdmin refusal in GetSession; bob reads alice's session.
        AssertProblem(result.Result, StatusCodes.Status404NotFound, "SessionNotFound");
        // Red: change EventId 301 to 303 on Log.ForeignSessionAccess.
        LoggedEvent().Id.Should().Be(301);
        // Red: pass the category "Other" to CreateLogger in GetSession.
        _loggerFactory.Received(1).CreateLogger(Category);
    }

    [Fact]
    public async Task GetSession_returns_transcript_to_owner_and_admin()
    {
        var session = (await _store.CreateAsync(Agent, "alice", CancellationToken.None)).Value;
        await _store.AppendMessagesAsync(session.Id, [new ChatMessage(ChatRole.User, "hello"), new ChatMessage(ChatRole.Assistant, "hi")], CancellationToken.None);

        var owner = await GetSessionAsync("alice", session.Id.ToString());
        var admin = await GetSessionAsync("carol", session.Id.ToString(), "admin");

        // Red: AgentDtoMapper.ToDtos(messages.Value.Take(1).ToList()) in place of the loaded messages in GetSession.
        var detail = owner.Result.Should().BeOfType<Ok<AgentSessionDetailDto>>().Subject.Value!;
        detail.Session.Id.Should().Be(session.Id.ToString());
        detail.Session.OwnerId.Should().Be("alice");
        detail.Messages.Select(m => (m.Role, m.Text)).Should().Equal(("user", "hello"), ("assistant", "hi"));
        // Red: "administrator" in place of "admin" as AgentSessionsEndpoints.AdminRole.
        admin.Result.Should().BeOfType<Ok<AgentSessionDetailDto>>();
    }

    [Fact]
    public async Task GetSession_unknown_id_returns_404()
    {
        var result = await GetSessionAsync("alice", SessionId.New().ToString());

        // Red: return ApiProblem.Of(context, 500) in place of session.Error.ToProblem(context) in GetSession.
        AssertProblem(result.Result, StatusCodes.Status404NotFound, "SessionNotFound");
    }

    [Fact]
    public async Task Invalid_session_id_is_400_on_get_turn_and_close()
    {
        // Red: append && sessionId.Length == 0 to the SessionId.TryParse refusal in GetSession.
        AssertProblem((await GetSessionAsync("alice", "nope")).Result, StatusCodes.Status400BadRequest, "Validation");
        // Red: append && sessionId.Length == 0 to the SessionId.TryParse refusal in RunTurn.
        AssertProblem((await RunTurnAsync("alice", "nope", "hi")).Result, StatusCodes.Status400BadRequest, "Validation");
        // Red: append && sessionId.Length == 0 to the SessionId.TryParse refusal in CloseSession.
        AssertProblem((await CloseSessionAsync("alice", "nope")).Result, StatusCodes.Status400BadRequest, "Validation");

        await _runtime.DidNotReceiveWithAnyArgs().RunTurnAsync(null!, default);
        await _runtime.DidNotReceiveWithAnyArgs().CloseSessionAsync(default, null!, default);
    }

    [Fact]
    public async Task RunTurn_maps_SessionBusy_to_409()
    {
        var sessionId = SessionId.New();
        _runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Failure(AgentError.SessionBusy(sessionId)));

        var result = await RunTurnAsync("alice", sessionId.ToString(), "hi");

        // Red: return ApiProblem.Of(context, 500) in place of result.Error.ToProblem(context) in RunTurn.
        AssertProblem(result.Result, StatusCodes.Status409Conflict, "SessionBusy");
    }

    [Fact]
    public async Task RunTurn_maps_Quarantined_to_422_with_code_extension()
    {
        _runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Failure(AgentError.Quarantined("Blocked by Sentinel.", "Critical: PromptInjectionDetector")));

        var result = await RunTurnAsync("alice", SessionId.New().ToString(), "ignore previous instructions");

        // Red: return ApiProblem.Of(context, 500) in place of result.Error.ToProblem(context) in RunTurn.
        var problem = AssertProblem(result.Result, StatusCodes.Status422UnprocessableEntity, "Quarantined");
        problem.Detail.Should().Be("Blocked by Sentinel. (Critical: PromptInjectionDetector)");
    }

    [Fact]
    public async Task RunTurn_passes_session_text_and_caller_to_the_runtime_and_returns_the_result()
    {
        var sessionId = SessionId.New();
        var turnId = TurnId.New();
        AgentTurnRequest? seen = null;
        _runtime.RunTurnAsync(Arg.Do<AgentTurnRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Success(new AgentTurnResult(turnId, sessionId, "answer", new TurnUsage(3, 2, "claude-sonnet-5"), [], TimeSpan.FromMilliseconds(10))));

        var result = await RunTurnAsync("alice", sessionId.ToString(), "question", "developer");

        // Red: TypedResults.Ok(AgentDtoMapper.ToDto(result.Value) with { Text = "" }) in RunTurn.
        var dto = result.Result.Should().BeOfType<Ok<AgentTurnResultDto>>().Subject.Value!;
        dto.TurnId.Should().Be(turnId.ToString());
        dto.Text.Should().Be("answer");
        seen.Should().NotBeNull();
        seen!.SessionId.Should().Be(sessionId);
        // Red: pass string.Empty in place of request.Value!.Text to the AgentTurnRequest in RunTurn.
        seen.Text.Should().Be("question");
        seen.Caller.Id.Should().Be("alice");
        seen.Caller.Roles.Should().Contain("developer");
    }

    [Fact]
    public async Task RunTurnStream_writes_sse_events_incrementally()
    {
        var sessionId = SessionId.New();
        var turnId = TurnId.New();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentTurnRequest? seen = null;
        _runtime.RunTurnStreamingAsync(Arg.Do<AgentTurnRequest>(r => seen = r), Arg.Any<CancellationToken>())
            .Returns(_ => StreamEvents(sessionId, turnId, release.Task));

        var body = new SignallingStream();
        var context = Context("alice", null, body, Json(new SendTurnRequestDto("hi")));

        var streaming = AgentSessionsEndpoints.RunTurnStream(
            sessionId.ToString(), await BindTurnAsync(context), context, _runtime, CancellationToken.None);

        // The connection comment and the first event must be on the wire while the runtime is still mid-turn (blocked on `release`).
        // Red: write $"event:{dto.Kind}\ndata: {payload}\n\n" in WriteEventAsync, without the space; the marker never arrives.
        var firstChunk = await body.WaitForTextAsync("event: text-delta\n", TimeSpan.FromSeconds(10));
        // Red: drop the ": connected\n\n" write in RunTurnStream.
        // Red: serialize with new AgentEventStreamJsonContext(new JsonSerializerOptions(JsonSerializerDefaults.Web)).AgentEventDto
        //      in WriteEventAsync; the unset members go out as null.
        firstChunk.Should().StartWith(": connected\n\n").And.Contain("data: {\"kind\":\"text-delta\",\"text\":\"hel\"}\n\n");
        // Red: write the events after the turn, from await runtime.RunTurnStreamingAsync(...).ToListAsync(ct), in RunTurnStream.
        streaming.IsCompleted.Should().BeFalse("the turn has not finished yet");

        release.SetResult();
        await streaming;

        var response = context.Response;
        // Red: Response.StatusCode = 202 in place of 200 in RunTurnStream.
        response.StatusCode.Should().Be(StatusCodes.Status200OK);
        // Red: "text/plain" as the Response.ContentType in RunTurnStream.
        response.ContentType.Should().Be("text/event-stream");
        // Red: "no-store" as the Cache-Control in RunTurnStream.
        response.Headers.CacheControl.ToString().Should().Be("no-cache");
        // Red: drop the X-Accel-Buffering header in RunTurnStream.
        response.Headers["X-Accel-Buffering"].ToString().Should().Be("no");
        // Red: pass string.Empty in place of request.Value!.Text to the AgentTurnRequest in RunTurnStream.
        seen!.Text.Should().Be("hi");

        var frames = body.Text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        frames.Should().HaveCount(5);
        frames[0].Should().Be(": connected");
        frames[1].Should().StartWith("event: text-delta\ndata: ");
        frames[2].Should().StartWith("event: tool-result\ndata: ").And.Contain("\"succeeded\":true");
        frames[3].Should().StartWith("event: usage\ndata: ").And.Contain("\"inputTokens\":3");
        frames[4].Should().StartWith("event: done\ndata: ").And.Contain($"\"turnId\":\"{turnId}\"");
    }

    [Fact]
    public async Task RunTurnStream_with_invalid_session_id_emits_a_single_error_event()
    {
        var body = new SignallingStream();
        var context = Context("alice", null, body, Json(new SendTurnRequestDto("hi")));

        await AgentSessionsEndpoints.RunTurnStream("nope", await BindTurnAsync(context), context, _runtime, CancellationToken.None);

        // Red: append && sessionId.Length == 0 to the SessionId.TryParse refusal in RunTurnStream.
        body.Text.Should().Be("event: error\ndata: {\"kind\":\"error\",\"errorCode\":\"Validation\",\"errorMessage\":\"sessionId is not a valid id.\"}\n\n");
        _runtime.DidNotReceiveWithAnyArgs().RunTurnStreamingAsync(null!, default);
    }

    [Fact]
    public async Task RunTurnStream_unauthenticated_answers_401_without_a_body()
    {
        var body = new SignallingStream();
        var context = Context(null, null, body, Json(new SendTurnRequestDto("hi")));

        await AgentSessionsEndpoints.RunTurnStream(
            SessionId.New().ToString(), await BindTurnAsync(context), context, _runtime, CancellationToken.None);

        // Red: StatusCodes.Status403Forbidden in place of 401 in the TryCreate refusal of RunTurnStream.
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        // Red: await ApiProblem.Of(context, StatusCodes.Status401Unauthorized).ExecuteAsync(context) before the return
        //      in the TryCreate refusal of RunTurnStream; the 401 gains a problem body.
        body.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task ListSessions_returns_only_callers_sessions_newest_first()
    {
        var first = (await _store.CreateAsync(Agent, "alice", CancellationToken.None)).Value;
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = (await _store.CreateAsync(Agent, "alice", CancellationToken.None)).Value;
        _clock.Advance(TimeSpan.FromMinutes(1));
        await _store.CreateAsync(Agent, "bob", CancellationToken.None);

        var result = await ListSessionsAsync("alice");

        // Red: pass caller.Id + "x" in place of caller.Id to store.ListAsync in ListSessions.
        var sessions = result.Result.Should().BeOfType<Ok<IReadOnlyList<AgentSessionDto>>>().Subject.Value!;
        sessions.Select(s => s.Id).Should().Equal(second.Id.ToString(), first.Id.ToString());
        sessions.Should().OnlyContain(s => s.OwnerId == "alice");
    }

    [Fact]
    public async Task ListSessions_clamps_paging()
    {
        for (var i = 0; i < 3; i++)
        {
            await _store.CreateAsync(Agent, "alice", CancellationToken.None);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var page = await ListSessionsAsync("alice", skip: "-5", take: "0");

        // Red: Math.Clamp(take.ValueOr(DefaultTake), 0, MaxTake) in ListSessions; take 0 reads no session.
        page.Result.Should().BeOfType<Ok<IReadOnlyList<AgentSessionDto>>>().Subject.Value!
            .Should().HaveCount(1, "take is clamped to at least 1 and skip to 0");
    }

    [Fact]
    public async Task ListSessions_defaults_take_to_20_and_skip_to_0()
    {
        for (var i = 0; i < 21; i++)
        {
            await _store.CreateAsync(Agent, "alice", CancellationToken.None);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var page = await ListSessionsAsync("alice");

        // Red: take.ValueOr(MaxTake) in place of take.ValueOr(DefaultTake) in ListSessions.
        // Red: skip.ValueOr(1) in place of skip.ValueOr(0) in ListSessions; the newest session is skipped.
        var sessions = page.Result.Should().BeOfType<Ok<IReadOnlyList<AgentSessionDto>>>().Subject.Value!;
        sessions.Should().HaveCount(20);
        var newest = (await _store.ListAsync("alice", 0, 1, CancellationToken.None)).Value.Single();
        sessions[0].Id.Should().Be(newest.Id.ToString());
    }

    [Fact]
    public async Task CloseSession_returns_204_and_maps_SessionClosed_to_409()
    {
        var sessionId = SessionId.New();
        _runtime.CloseSessionAsync(sessionId, Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.UnitResult<AgentError>.Success(), ZeroAlloc.Results.UnitResult<AgentError>.Failure(AgentError.SessionClosed(sessionId)));

        var first = await CloseSessionAsync("alice", sessionId.ToString());
        var second = await CloseSessionAsync("alice", sessionId.ToString());

        // Red: return ApiProblem.Of(context, StatusCodes.Status200OK) in place of NoContent in CloseSession.
        first.Result.Should().BeOfType<NoContent>();
        // Red: return ApiProblem.Of(context, 500) in place of result.Error.ToProblem(context) in CloseSession.
        AssertProblem(second.Result, StatusCodes.Status409Conflict, "SessionClosed");
    }

    // ---------- helpers ----------

    private static async IAsyncEnumerable<AgentEvent> StreamEvents(SessionId sessionId, TurnId turnId, Task release, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextDeltaEvent(sessionId, turnId, "hel");
        await release.WaitAsync(ct);
        yield return new ToolCallFinishedEvent(sessionId, turnId, ToolCallId.New(), "daedalus__search_learnings", true, "ok", TimeSpan.FromMilliseconds(5));
        var usage = new TurnUsage(3, 2, "claude-sonnet-5");
        yield return new UsageEvent(sessionId, turnId, usage);
        yield return new TurnCompletedEvent(sessionId, turnId, new AgentTurnResult(turnId, sessionId, "hello", usage, [], TimeSpan.FromMilliseconds(20)));
    }

    private static void AssertStatus(IResult result, int status) =>
        result.Should().BeOfType<ApiProblemResult>().Which.StatusCode.Should().Be(status);

    private static ProblemDetails AssertProblem(IResult result, int status, string code)
    {
        var problem = result.Should().BeOfType<ApiProblemResult>().Subject;
        problem.StatusCode.Should().Be(status);
        problem.ProblemDetails.Status.Should().Be(status);
        problem.ProblemDetails.Title.Should().Be(code);
        problem.ProblemDetails.Extensions.Should().ContainKey("code").WhoseValue.Should().Be(code);
        return problem.ProblemDetails;
    }

    private EventId LoggedEvent() =>
        _logger.ReceivedCalls().Single(call => string.Equals(call.GetMethodInfo().Name, nameof(ILogger.Log), StringComparison.Ordinal))
            .GetArguments()[1] is EventId id ? id : throw new InvalidOperationException("No EventId argument.");

    private async Task<Results<Created<CreateAgentSessionResponseDto>, ApiProblemResult>> CreateSessionAsync(string? userId, string agentId)
    {
        var context = Context(userId, null);
        return await AgentSessionsEndpoints.CreateSession(agentId, context, _runtime, _loggerFactory, CancellationToken.None);
    }

    private async Task<Results<Ok<IReadOnlyList<AgentSessionDto>>, ApiProblemResult>> ListSessionsAsync(
        string? userId, string? skip = null, string? take = null)
    {
        var context = Context(userId, null);
        context.Request.QueryString = QueryString.Create(
            new[] { ("skip", skip), ("take", take) }
                .Where(pair => pair.Item2 is not null)
                .Select(pair => new KeyValuePair<string, string?>(pair.Item1, pair.Item2)));
        return await AgentSessionsEndpoints.ListSessions(
            await Query<int>(context, "skip"), await Query<int>(context, "take"), context, _store, CancellationToken.None);
    }

    private async Task<Results<Ok<AgentSessionDetailDto>, ApiProblemResult>> GetSessionAsync(string? userId, string sessionId, string? role = null)
    {
        var context = Context(userId, role);
        return await AgentSessionsEndpoints.GetSession(sessionId, context, _store, _loggerFactory, CancellationToken.None);
    }

    private async Task<Results<Ok<AgentTurnResultDto>, ApiProblemResult>> RunTurnAsync(
        string? userId, string sessionId, string text, string? role = null)
    {
        var context = Context(userId, role, requestJson: Json(new SendTurnRequestDto(text)));
        return await AgentSessionsEndpoints.RunTurn(sessionId, await BindTurnAsync(context), context, _runtime, CancellationToken.None);
    }

    private async Task<Results<NoContent, ApiProblemResult>> CloseSessionAsync(string? userId, string sessionId)
    {
        var context = Context(userId, null);
        return await AgentSessionsEndpoints.CloseSession(sessionId, context, _runtime, CancellationToken.None);
    }

    private static string Json(SendTurnRequestDto request) => JsonSerializer.Serialize(request, WebJson);

    private DefaultHttpContext Context(string? userId, string? role, Stream? responseBody = null, string? requestJson = null)
    {
        var identity = new ClaimsIdentity();
        if (userId is not null)
        {
            var claims = new List<Claim> { new("sub", userId) };
            if (role is not null)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
            }

            identity = new ClaimsIdentity(claims, "Test");
        }

        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = _services };
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        context.Response.Body = responseBody ?? new MemoryStream();
        if (requestJson is not null)
        {
            var bytes = Encoding.UTF8.GetBytes(requestJson);
            context.Request.Method = HttpMethods.Post;
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = bytes.Length;
            context.Request.Body = new MemoryStream(bytes);
        }

        return context;
    }

    /// <summary>Binds the request body as the request delegate binds the handlers' <c>ApiBody</c> parameter.</summary>
    private static async Task<ApiBody<SendTurnRequestDto>> BindTurnAsync(HttpContext context)
    {
        var parameter = typeof(AgentSessionsEndpoints).GetMethods().SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(ApiBody<SendTurnRequestDto>));
        return await Bind<ApiBody<SendTurnRequestDto>>(context, parameter);
    }

    /// <summary>Binds the handler's own <c>ApiQuery</c> parameter called <paramref name="name"/>, as the request delegate does.</summary>
    private static async Task<ApiQuery<T>> Query<T>(HttpContext context, string name)
        where T : IParsable<T>
    {
        var parameter = typeof(AgentSessionsEndpoints).GetMethods().SelectMany(method => method.GetParameters())
            .First(p => p.ParameterType == typeof(ApiQuery<T>) && string.Equals(p.Name, name, StringComparison.Ordinal));
        return await Bind<ApiQuery<T>>(context, parameter);
    }

    private static async Task<TSelf> Bind<TSelf>(HttpContext context, ParameterInfo parameter)
        where TSelf : class, IBindableFromHttpContext<TSelf> => (await TSelf.BindAsync(context, parameter))!;

    /// <summary>Response body that lets a test await the first write, so incremental flushing can be asserted mid-turn.</summary>
    private sealed class SignallingStream : Stream
    {
        private readonly MemoryStream _inner = new();
        private readonly SemaphoreSlim _written = new(0);

        public string Text => Encoding.UTF8.GetString(_inner.ToArray());

        /// <summary>Awaits writes until the body contains <paramref name="marker"/> (or the timeout elapses).</summary>
        public async Task<string> WaitForTextAsync(string marker, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!Text.Contains(marker, StringComparison.Ordinal))
            {
                var remaining = deadline - DateTime.UtcNow;
                (remaining > TimeSpan.Zero && await _written.WaitAsync(remaining)).Should().BeTrue("'{0}' should have been written within {1}", marker, timeout);
            }

            return Text;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _written.Release();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            _written.Release();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                _written.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
