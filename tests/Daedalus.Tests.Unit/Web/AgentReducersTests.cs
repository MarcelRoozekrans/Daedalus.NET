using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;
using Daedalus.Web.State.Agent;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The Agent reducers: the session open and read, the streamed turn with its session and turn guard, the memories
///     panel, and the close. The turn tests replay events in the order the API streams them.
/// </summary>
public sealed class AgentReducersTests
{
    private const string A = "session-a";
    private const string B = "session-b";

    private static readonly AgentSessionDto HeaderA = new(A, "architect", "owner", "Idle", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, 10, 5);

    private static readonly AgentToolCallDto Call = new("call-1", "roslyn__find_callers", "{\"symbol\":\"Foo\"}", null);

    private static readonly AgentToolCallDto CallDone = Call with { ResultPreview = "3 callers", Succeeded = true };

    private static readonly TurnUsageDto Usage = new(12, 7, "stub-model");

    private static readonly AgentSessionDetailDto DetailA = new(HeaderA,
    [
        new AgentMessageDto("user", "earlier question", [], DateTimeOffset.UnixEpoch),
        new AgentMessageDto("assistant", "earlier answer", [CallDone], DateTimeOffset.UnixEpoch),
    ]);

    private static MemoryDto Memory(string id) => new(
        id, "owner", null, "fact", $"memory {id}", [], "agent", 0.5, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null, 0, false,
        false, false);

    private static AgentState Initial => AgentState.Initial(null!);

    /// <summary>Session <paramref name="sessionId"/> opened as turn <paramref name="turn"/>, from <paramref name="state"/>.</summary>
    private static AgentState Opened(AgentState state, string sessionId, long turn) =>
        AgentReducers.On(state, new AgentSessionOpened(sessionId, turn));

    /// <summary>Session A opened as turn 1 with its stored transcript, then turn 2 started with "hi".</summary>
    private static AgentState Streaming()
    {
        var loaded = AgentReducers.On(Opened(Initial, A, 1), new AgentSessionLoaded(A, 1, DetailA));
        return AgentReducers.On(loaded, new AgentTurnStarted(A, 2, "hi"));
    }

    /// <summary>Plays <paramref name="events"/> as turn <paramref name="turn"/> of session <paramref name="sessionId"/>.</summary>
    private static AgentState Play(AgentState state, string sessionId, long turn, params AgentEventDto[] events) =>
        events.Aggregate(state, (s, e) => AgentReducers.On(s, new AgentTurnEvent(sessionId, turn, e)));

    private static AgentEventDto Delta(string text) => new("text-delta", Text: text);

    /// <summary>
    ///     Opening a session takes its turn number and drops the session shown before, its reply, error and recall; the
    ///     agents, the session list and the memories panel stay. An open seen out of order never moves the number back.
    ///     Red: leave SessionId unset in the AgentSessionOpened reducer; it stays A.
    ///     Red: keep Transcript in the AgentSessionOpened reducer; A's messages stay.
    ///     Red: keep Reply in the AgentSessionOpened reducer; A's reply is still streaming in B.
    ///     Red: keep Error in the AgentSessionOpened reducer; A's error shows on B.
    ///     Red: keep Recalled in the AgentSessionOpened reducer; B inherits A's "recalled this turn".
    ///     Red: keep RecallStatus in the AgentSessionOpened reducer; B inherits A's recall status.
    ///     Red: reset Memories in the AgentSessionOpened reducer; the open panel empties.
    ///     Red: set Turn to the action's in the AgentSessionOpened reducer; an out-of-order open moves it back to 3.
    /// </summary>
    [Fact]
    public void Opening_a_session_drops_the_one_shown_before()
    {
        var shown = Play(Streaming(), A, 2, new AgentEventDto("memory-stored", Memory: new MemoryEventDto(Deduped: false))) with
        {
            Error = AgentError.Cancelled,
            Recalled = [Memory("m1")],
            Memories = [Memory("m2")],
            Sessions = [HeaderA],
        };

        var opened = Opened(shown, B, 4);

        opened.SessionId.Should().Be(B);
        opened.Session.Should().BeNull();
        opened.Transcript.Should().BeEmpty();
        opened.Reply.Should().BeNull();
        opened.Error.Should().BeNull();
        opened.Recalled.Should().BeEmpty();
        opened.RecallStatus.Should().BeNull();
        opened.Memories.Should().ContainSingle();
        opened.Sessions.Should().ContainSingle();
        opened.Turn.Should().Be(4);
        Opened(opened, A, 3).Turn.Should().Be(4);
    }

