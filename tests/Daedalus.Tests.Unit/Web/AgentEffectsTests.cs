using System.Net;
using System.Text.Json;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Web.State.Agent;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using ZeroAlloc.Flux;
using ZeroAlloc.Flux.Generated;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The Agent effects: the streamed turn with Stop, a transport failure, a session switch and a session renewal; and
///     the reads and writes around it, each returning its outcome. A failure is logged with its text only, never the
///     person's message or the model's reply.
/// </summary>
public sealed class AgentEffectsTests : IDisposable
{
    private const string A = "session-a";
    private const string B = "session-b";
    private const string Prompt = "where is Foo called";
    private const string Secret = "the model's private reply";
    private const string Refusal = "The session is busy.";

    private const string AgentsPath = "/api/agents";
    private const string SessionsPath = "/api/agents/sessions?skip=0&take=20";
    private const string PathA = $"/api/agents/sessions/{A}";
    private const string PathB = $"/api/agents/sessions/{B}";
    private const string StreamA = $"{PathA}/turns/stream";
    private const string CreatePath = "/api/agents/architect/sessions";
    private const string MemoriesPath = "/api/agent-memories?page=1&pageSize=20&includeArchived=false&agentId=architect";

    private static readonly AgentSessionDto HeaderA = new(A, "architect", "owner", "Idle", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0, 0, 0);
    private static readonly AgentSessionDto HeaderB = HeaderA with { Id = B };
    private static readonly AgentSessionDetailDto DetailA = new(HeaderA, []);
    private static readonly AgentSessionDetailDto DetailB = new(HeaderB, [new AgentMessageDto("user", "on b", [], null)]);

    private readonly RecordingHandler _handler = new();
    private readonly ServiceProvider _provider = new ServiceCollection().AddZeroAllocFlux().BuildServiceProvider();
    private readonly IStore<AgentState> _store;
    private readonly AgentEffects _effects;
    private readonly RecordingLogger<AgentEffects> _logger = new();
    private readonly SseFeed _feed = new();

    public AgentEffectsTests()
    {
        _store = _provider.GetRequiredService<IStore<AgentState>>();
        _effects = new AgentEffects(_handler.AgentClient(), _provider.GetRequiredService<IDispatcher>(), _store, _logger);
        _handler.OnJson(SessionsPath, Json(new[] { HeaderA }));
        _handler.OnJson(PathA, Json(DetailA));
        _handler.OnJson(PathB, Json(DetailB));
        _handler.On(StreamA, _feed.Response);
    }

