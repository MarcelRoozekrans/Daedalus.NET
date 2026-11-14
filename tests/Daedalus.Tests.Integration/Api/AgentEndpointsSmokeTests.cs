using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thalos;
using Thalos.Memory;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Api;

/// <summary>
///     End-to-end over the real API host (see <see cref="ApiWebApplicationFactory"/>): auth policy, routing, JSON, and —
///     through the production middleware pipeline including response compression — SSE reaching the client mid-turn.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AgentEndpointsSmokeTests(PostgresFixture fixture) : IAsyncLifetime
{
    /// <summary>The agent declared in <c>src/Daedalus.Api/appsettings.json</c>.</summary>
    private const string ArchitectAgentId = "01M05YCM7DPKRG9X04870B2JYH";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IAgentRuntime _runtime = Substitute.For<IAgentRuntime>();
    private ApiWebApplicationFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _factory = new ApiWebApplicationFactory(fixture.ConnectionString, _runtime);
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Agents_endpoint_requires_authentication()
    {
        var response = await _client.GetAsync(new Uri("/api/agents", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Agents_endpoint_lists_the_configured_agent()
    {
        var response = await Send(HttpMethod.Get, "/api/agents", user: "alice");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var agents = await response.Content.ReadFromJsonAsync<List<AgentSummaryDto>>(Json);
        // The catalog also carries the RepoDigest workflow's scout and writer agents; this test only guards
        // the human-facing Daedalus Architect entry.
        agents.Should().ContainSingle(a => a.Name == "Daedalus Architect")
            .Which.Should().BeEquivalentTo(new { Id = ArchitectAgentId, Name = "Daedalus Architect" });
    }

    [Fact]
    public async Task Create_get_and_list_sessions_round_trip_through_the_real_store()
    {
        // The fake runtime persists through the production store so the follow-up GETs read real rows.
        var store = _factory.Services.GetRequiredService<IAgentSessionStore>();
        _runtime.CreateSessionAsync(Arg.Any<AgentId>(), Arg.Any<ISecurityContext>(), Arg.Any<CancellationToken>())
            .Returns(call => CreateThroughStoreAsync(call.Arg<AgentId>(), call.Arg<ISecurityContext>().Id, call.Arg<CancellationToken>()));

        async ValueTask<ZeroAlloc.Results.Result<SessionId, AgentError>> CreateThroughStoreAsync(AgentId agentId, string owner, CancellationToken ct)
        {
            var created = await store.CreateAsync(agentId, owner, ct);
            return created.IsSuccess
                ? ZeroAlloc.Results.Result<SessionId, AgentError>.Success(created.Value.Id)
                : ZeroAlloc.Results.Result<SessionId, AgentError>.Failure(created.Error);
        }

        var create = await Send(HttpMethod.Post, $"/api/agents/{ArchitectAgentId}/sessions", user: "alice");
        create.StatusCode.Should().Be(HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var sessionId = (await create.Content.ReadFromJsonAsync<CreateAgentSessionResponseDto>(Json))!.SessionId;
        create.Headers.Location!.ToString().Should().EndWith($"/api/agents/sessions/{sessionId}");

        var get = await Send(HttpMethod.Get, $"/api/agents/sessions/{sessionId}", user: "alice");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await get.Content.ReadFromJsonAsync<AgentSessionDetailDto>(Json);
        detail!.Session.Should().BeEquivalentTo(new { Id = sessionId, AgentId = ArchitectAgentId, OwnerId = "alice", State = "Idle", TurnCount = 0 });
        detail.Messages.Should().BeEmpty();

        var foreign = await Send(HttpMethod.Get, $"/api/agents/sessions/{sessionId}", user: "bob");
        foreign.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // [Produces("application/json")] on the controller wins over ProblemDetails' problem+json — same as the other controllers.
        foreign.Content.Headers.ContentType!.MediaType.Should().EndWith("json");
        var problem = await foreign.Content.ReadFromJsonAsync<JsonElement>(Json);
        problem.GetProperty("code").GetString().Should().Be("SessionNotFound");
        problem.GetProperty("status").GetInt32().Should().Be(404);

        var list = await Send(HttpMethod.Get, "/api/agents/sessions", user: "alice");
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        (await list.Content.ReadFromJsonAsync<List<AgentSessionDto>>(Json))!.Select(s => s.Id).Should().Equal(sessionId);
    }

    [Fact]
    public async Task Memories_endpoint_lists_the_callers_and_shared_memories()
    {
        var store = _factory.Services.GetRequiredService<IMemoryStore>();
        var mine = await SeedMemoryAsync(store, "alice", "The E2E user prefers xUnit over NUnit.");
        var shared = await SeedMemoryAsync(store, "daedalus", "Playwright locators use data-testid.");
        var foreign = await SeedMemoryAsync(store, "bob", "Bob likes NUnit.");

        var anonymous = await _client.GetAsync(new Uri("/api/agent-memories", UriKind.Relative));
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var response = await Send(HttpMethod.Get, "/api/agent-memories", user: "alice");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var page = await response.Content.ReadFromJsonAsync<PagedResultDto<MemoryDto>>(Json);
        page!.Total.Should().Be(2);
        page.Items.Should().HaveCount(2);
        page.Items.Select(m => m.Id).Should().BeEquivalentTo([mine.ToString(), shared.ToString()]);
        page.Items.Single(m => string.Equals(m.Id, shared.ToString(), StringComparison.Ordinal)).IsShared.Should().BeTrue();
        page.Items.Single(m => string.Equals(m.Id, mine.ToString(), StringComparison.Ordinal)).IsShared.Should().BeFalse();
        page.Items.Should().NotContain(m => string.Equals(m.Id, foreign.ToString(), StringComparison.Ordinal), "another owner's memories are invisible");

        var single = await Send(HttpMethod.Get, $"/api/agent-memories/{mine}", user: "alice");
        single.StatusCode.Should().Be(HttpStatusCode.OK);
        (await single.Content.ReadFromJsonAsync<MemoryDto>(Json))!.Text.Should().Be("The E2E user prefers xUnit over NUnit.");

        var probing = await Send(HttpMethod.Get, $"/api/agent-memories/{foreign}", user: "alice");
        probing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await probing.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("code").GetString().Should().Be("MemoryNotFound");
    }

    [Fact]
    public async Task Turn_errors_arrive_as_problem_details_with_code()
    {
        var sessionId = SessionId.New();
        _runtime.RunTurnAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(ZeroAlloc.Results.Result<AgentTurnResult, AgentError>.Failure(AgentError.SessionBusy(sessionId)));

        var response = await Send(HttpMethod.Post, $"/api/agents/sessions/{sessionId}/turns", user: "alice", body: new SendTurnRequestDto("hi"));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        problem.GetProperty("code").GetString().Should().Be("SessionBusy");
        problem.GetProperty("title").GetString().Should().Be("SessionBusy");
    }

    [Fact]
    public async Task Streaming_turn_reaches_the_client_before_the_turn_completes_even_with_compression_negotiated()
    {
        var sessionId = SessionId.New();
        var turnId = TurnId.New();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => StreamEvents(sessionId, turnId, release.Task));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/agents/sessions/{sessionId}/turns/stream", UriKind.Relative));
        request.Headers.Add(HeaderTestAuthHandler.UserHeader, "alice");
        request.Headers.AcceptEncoding.ParseAdd("gzip"); // exercises UseResponseCompression's decision for text/event-stream
        request.Content = JsonContent.Create(new SendTurnRequestDto("hi"), options: Json);

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        response.Content.Headers.ContentEncoding.Should().BeEmpty("SSE must never be gzip-buffered");
        response.Headers.CacheControl!.NoCache.Should().BeTrue();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        // The connection comment goes out with the headers, then the first event — both while the runtime is still blocked.
        var connectedFrame = await ReadFrameAsync(reader).WaitAsync(TimeSpan.FromSeconds(10));
        connectedFrame.Should().Be(": connected");
        var firstFrame = await ReadFrameAsync(reader).WaitAsync(TimeSpan.FromSeconds(10));
        firstFrame.Should().Be("event: text-delta\ndata: {\"kind\":\"text-delta\",\"text\":\"hel\"}");
        release.Task.IsCompleted.Should().BeFalse();

        release.SetResult();

        var rest = await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var frames = rest.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        frames.Select(f => f[..f.IndexOf('\n', StringComparison.Ordinal)]).Should().Equal("event: usage", "event: done");
        frames[^1].Should().Contain($"\"turnId\":\"{turnId}\"");
    }

    /// <summary>
    ///     Review Focus 4: the stream goes out byte-transparent, with no <c>Content-Encoding</c>, even when the client
    ///     offers gzip. The response-compression <c>MimeTypes</c> in <c>ApiApplication.cs</c> leave out <c>text/event-stream</c>;
    ///     that, not <c>DisableBuffering</c>, is what keeps it uncompressed. The raw bytes start with the connection comment.
    /// </summary>
    [Fact]
    public async Task Stream_is_not_compressed()
    {
        var sessionId = SessionId.New();
        var turnId = TurnId.New();
        _runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => StreamEvents(sessionId, turnId, Task.CompletedTask));

        using var request = StreamRequest(sessionId);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        // Red: Response.StatusCode = 202 in place of 200 in RunTurnStream.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Red: "text/event-stream" added to the response compression MimeTypes in ApiApplication.cs; the stream goes out
        //      gzip-encoded, DisableBuffering notwithstanding.
        response.Content.Headers.ContentEncoding.Should().BeEmpty("SSE must never be gzip-buffered");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        // Red: drop the ": connected\n\n" write in RunTurnStream.
        Encoding.UTF8.GetString(bytes).Should().StartWith(": connected\n\n");
    }

    /// <summary>
    ///     Review Focus 4: a client that goes away mid-stream ends the turn quietly. The stub runtime keeps a session busy
    ///     from the start of a turn's enumeration until its enumerator finishes, and answers a second turn on a busy session
    ///     with a <c>SessionBusy</c> error event, as the runtime does. The test proves that the request's abort token
    ///     reaches the runtime, that the handler disposes the turn so the session is free for the next turn, and that the
    ///     host logs no error and no exception at Warning or above for the aborted request.
    /// </summary>
    [Fact]
    public async Task Client_disconnect_mid_stream_is_quiet()
    {
        var logs = new CapturingLoggerProvider();
        var finished = new RequestCompletion();
        await using var factory = new ApiWebApplicationFactory(
            fixture.ConnectionString, _runtime, configureServices: services => services
                .AddSingleton<ILoggerProvider>(logs)
                .AddSingleton<IStartupFilter>(finished));
        using var client = factory.CreateClient();
        var sessionId = SessionId.New();
        var session = new StubSession();
        _runtime.RunTurnStreamingAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(
                call => session.Turn(sessionId, hang: true, call.Arg<CancellationToken>()),
                call => session.Turn(sessionId, hang: false, call.Arg<CancellationToken>()));

        using (var request = StreamRequest(sessionId))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
        {
            // Red: Response.StatusCode = 202 in place of 200 in RunTurnStream.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await using var stream = await response.Content.ReadAsStreamAsync();
            var seen = new StringBuilder();
            var buffer = new byte[256];
            using var firstEvent = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!seen.ToString().Contains("event: text-delta\n", StringComparison.Ordinal))
            {
                var read = await stream.ReadAsync(buffer, firstEvent.Token);
                // Red: enumerate runtime.RunTurnStreamingAsync(...).Take(0) in RunTurnStream; the stream ends after the
                //      connection comment and the read returns 0.
                read.Should().BePositive("the first event arrives before the turn ends");
                seen.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }

            using var abort = new CancellationTokenSource();
            var pending = stream.ReadAsync(buffer, abort.Token).AsTask();
            await abort.CancelAsync();
            // Harness precondition, no Red: this checks that the client's own cancellation of a pending read throws,
            // which is how the test aborts the request. The handler cannot make it pass or fail; it holds the first
            // turn open, so the read has nothing to return before it is cancelled.
            await FluentActions.Awaiting(() => pending).Should().ThrowAsync<OperationCanceledException>();
        }

        // Red: pass CancellationToken.None in place of ct to runtime.RunTurnStreamingAsync in RunTurnStream; the turn
        //      never learns that its client went away.
        await session.Cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        // The aborted request has left the whole pipeline, the exception middleware and its logging included, before
        // the logs are read below.
        await finished.First.WaitAsync(TimeSpan.FromSeconds(10));

        using (var next = StreamRequest(sessionId))
        using (var response = await client.SendAsync(next))
        {
            // Red: llm-operations' PermitLimit lowered to 1 in ApiApplication.cs; the next turn is refused with 429.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var frames = (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            // Red: enumerate the turn in RunTurnStream through runtime.RunTurnStreamingAsync(...).GetAsyncEnumerator(ct)
            //      and MoveNextAsync, never disposing it; the first turn's enumerator stays suspended and the session busy.
            frames[^1].Should().StartWith("event: done\n");
        }

        // Red: rethrow the turn's OperationCanceledException as an InvalidOperationException around the event loop
        //      in RunTurnStream; the host logs it as an unhandled exception.
        logs.Entries.Where(entry => entry.Level >= LogLevel.Error || (entry.Level >= LogLevel.Warning && entry.Exception is not null))
            .Select(entry => $"{entry.Level} {entry.Category}: {entry.Message} {entry.Exception}")
            .Should().BeEmpty();
    }

    private static async Task<MemoryId> SeedMemoryAsync(IMemoryStore store, string ownerId, string text)
    {
        var now = DateTimeOffset.UtcNow;
        var created = await store.CreateAsync(
            new MemoryRecord { Id = MemoryId.New(), OwnerId = ownerId, Kind = MemoryKind.Learning, Text = text, Source = "test", CreatedAt = now, UpdatedAt = now },
            CancellationToken.None);

        created.IsSuccess.Should().BeTrue();
        return created.Value.Id;
    }

    private static async Task<string> ReadFrameAsync(StreamReader reader)
    {
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.Length == 0)
            {
                break;
            }

            lines.Add(line);
        }

        return string.Join('\n', lines);
    }

    private static async IAsyncEnumerable<AgentEvent> StreamEvents(SessionId sessionId, TurnId turnId, Task release, [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new TextDeltaEvent(sessionId, turnId, "hel");
        await release.WaitAsync(ct);
        var usage = new TurnUsage(3, 2, "claude-sonnet-5");
        yield return new UsageEvent(sessionId, turnId, usage);
        yield return new TurnCompletedEvent(sessionId, turnId, new AgentTurnResult(turnId, sessionId, "hello", usage, [], TimeSpan.FromMilliseconds(20)));
    }

    /// <summary>
    ///     One session of the stub runtime: busy from the start of a turn's enumeration until its enumerator finishes, as
    ///     the runtime's session lock is, and a turn on a busy session yields only a <c>SessionBusy</c> failure event.
    /// </summary>
    private sealed class StubSession
    {
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _busy;

        /// <summary>Completes when a hanging turn sees its cancellation token fire.</summary>
        public Task Cancelled => _cancelled.Task;

        public async IAsyncEnumerable<AgentEvent> Turn(
            SessionId sessionId, bool hang, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var turnId = TurnId.New();
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                yield return new TurnFailedEvent(sessionId, turnId, AgentError.SessionBusy(sessionId));
                yield break;
            }

            try
            {
                yield return new TextDeltaEvent(sessionId, turnId, "hel");
                if (hang)
                {
                    // Like the runtime, a cancelled turn reports its cancellation as an event rather than throwing, so
                    // the enumerator stays suspended until its consumer disposes it.
                    await UntilCancelledAsync(ct);
                    _cancelled.TrySetResult();
                    yield return new TurnFailedEvent(sessionId, turnId, AgentError.Cancelled());
                }

                var usage = new TurnUsage(3, 2, "claude-sonnet-5");
                yield return new UsageEvent(sessionId, turnId, usage);
                yield return new TurnCompletedEvent(sessionId, turnId, new AgentTurnResult(turnId, sessionId, "hello", usage, [], TimeSpan.FromMilliseconds(20)));
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
        }

        private static async Task UntilCancelledAsync(CancellationToken ct)
        {
            var fired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (ct.Register(() => fired.TrySetResult()))
            {
                await fired.Task;
            }
        }
    }

    private static HttpRequestMessage StreamRequest(SessionId sessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"/api/agents/sessions/{sessionId}/turns/stream", UriKind.Relative));
        request.Headers.Add(HeaderTestAuthHandler.UserHeader, "alice");
        request.Headers.AcceptEncoding.ParseAdd("gzip");
        request.Content = JsonContent.Create(new SendTurnRequestDto("hi"), options: Json);
        return request;
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string user, object? body = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add(HeaderTestAuthHandler.UserHeader, user);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        return await _client.SendAsync(request);
    }

    private sealed record LogEntry(LogLevel Level, string Category, string Message, Exception? Exception);

    /// <summary>Keeps every entry the host logs, with its level, category and exception.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new LogEntry(logLevel, category, formatter(state, exception), exception));
        }
    }

    /// <summary>
    ///     Middleware ahead of the whole pipeline that completes <see cref="First"/> when the first request has left it.
    ///     The host's exception middleware sits inside it, so by then any exception it logs has been logged. The host
    ///     keeps <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> below Warning out of every provider, so its "Request
    ///     finished" entry cannot serve.
    /// </summary>
    private sealed class RequestCompletion : IStartupFilter
    {
        private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task First => _first.Task;

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, inner) =>
            {
                try
                {
                    await inner(context);
                }
                finally
                {
                    _first.TrySetResult();
                }
            });
            next(app);
        };
    }
}