    /// <summary>
    ///     The open's read lands its header and transcript, with the stored tool calls finished; a read for another
    ///     session, or made by an earlier open, changes nothing.
    ///     Red: drop the SessionId check in the AgentSessionLoaded reducer; B's read lands on A.
    ///     Red: drop the open check in the AgentSessionLoaded reducer; the earlier open's read lands.
    ///     Red: build the stored tool calls with Started in AgentChatMessage.FromDto; the card says running.
    /// </summary>
    [Fact]
    public void The_opens_read_lands_its_transcript()
    {
        var opened = Opened(Opened(Initial, A, 1), A, 2);

        var loaded = AgentReducers.On(opened, new AgentSessionLoaded(A, 2, DetailA));

        loaded.Session.Should().Be(HeaderA);
        loaded.Transcript.Select(m => (m.IsUser, m.Text)).Should().Equal((true, "earlier question"), (false, "earlier answer"));
        loaded.Transcript[1].ToolCalls.Should().ContainSingle().Which.Status.Should().Be("succeeded");
        AgentReducers.On(opened, new AgentSessionLoaded(B, 2, DetailA)).Session.Should().BeNull();
        AgentReducers.On(opened, new AgentSessionLoaded(A, 1, DetailA)).Session.Should().BeNull();
    }

    /// <summary>
    ///     A turn sent before the open's read landed keeps its messages, after the stored ones.
    ///     Red: replace Transcript with the stored messages in the AgentSessionLoaded reducer; "hi" goes.
    /// </summary>
    [Fact]
    public void A_turn_sent_before_the_read_landed_keeps_its_messages()
    {
        var sent = AgentReducers.On(Opened(Initial, A, 1), new AgentTurnStarted(A, 2, "hi"));
        var ended = AgentReducers.On(Play(sent, A, 2, Delta("hello")), new AgentTurnEnded(A, 2, null));

        var loaded = AgentReducers.On(ended, new AgentSessionLoaded(A, 1, DetailA));

        loaded.Transcript.Select(m => m.Text).Should().Equal("earlier question", "earlier answer", "hi", "hello");
    }

    /// <summary>
    ///     A send adds the person's message, starts an empty reply, takes the turn and clears the last error and recall
    ///     status; the memories recalled by the last turn stay until this turn's are read.
    ///     Red: drop the user message in the AgentTurnStarted reducer; the transcript keeps two messages.
    ///     Red: leave Reply null in the AgentTurnStarted reducer; nothing streams.
    ///     Red: keep Error in the AgentTurnStarted reducer; the last turn's error stays.
    ///     Red: keep RecallStatus in the AgentTurnStarted reducer; the last turn's status stays.
    ///     Red: keep RecalledIds in the AgentTurnStarted reducer; the last turn's ids stay.
    ///     Red: clear Recalled in the AgentTurnStarted reducer; the panel empties before this turn's recall.
    ///     Red: leave Turn unset in the AgentTurnStarted reducer; it stays 1.
    /// </summary>
    [Fact]
    public void A_send_starts_a_reply_and_clears_the_last_turns_error()
    {
        var loaded = AgentReducers.On(Opened(Initial, A, 1), new AgentSessionLoaded(A, 1, DetailA)) with
        {
            Error = AgentError.Cancelled,
            RecallStatus = "New memory stored",
            RecalledIds = ["m1"],
            Recalled = [Memory("m1")],
        };

        var started = AgentReducers.On(loaded, new AgentTurnStarted(A, 2, "hi"));

        started.Transcript.Should().HaveCount(3);
        started.Transcript[^1].Should().BeEquivalentTo(AgentChatMessage.User("hi"));
        started.Reply.Should().BeEquivalentTo(AgentChatMessage.Assistant());
        started.Streaming.Should().BeTrue();
        started.Error.Should().BeNull();
        started.RecallStatus.Should().BeNull();
        started.RecalledIds.Should().BeEmpty();
        started.Recalled.Should().ContainSingle();
        started.Turn.Should().Be(2);
    }