    public void Dispose()
    {
        _feed.Dispose();
        _provider.Dispose();
        _handler.Dispose();
    }

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);

    private static AgentEventDto Delta(string text) => new("text-delta", Text: text);

    private static MemoryDto Memory(string id) => new(
        id, "owner", null, "fact", $"memory {id}", [], "agent", 0.5, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, 0, false,
        false, false);

    private static HttpResponseMessage SigningIn()
    {
        var navigation = new RecordingNavigation();
        throw new AccessTokenNotAvailableException(navigation, navigation.RefreshFailed(), scopes: null);
    }

    private async Task OpenedAsync(string sessionId = A) => await _effects.OpenSessionAsync(sessionId, CancellationToken.None);

    /// <summary>Waits until <paramref name="condition"/> holds on the store, as a turn streams on.</summary>
    private async Task UntilAsync(Func<AgentState, bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition(_store.Value))
        {
            await Task.Delay(1, timeout.Token);
        }
    }

    /// <summary>
    ///     A turn sends the person's text, streams the reply into the transcript, then refreshes the session list and the
    ///     session's header, and reads what the turn recalled in the session's agent context.
    ///     Red: dispatch nothing per event in SendAsync; the reply is empty and dropped.
    ///     Red: drop the AgentTurnEnded dispatch in SendAsync; the turn never ends.
    ///     Red: drop the AgentSessionRefreshed dispatch in SendAsync; the header keeps 0 turns.
    ///     Red: drop the agent context in LoadRecalledAsync; the memory read has no agentId and is not routed.
    ///     Red: pass string.Empty as the text to StreamTurnAsync in SendAsync; the body lacks the prompt.
    /// </summary>
    [Fact]
    public async Task A_turn_streams_its_reply_and_refreshes_the_session()
    {
        await OpenedAsync();
        _handler.OnJson(SessionsPath, Json(new[] { HeaderA with { TurnCount = 1 } }));
        _handler.OnJson("/api/agent-memories/m1?agentId=architect", Json(Memory("m1")));
        _feed.Send(new AgentEventDto("memory-recalled", Memory: new MemoryEventDto(Count: 1, Ids: ["m1"])))
            .Send(Delta("Foo is called ")).Send(Delta("twice.")).Send(new AgentEventDto("done")).End();

        var outcome = await _effects.SendAsync(A, Prompt, CancellationToken.None);

        outcome.Should().Be(AgentOutcome.Done);
        var state = _store.Value;
        state.Transcript.Select(m => m.Text).Should().Equal(Prompt, "Foo is called twice.");
        state.Streaming.Should().BeFalse();
        state.Session!.TurnCount.Should().Be(1);
        state.Recalled.Should().ContainSingle().Which.Id.Should().Be("m1");
        _handler.Requests.Single(r => string.Equals(r.PathAndQuery, StreamA, StringComparison.Ordinal)).Body.Should().Contain(Prompt);
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     Stop cancels the turn: the text that streamed stays and "The turn was stopped." shows; nothing is logged.
    ///     Red: make StopTurn do nothing; the turn waits for the next event and the test times out.
    ///     Red: return null instead of AgentError.Cancelled for a stop in StreamAsync; no alert.
    /// </summary>
    [Fact]
    public async Task Stop_ends_the_turn_and_says_so()
    {
        await OpenedAsync();
        _feed.Send(Delta("Foo is"));
        var sending = _effects.SendAsync(A, Prompt, CancellationToken.None);
        await UntilAsync(s => s.Reply is { Text: "Foo is" });

        _effects.StopTurn();
        var outcome = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        outcome.Should().Be(AgentOutcome.Done);
        _store.Value.Error.Should().Be(AgentError.Cancelled);
        _store.Value.Transcript[^1].Text.Should().Be("Foo is");
        _store.Value.Streaming.Should().BeFalse();
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     A connection lost mid-turn says so and is logged as a warning with the transport's text, never the prompt or
    ///     the reply.
    ///     Red: drop the HttpRequestException catch in StreamAsync; it reads as an unexpected error.
    ///     Red: drop the LogTurnTransportFailed call; no warning.
    /// </summary>
    [Fact]
    public async Task A_transport_failure_says_the_connection_was_lost()
    {
        await OpenedAsync();
        _feed.Send(Delta(Secret)).Fail(new HttpRequestException("The connection was reset."));

        await _effects.SendAsync(A, Prompt, CancellationToken.None);

        _store.Value.Error.Should().Be(new AgentError("Transport", "Connection lost: The connection was reset.", null));
        _store.Value.Transcript[^1].Text.Should().Be(Secret);
        var entry = _logger.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("The connection was reset.").And.NotContain(Prompt).And.NotContain(Secret);
    }

    /// <summary>
    ///     A failure the page did not expect, such as the resilience handler's open circuit, shows its type as the page
    ///     always did, and is logged with the type alone.
    ///     Red: drop the catch-all in StreamAsync; the exception escapes.
    ///     Red: drop the LogTurnFailed call; no warning.
    /// </summary>
    [Fact]
    public async Task An_unexpected_failure_shows_its_type()
    {
        await OpenedAsync();
        _handler.On(StreamA, new Func<HttpResponseMessage>(() => throw new BrokenCircuitException(Secret)));

        await _effects.SendAsync(A, Prompt, CancellationToken.None);

        _store.Value.Error.Should().Be(new AgentError("Error", "Unexpected error while streaming: BrokenCircuitException", null));
        _store.Value.Streaming.Should().BeFalse();
        _logger.Entries.Should().ContainSingle().Which.Message.Should().Contain("BrokenCircuitException").And.NotContain(Secret);
    }

    /// <summary>
    ///     A turn the API refuses before it streams, such as a busy session, shows the API's code and reason, through the
    ///     error event the client makes of it.
    ///     Red: drop the error case in ApplyEvent; no alert.
    /// </summary>
    [Fact]
    public async Task A_refused_turn_shows_the_apis_reason()
    {
        await OpenedAsync();
        _handler.OnJson(StreamA, Json(new { code = "SessionBusy", detail = Refusal }), HttpStatusCode.Conflict);

        await _effects.SendAsync(A, Prompt, CancellationToken.None);

        _store.Value.Error.Should().Be(new AgentError("SessionBusy", Refusal, null));
    }

    /// <summary>
    ///     Opening another session mid-turn stops the turn, and nothing of it reaches the new session: no text, no
    ///     "stopped" alert, no recall. The session list is still refreshed, as before.
    ///     Red: drop the StopTurn call in OpenSessionAsync; the turn streams on and the test times out.
    ///     Red: in the AgentTurnEnded reducer, check neither IsCurrent nor the reply, ending
    ///     <c>s.Reply ?? AgentChatMessage.Assistant()</c>; "The turn was stopped." shows on B.
    ///     Red: keep Reply in the AgentSessionOpened reducer; A's reply still streams on B.
    ///     Red: stamp AgentSessionLoaded with turn - 1 in OpenSessionAsync; B's read is dropped: no header, no transcript.
    ///     Red: skip the session list refresh in SendAsync when the session changed; the list is not read.
    /// </summary>
    [Fact]
    public async Task A_session_switch_stops_the_turn_and_shows_nothing_of_it()
    {
        await OpenedAsync();
        _feed.Send(Delta(Secret));
        var sending = _effects.SendAsync(A, Prompt, CancellationToken.None);
        await UntilAsync(s => s.Reply is { Text: Secret });

        await _effects.OpenSessionAsync(B, CancellationToken.None);
        await sending.WaitAsync(TimeSpan.FromSeconds(5));

        var state = _store.Value;
        state.SessionId.Should().Be(B);
        state.Transcript.Select(m => m.Text).Should().Equal("on b");
        state.Reply.Should().BeNull();
        state.Error.Should().BeNull();
        state.Session.Should().Be(HeaderB);
        _handler.Paths.Count(p => string.Equals(p, SessionsPath, StringComparison.Ordinal)).Should().Be(1);
    }

    /// <summary>
    ///     A turn whose token cannot be refreshed stores no error and logs nothing: the layout already says "Signing in
    ///     again". The turn still ends, so the composer is not left disabled.
    ///     Red: drop <c>and not AccessTokenNotAvailableException</c> from StreamAsync's catch-all; an error is stored and logged.
    ///     Red: drop the AgentTurnEnded dispatch on a renewal in SendAsync; the turn stays streaming.
    ///     Red: return AgentOutcome.Done on a renewal in SendAsync; it is an outcome.
    /// </summary>
    [Fact]
    public async Task A_session_renewal_mid_turn_stores_no_error()
    {
        await OpenedAsync();
        _handler.On(StreamA, SigningIn);

        var outcome = await _effects.SendAsync(A, Prompt, CancellationToken.None);

        outcome.Should().BeNull();
        _store.Value.Error.Should().BeNull();
        _store.Value.Streaming.Should().BeFalse();
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     A page that closes mid-turn gets no outcome, and the effect dispatches nothing after: no end, no refresh. A
    ///     send for a page already gone dispatches nothing at all.
    ///     Red: drop the cancelled-token guard at the top of SendAsync; the turn number moves.
    ///     Red: drop the page-gone filter on StreamAsync's cancellation catch; the turn ends with "The turn was stopped.".
    ///     Red: dispatch AgentTurnEnded before returning on a page gone in SendAsync; the reply is ended.
    ///     Red: return AfterTurnAsync with no token on a page gone in SendAsync; the list is read and there is an outcome.
    /// </summary>
    [Fact]
    public async Task A_page_gone_mid_turn_dispatches_nothing_more()
    {
        await OpenedAsync();
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var turn = _store.Value.Turn;
        var forGone = await _effects.SendAsync(A, Prompt, gone.Token);
        var turnAfterGone = _store.Value.Turn;
        using var page = new CancellationTokenSource();
        _feed.Send(Delta("Foo"));
        var sending = _effects.SendAsync(A, Prompt, page.Token);
        await UntilAsync(s => s.Reply is { Text: "Foo" });

        await page.CancelAsync();
        var outcome = await sending.WaitAsync(TimeSpan.FromSeconds(5));

        forGone.Should().BeNull();
        turnAfterGone.Should().Be(turn);
        outcome.Should().BeNull();
        _store.Value.Error.Should().BeNull();
        _store.Value.Reply.Should().NotBeNull();
        _handler.Paths.Should().NotContain(SessionsPath);
        _logger.Entries.Should().BeEmpty();
    }

    /// <summary>
    ///     A send while a turn streams is no outcome and starts nothing: the composer is disabled, and Enter must not
    ///     slip a second turn in.
    ///     Red: drop the Streaming guard in SendAsync; a second stream request is made.
    /// </summary>
    [Fact]
    public async Task A_send_while_a_turn_streams_starts_nothing()
    {
        await OpenedAsync();
        var sending = _effects.SendAsync(A, Prompt, CancellationToken.None);
        await UntilAsync(s => s.Streaming);

        var again = _effects.SendAsync(A, "again", CancellationToken.None);
        await Task.WhenAny(again, Task.Delay(TimeSpan.FromMilliseconds(200)));
        _feed.End();
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await again.WaitAsync(TimeSpan.FromSeconds(5));

        second.Should().BeNull();
        _handler.Paths.Count(p => string.Equals(p, StreamA, StringComparison.Ordinal)).Should().Be(1);
    }

    /// <summary>
    ///     The agents and the session list are read for the page.
    ///     Red: drop the AgentsLoaded dispatch in LoadAgentsAsync; no agents.
    ///     Red: drop the AgentSessionsLoaded dispatch in RefreshSessionsAsync; no sessions.
    ///     Red: return null on a success in LoadAgentsAsync; there is no outcome.
    ///     Red: return null on a success in RefreshSessionsAsync; there is no outcome.
    /// </summary>
    [Fact]
    public async Task The_agents_and_sessions_are_read()
    {
        _handler.OnJson(AgentsPath, Json(new[] { new AgentSummaryDto("architect", "Daedalus Architect", "", []) }));

        var agents = await _effects.LoadAgentsAsync(CancellationToken.None);
        var sessions = await _effects.RefreshSessionsAsync(CancellationToken.None);

        agents.Should().Be(AgentOutcome.Done);
        sessions.Should().Be(AgentOutcome.Done);
        _store.Value.Agents.Should().ContainSingle();
        _store.Value.Sessions.Should().ContainSingle();
    }

    /// <summary>
    ///     An agents read whose page closes after its answer arrived, before the effect resumes, stores nothing and is no
    ///     outcome. The client reads the whole answer with no await that could yield, so the close is stepped in: the read
    ///     runs on <see cref="SteppedContext"/>, which queues every continuation, and the page closes just before the
    ///     effect's own continuation runs.
    ///     Red: drop the cancelled-token check on the success branch in LoadAgentsAsync; the agents land and it is Done.
    /// </summary>
    [Fact]
    public async Task An_agents_read_answered_after_its_page_closed_stores_nothing()
    {
        var answer = new TaskCompletionSource<HttpResponseMessage>();
        _handler.On(AgentsPath, () => answer.Task);
        using var closing = new CancellationTokenSource();
        var context = new SteppedContext();
        Task<AgentOutcome?> loading;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            loading = _effects.LoadAgentsAsync(closing.Token);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        answer.SetResult(RecordingHandler.Json(
            HttpStatusCode.OK, Json(new[] { new AgentSummaryDto("architect", "Daedalus Architect", "", []) })));
        await context.RunUntilAsync(nameof(AgentEffects.LoadAgentsAsync), closing.CancelAsync);
        var agents = await loading.WaitAsync(TimeSpan.FromSeconds(5));

        context.Closed.Should().BeTrue("the page must close before the effect resumes, or the test proves nothing");
        agents.Should().BeNull("the page that asked is gone");
        _store.Value.Agents.Should().BeEmpty();
    }

    /// <summary>
    ///     A failed read returns its reason for the page's toast and is logged as a warning; a read whose token cannot be
    ///     refreshed is no outcome and logs nothing.
    ///     Red: return Done on a failure in LoadAgentsAsync; the reason is lost.
    ///     Red: drop the LogReadFailed call in Failed; no warning.
    ///     Red: drop the SigningInAgain check in Failed; the renewal is a refusal, and is logged.
    /// </summary>
    [Fact]
    public async Task A_failed_read_says_why_and_a_renewal_says_nothing()
    {
        _handler.On(AgentsPath, () => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        _handler.On(PathA, SigningIn);

        var agents = await _effects.LoadAgentsAsync(CancellationToken.None);
        var renewal = await _effects.OpenSessionAsync(A, CancellationToken.None);

        agents!.Value.Refusal.Should().Contain("500");
        renewal.Should().BeNull();
        _logger.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Warning);
    }

    /// <summary>
    ///     Opening a session reads it and lands it under the open's turn; an open for a page already gone dispatches
    ///     nothing; opening none reads nothing.
    ///     Red: return null on a success in OpenSessionAsync; there is no outcome.
    ///     Red: drop the cancelled-token guard at the top of OpenSessionAsync; the turn number moves.
    ///     Red: drop the null check in OpenSessionAsync; the read of no session throws.
    ///     Red: return null for no session in OpenSessionAsync; there is no outcome.
    ///     Red: dispatch AgentSessionOpened only for a session in OpenSessionAsync; the page still shows A.
    /// </summary>
    [Fact]
    public async Task Opening_a_session_reads_it()
    {
        var opened = await _effects.OpenSessionAsync(A, CancellationToken.None);
        using var gone = new CancellationTokenSource();
        await gone.CancelAsync();
        var turn = _store.Value.Turn;
        var forGone = await _effects.OpenSessionAsync(B, gone.Token);
        var turnAfterGone = _store.Value.Turn;
        var none = await _effects.OpenSessionAsync(null, CancellationToken.None);

        opened.Should().Be(AgentOutcome.Done);
        forGone.Should().BeNull();
        turnAfterGone.Should().Be(turn);
        none.Should().Be(AgentOutcome.Done);
        _store.Value.SessionId.Should().BeNull();
        _handler.Paths.Should().Equal(PathA);
    }

    /// <summary>
    ///     The header lands for the session opened.
    ///     Red: drop the AgentSessionLoaded dispatch in OpenSessionAsync; the header stays null.
    /// </summary>
    [Fact]
    public async Task An_opened_session_shows_its_header()
    {
        await OpenedAsync();

        _store.Value.Session.Should().Be(HeaderA);
    }

    /// <summary>
    ///     Creating a session returns its id for the page to open and refreshes the list; a refusal returns its reason.
    ///     Red: return Done without the id in CreateSessionAsync; the page has nowhere to go.
    ///     Red: drop the refresh in CreateSessionAsync; the list is not read.
    ///     Red: return Done on a failure in CreateSessionAsync; the reason is lost.
    /// </summary>
    [Fact]
    public async Task Creating_a_session_returns_its_id()
    {
        _handler.OnJson(CreatePath, Json(new CreateAgentSessionResponseDto(B)));

        var created = await _effects.CreateSessionAsync("architect", CancellationToken.None);
        _handler.OnJson(CreatePath, Json(new { detail = Refusal }), HttpStatusCode.Conflict);
        var refused = await _effects.CreateSessionAsync("architect", CancellationToken.None);

        created.Should().Be(new AgentOutcome(null, B));
        _store.Value.Sessions.Should().ContainSingle();
        refused!.Value.Refusal.Should().Be(Refusal);
    }

    /// <summary>
    ///     Closing the shown session marks it Closed and refreshes the list; a refusal returns its reason and leaves it.
    ///     Red: drop the AgentSessionClosed dispatch in CloseSessionAsync; it still reads Idle.
    ///     Red: dispatch AgentSessionClosed on a refusal in CloseSessionAsync; it reads Closed.
    ///     Red: return Done on a refusal in CloseSessionAsync; the reason is lost.
    /// </summary>
    [Fact]
    public async Task Closing_a_session_marks_it_closed()
    {
        await OpenedAsync();
        _handler.OnJson(PathA, Json(new { detail = Refusal }), HttpStatusCode.Conflict);
        var refused = await _effects.CloseSessionAsync(A, CancellationToken.None);
        var stateAfterRefusal = _store.Value.Session!.State;
        _handler.On(PathA, () => new HttpResponseMessage(HttpStatusCode.NoContent));

        var closed = await _effects.CloseSessionAsync(A, CancellationToken.None);

        refused!.Value.Refusal.Should().Be(Refusal);
        stateAfterRefusal.Should().Be("Idle");
        closed.Should().Be(AgentOutcome.Done);
        _store.Value.Session!.State.Should().Be("Closed");
    }

    /// <summary>
    ///     A memories page is read in the shown session's agent context.
    ///     Red: drop the agent context in LoadMemoriesAsync; the read has no agentId and is not routed.
    ///     Red: drop the AgentMemoriesLoaded dispatch in LoadMemoriesAsync; the page stays empty.
    /// </summary>
    [Fact]
    public async Task A_memories_page_is_read_in_the_sessions_agent_context()
    {
        await OpenedAsync();
        _handler.OnJson(MemoriesPath, Json(new PagedResultDto<MemoryDto>([Memory("m1")], 1, 1, 20)));

        var outcome = await _effects.LoadMemoriesAsync(null, 1, CancellationToken.None);

        outcome.Should().Be(AgentOutcome.Done);
        _store.Value.Memories.Should().ContainSingle();
    }

    /// <summary>
    ///     Forgetting archives the memory and drops it without a round trip; forgetting the last of a page steps back
    ///     one page; a refusal returns its reason and keeps it.
    ///     Red: send hard=true in ForgetAsync; the archive route is not hit and the memory stays.
    ///     Red: drop the step back in ForgetAsync; the panel stays on the empty page 2.
    ///     Red: dispatch AgentMemoryForgotten on a refusal in ForgetAsync; the memory goes.
    /// </summary>
    [Fact]
    public async Task Forgetting_the_last_of_a_page_steps_back_one()
    {
        await OpenedAsync();
        var page2 = "/api/agent-memories?page=2&pageSize=20&includeArchived=false&agentId=architect";
        _handler.OnJson(page2, Json(new PagedResultDto<MemoryDto>([Memory("m21")], 21, 2, 20)));
        _handler.OnJson(MemoriesPath, Json(new PagedResultDto<MemoryDto>([Memory("m1")], 20, 1, 20)));
        await _effects.LoadMemoriesAsync(null, 2, CancellationToken.None);
        _handler.OnJson("/api/agent-memories/m21?hard=false", Json(new { detail = Refusal }), HttpStatusCode.Conflict);
        var refused = await _effects.ForgetAsync("m21", null, CancellationToken.None);
        var keptAfterRefusal = _store.Value.Memories.Count;
        _handler.On("/api/agent-memories/m21?hard=false", () => new HttpResponseMessage(HttpStatusCode.NoContent));

        var forgotten = await _effects.ForgetAsync("m21", null, CancellationToken.None);

        refused!.Value.Refusal.Should().Be(Refusal);
        keptAfterRefusal.Should().Be(1);
        forgotten.Should().Be(AgentOutcome.Done);
        _store.Value.MemoriesPage.Should().Be(1);
        _store.Value.Memories.Should().ContainSingle().Which.Id.Should().Be("m1");
    }

    /// <summary>
    ///     A context that queues every continuation and runs them one at a time, without setting itself as current, so no
    ///     continuation runs inline with another. <see cref="RunUntilAsync"/> runs them, and closes the page just before
    ///     the continuation of the effect method it names.
    /// </summary>
    private sealed class SteppedContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

        public bool Closed { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public async Task RunUntilAsync(string method, Func<Task> close)
        {
            while (_queue.TryTake(out var item, TimeSpan.FromSeconds(5)))
            {
                var resumes = item.State is Delegate { Target: { } target }
                    && target.GetType().ToString().Contains($"<{method}>", StringComparison.Ordinal);
                if (resumes && !Closed)
                {
                    await close();
                    Closed = true;
                }

                item.Callback(item.State);
                if (resumes)
                {
                    return;
                }
            }
        }
    }
}
