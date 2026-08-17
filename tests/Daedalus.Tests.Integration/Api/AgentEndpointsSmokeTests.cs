using System.Runtime.CompilerServices;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
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
        agents.Should().ContainSingle().Which.Should().BeEquivalentTo(new { Id = ArchitectAgentId, Name = "Daedalus Architect" });
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
}