    /// <summary>
    ///     A send for a session the page no longer shows adds nothing, and still never moves the turn back.
    ///     Red: drop the SessionId check in the AgentTurnStarted reducer; "hi" lands on B.
    /// </summary>
    [Fact]
    public void A_send_for_another_session_adds_nothing()
    {
        var onB = Opened(Initial, B, 3);

        var started = AgentReducers.On(onB, new AgentTurnStarted(A, 2, "hi"));

        started.Transcript.Should().BeEmpty();
        started.Reply.Should().BeNull();
        started.Turn.Should().Be(3);
    }

    /// <summary>
    ///     A recorded stream, replayed one action per event as the API sends it: recall, text in chunks, a tool call and
    ///     its result, more text, usage, a stored memory and done. The reply grows chunk by chunk; the end moves it into
    ///     the transcript with its tool card and usage.
    ///     Red: drop the text-delta case in ApplyEvent; the text stays empty.
    ///     Red: drop the tool-call case in ApplyEvent; the card is added only by its result, so the mid-stream card is missing.
    ///     Red: leave the call running in the tool-result case of ApplyEvent; the card says running.
    ///     Red: drop the usage case in ApplyEvent; the usage is null.
    ///     Red: drop the memory-recalled ids in ApplyEvent; no ids are kept.
    ///     Red: drop the memory-stored case in ApplyEvent; the status still reads "2 memories recalled".
    ///     Red: leave Reply set in the AgentTurnEnded reducer; the turn never ends.
    ///     Red: drop the append in the AgentTurnEnded reducer; the reply is lost.
    /// </summary>
    [Fact]
    public void A_recorded_stream_replays_into_the_transcript()
    {
        var recorded = new[]
        {
            new AgentEventDto("memory-recalled", Memory: new MemoryEventDto(Count: 2, Ids: ["m1", "m2"])),
            Delta("Let me "),
            Delta("look."),
            new AgentEventDto("tool-call", ToolCall: Call),
            new AgentEventDto("tool-result", ToolCall: CallDone),
            Delta(" Foo has "),
            Delta("3 callers."),
            new AgentEventDto("usage", Usage: Usage),
            new AgentEventDto("memory-stored", Memory: new MemoryEventDto(MemoryId: "m3", Deduped: false)),
            new AgentEventDto("done", Result: new AgentTurnResultDto("t1", "Let me look. Foo has 3 callers.", Usage, [CallDone], 5)),
        };

        var midway = Play(Streaming(), A, 2, recorded[..4]);
        var played = Play(midway, A, 2, recorded[4..]);
        var ended = AgentReducers.On(played, new AgentTurnEnded(A, 2, null));

        midway.Reply!.Text.Should().Be("Let me look.");
        midway.Reply.ToolCalls.Should().ContainSingle().Which.Status.Should().Be("running");
        ended.Reply.Should().BeNull();
        ended.Streaming.Should().BeFalse();
        var reply = ended.Transcript[^1];
        reply.IsUser.Should().BeFalse();
        reply.Text.Should().Be("Let me look. Foo has 3 callers.");
        reply.ToolCalls.Should().ContainSingle().Which.Should().Be(AgentToolCall.FromHistory(CallDone));
        reply.Usage.Should().Be(Usage);
        ended.Transcript[^2].Text.Should().Be("hi");
        ended.RecalledIds.Should().Equal("m1", "m2");
        ended.RecallStatus.Should().Be("New memory stored");
        ended.Error.Should().BeNull();
    }

    /// <summary>
    ///     A late chunk of session A's turn, arriving after the page switched to session B, is ignored: B shows nothing
    ///     of A, and A's stop never puts "The turn was stopped." on B.
    ///     Red: drop the session and turn check for AgentTurnEvent; A's text streams into B.
    ///     Red: drop the session and turn check in the AgentTurnEnded reducer; A's stop shows on B.
    /// </summary>
    [Fact]
    public void A_late_chunk_after_a_session_switch_is_ignored()
    {
        var onB = Opened(Play(Streaming(), A, 2, Delta("Let me")), B, 3);

        var late = Play(onB, A, 2, Delta(" look."), new AgentEventDto("tool-call", ToolCall: Call));
        var stopped = AgentReducers.On(late, new AgentTurnEnded(A, 2, AgentError.Cancelled));

        stopped.Should().Be(onB);
        stopped.Reply.Should().BeNull();
        stopped.Transcript.Should().BeEmpty();
        stopped.Error.Should().BeNull();
    }

    /// <summary>
    ///     A late chunk of an earlier turn of the same session, after the session was opened again, is ignored too.
    ///     Red: check only the session id for AgentTurnEvent; turn 2's text streams into turn 4.
    /// </summary>
    [Fact]
    public void A_late_chunk_of_an_earlier_turn_is_ignored()
    {
        var again = AgentReducers.On(Opened(Streaming(), A, 3), new AgentTurnStarted(A, 4, "again"));

        var late = Play(again, A, 2, Delta("old"));

        late.Reply!.Text.Should().BeEmpty();
    }

    /// <summary>
    ///     A stopped or failed turn keeps the text that streamed and shows the error; an error event the stream sent
    ///     itself is kept by the end, with the usage it carried.
    ///     Red: drop the error in the AgentTurnEnded reducer; the stop shows nothing.
    ///     Red: set Error to the action's even when it carries none in the AgentTurnEnded reducer; the stream's error goes.
    ///     Red: drop the error case in ApplyEvent; the quarantine shows nothing.
    ///     Red: drop the usage of the error case in ApplyEvent; the usage is null.
    /// </summary>
    [Fact]
    public void An_ended_turn_keeps_its_text_and_shows_its_error()
    {
        var stopped = AgentReducers.On(Play(Streaming(), A, 2, Delta("Let me")), new AgentTurnEnded(A, 2, AgentError.Cancelled));
        var quarantine = new AgentEventDto("error", Usage: Usage, ErrorCode: "Quarantined", ErrorMessage: "Blocked.", ErrorDetail: "PI-1");

        var refused = AgentReducers.On(Play(Streaming(), A, 2, Delta("Let"), quarantine), new AgentTurnEnded(A, 2, null));

        stopped.Transcript[^1].Text.Should().Be("Let me");
        stopped.Error.Should().Be(AgentError.Cancelled);
        refused.Error.Should().Be(new AgentError("Quarantined", "Blocked.", "PI-1"));
        refused.Error!.Title.Should().Be("Quarantined by AI Sentinel");
        refused.Transcript[^1].Usage.Should().Be(Usage);
    }

    /// <summary>
    ///     A reply that produced nothing is not kept; a done event fills a reply that streamed no text with the result's.
    ///     Red: append the reply even when empty in the AgentTurnEnded reducer; an empty bubble is kept.
    ///     Red: drop the result text in the done case of ApplyEvent; the reply stays empty and is dropped.
    ///     Red: drop the result usage in the done case of ApplyEvent; the usage is null.
    /// </summary>
    [Fact]
    public void An_empty_reply_goes_and_done_fills_a_silent_one()
    {
        var empty = AgentReducers.On(Streaming(), new AgentTurnEnded(A, 2, AgentError.Cancelled));
        var done = new AgentEventDto("done", Result: new AgentTurnResultDto("t1", "All done.", Usage, [], 5));

        var filled = AgentReducers.On(Play(Streaming(), A, 2, done), new AgentTurnEnded(A, 2, null));

        empty.Transcript[^1].Text.Should().Be("hi");
        filled.Transcript[^1].Text.Should().Be("All done.");
        filled.Transcript[^1].Usage.Should().Be(Usage);
    }

    /// <summary>
    ///     A tool result whose call the stream never announced still shows its card, finished.
    ///     Red: drop the add-if-missing branch in the tool-result case of ApplyEvent; no card.
    /// </summary>
    [Fact]
    public void A_result_for_an_unannounced_call_still_shows_its_card()
    {
        var played = Play(Streaming(), A, 2, new AgentEventDto("tool-result", ToolCall: CallDone with { Succeeded = false }));

        played.Reply!.ToolCalls.Should().ContainSingle().Which.Status.Should().Be("failed");
    }

    /// <summary>
    ///     Each memory event sets the recall status the page has always shown.
    ///     Red: change any one status text in ApplyEvent; its case fails.
    /// </summary>
    [Theory]
    [InlineData("memory-recalled", "3 memories recalled")]
    [InlineData("memory-stored", "Memory updated (duplicate merged)")]
    [InlineData("memory-recall-failed", "Memory recall unavailable (Timeout)")]
    [InlineData("memory-index-pending", "Memory stored; indexing pending (embedding service unavailable)")]
    [InlineData("memory-quarantined", "A recalled memory was quarantined and not shown")]
    public void Each_memory_event_sets_its_status(string kind, string status)
    {
        var memory = new MemoryEventDto(Count: 3, Deduped: true, Code: "Timeout");

        Play(Streaming(), A, 2, new AgentEventDto(kind, Memory: memory)).RecallStatus.Should().Be(status);
    }

    /// <summary>
    ///     After a turn, the session list's header for the shown session replaces the one read on open; a header for
    ///     another session, or from an earlier turn, changes nothing.
    ///     Red: drop the session check in the AgentSessionRefreshed reducer; B's header lands on A.
    ///     Red: drop the turn check in the AgentSessionRefreshed reducer; turn 1's header lands after turn 2 started.
    /// </summary>
    [Fact]
    public void A_turns_header_lands_on_its_session()
    {
        var ended = AgentReducers.On(Streaming(), new AgentTurnEnded(A, 2, null));
        var after = HeaderA with { TurnCount = 2 };

        ended = AgentReducers.On(ended, new AgentSessionRefreshed(A, 2, after));

        ended.Session.Should().Be(after);
        AgentReducers.On(ended, new AgentSessionRefreshed(B, 2, HeaderA with { Id = B })).Session.Should().Be(after);
        AgentReducers.On(ended, new AgentSessionRefreshed(A, 1, HeaderA)).Session.Should().Be(after);
    }

    /// <summary>
    ///     The turn's recalled memories land for that turn only.
    ///     Red: drop the session and turn check in the AgentRecalledLoaded reducer; A's memories land on B.
    ///     Red: leave Recalled unset in the AgentRecalledLoaded reducer; nothing lands.
    /// </summary>
    [Fact]
    public void The_turns_recalled_memories_land_for_that_turn()
    {
        var ended = AgentReducers.On(Streaming(), new AgentTurnEnded(A, 2, null));

        var recalled = AgentReducers.On(ended, new AgentRecalledLoaded(A, 2, [Memory("m1")]));
        var onB = AgentReducers.On(Opened(ended, B, 3), new AgentRecalledLoaded(A, 2, [Memory("m1")]));

        recalled.Recalled.Should().ContainSingle().Which.Id.Should().Be("m1");
        onB.Recalled.Should().BeEmpty();
    }

    /// <summary>
    ///     Closing the shown session marks it Closed; closing another changes nothing.
    ///     Red: drop the SessionId check in the AgentSessionClosed reducer; A reads Closed after B was closed.
    ///     Red: return the state unchanged in the AgentSessionClosed reducer; A still reads Idle.
    /// </summary>
    [Fact]
    public void Closing_the_shown_session_marks_it_closed()
    {
        var loaded = AgentReducers.On(Opened(Initial, A, 1), new AgentSessionLoaded(A, 1, DetailA));

        AgentReducers.On(loaded, new AgentSessionClosed(A)).Session!.State.Should().Be("Closed");
        AgentReducers.On(loaded, new AgentSessionClosed(B)).Session!.State.Should().Be("Idle");
    }

    /// <summary>
    ///     Closing the alert clears the error.
    ///     Red: return the state unchanged in the AgentErrorDismissed reducer; the error stays.
    /// </summary>
    [Fact]
    public void Closing_the_alert_clears_the_error()
    {
        AgentReducers.On(Initial with { Error = AgentError.Cancelled }, default(AgentErrorDismissed)).Error.Should().BeNull();
    }

    /// <summary>
    ///     The agents land; the newest session list read lands and an older one changes nothing; a start seen out of
    ///     order never moves the number back.
    ///     Red: return the state unchanged in the AgentsLoaded reducer; no agents.
    ///     Red: drop the sequence check in the AgentSessionsLoaded reducer; the older list lands.
    ///     Red: set SessionsSequence to the action's in the AgentSessionsReadStarted reducer; it moves back to 1.
    /// </summary>
    [Fact]
    public void The_newest_session_list_lands()
    {
        var agents = AgentReducers.On(Initial, new AgentsLoaded([new AgentSummaryDto("architect", "Daedalus Architect", "", [])]));
        var started = AgentReducers.On(AgentReducers.On(agents, new AgentSessionsReadStarted(2)), new AgentSessionsReadStarted(1));

        var loaded = AgentReducers.On(started, new AgentSessionsLoaded(2, [HeaderA]));

        agents.Agents.Should().ContainSingle();
        started.SessionsSequence.Should().Be(2);
        loaded.Sessions.Should().ContainSingle();
        AgentReducers.On(loaded, new AgentSessionsLoaded(1, [])).Sessions.Should().ContainSingle();
    }

    /// <summary>
    ///     The newest memories read lands its page, total and number; an older one changes nothing.
    ///     Red: drop the sequence check in the AgentMemoriesLoaded reducer; the older page lands.
    ///     Red: leave MemoriesTotal unset in the AgentMemoriesLoaded reducer; it stays 0.
    ///     Red: leave MemoriesPage unset in the AgentMemoriesLoaded reducer; it stays 1.
    ///     Red: set MemoriesSequence to the action's in the AgentMemoriesReadStarted reducer; it moves back to 1.
    /// </summary>
    [Fact]
    public void The_newest_memories_page_lands()
    {
        var started = AgentReducers.On(AgentReducers.On(Initial, new AgentMemoriesReadStarted(2)), new AgentMemoriesReadStarted(1));

        var loaded = AgentReducers.On(started, new AgentMemoriesLoaded(2, new PagedResultDto<MemoryDto>([Memory("m1")], 41, 2, 20)));

        started.MemoriesSequence.Should().Be(2);
        loaded.Memories.Should().ContainSingle();
        loaded.MemoriesTotal.Should().Be(41);
        loaded.MemoriesPage.Should().Be(2);
        AgentReducers.On(loaded, new AgentMemoriesLoaded(1, new PagedResultDto<MemoryDto>([], 0, 1, 20))).Memories.Should().ContainSingle();
    }

    /// <summary>
    ///     A forgotten memory leaves the page and the recalled list, and the total drops by one, never below 0.
    ///     Red: drop the Memories removal in the AgentMemoryForgotten reducer; it stays on the page.
    ///     Red: drop the Recalled removal in the AgentMemoryForgotten reducer; it stays under "Recalled this turn".
    ///     Red: drop the Math.Max in the AgentMemoryForgotten reducer; the total goes to -1.
    /// </summary>
    [Fact]
    public void A_forgotten_memory_leaves_both_lists()
    {
        var shown = Initial with { Memories = [Memory("m1"), Memory("m2")], Recalled = [Memory("m1")], MemoriesTotal = 2 };

        var forgotten = AgentReducers.On(shown, new AgentMemoryForgotten("m1"));

        forgotten.Memories.Select(m => m.Id).Should().Equal("m2");
        forgotten.Recalled.Should().BeEmpty();
        forgotten.MemoriesTotal.Should().Be(1);
        AgentReducers.On(Initial with { Memories = [Memory("m1")] }, new AgentMemoryForgotten("m1")).MemoriesTotal.Should().Be(0);
    }

    /// <summary>
    ///     Closing the page clears what it showed and keeps every number.
    ///     Red: return the state unchanged in the AgentClosed reducer; the transcript is still held.
    ///     Red: return AgentState.Empty in the AgentClosed reducer; the numbers go back to 0.
    /// </summary>
    [Fact]
    public void The_close_clears_the_page_and_keeps_the_numbers()
    {
        var held = Streaming() with { SessionsSequence = 5, MemoriesSequence = 6 };

        var closed = AgentReducers.On(held, default(AgentClosed));

        closed.Should().Be(AgentState.Empty with { Turn = 2, SessionsSequence = 5, MemoriesSequence = 6 });
    }
}
