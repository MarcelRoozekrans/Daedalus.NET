using System.Collections.Immutable;
using Daedalus.Application.DTOs.Agents;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Agent;

/// <summary>
///     The Agent reducers. Pure: no I/O, no clock. A stream event, a turn's end, its header and its recall land only on
///     the session and turn that made them; every other answer only under its own newest request. See
///     <see cref="AgentState"/>.
/// </summary>
public static class AgentReducers
{
    [Reducer]
    public static AgentState On(AgentState s, AgentsLoaded a) => s with { Agents = a.Agents };

    /// <summary>Marks the newest session list read; a start seen out of order never moves the number back.</summary>
    [Reducer]
    public static AgentState On(AgentState s, AgentSessionsReadStarted a) =>
        s with { SessionsSequence = Math.Max(s.SessionsSequence, a.Sequence) };

    [Reducer]
    public static AgentState On(AgentState s, AgentSessionsLoaded a) =>
        a.Sequence == s.SessionsSequence ? s with { Sessions = a.Sessions } : s;

    /// <summary>
    ///     Shows session <c>SessionId</c>, or none, and drops everything of the session shown before: its header,
    ///     transcript, a reply still streaming, its error and what its turn recalled. The agents, the session list and
    ///     the memories panel stay; the page reloads the panel for the new session's agent.
    /// </summary>
    [Reducer]
    public static AgentState On(AgentState s, AgentSessionOpened a) => s with
    {
        SessionId = a.SessionId,
        Session = null,
        Transcript = [],
        Reply = null,
        Error = null,
        RecallStatus = null,
        RecalledIds = [],
        Recalled = [],
        Turn = Math.Max(s.Turn, a.Turn),
        Opened = Math.Max(s.Opened, a.Turn),
    };

    /// <summary>
    ///     Lands the open's read: the header, and the stored messages ahead of any turn sent while the read was in flight.
    /// </summary>
    [Reducer]
    public static AgentState On(AgentState s, AgentSessionLoaded a) =>
        IsShown(s, a.SessionId) && a.Turn == s.Opened
            ? s with
            {
                Session = a.Detail.Session,
                Transcript = [.. a.Detail.Messages.Select(AgentChatMessage.FromDto), .. s.Transcript],
            }
            : s;

    [Reducer]
    public static AgentState On(AgentState s, AgentSessionClosed a) =>
        IsShown(s, a.SessionId) && s.Session is { } session ? s with { Session = session with { State = "Closed" } } : s;

    [Reducer]
    public static AgentState On(AgentState s, AgentSessionRefreshed a) =>
        IsCurrent(s, a.SessionId, a.Turn) ? s with { Session = a.Session } : s;

    /// <summary>
    ///     Adds the person's message, starts an empty reply and takes the turn, clearing the last turn's error and recall
    ///     status. The memories the last turn recalled stay until this turn's are read, as they always did.
    /// </summary>
    [Reducer]
    public static AgentState On(AgentState s, AgentTurnStarted a) =>
        IsShown(s, a.SessionId)
            ? s with
            {
                Transcript = s.Transcript.Add(AgentChatMessage.User(a.Text)),
                Reply = AgentChatMessage.Assistant(),
                Error = null,
                RecallStatus = null,
                RecalledIds = [],
                Turn = Math.Max(s.Turn, a.Turn),
            }
            : s with { Turn = Math.Max(s.Turn, a.Turn) };

    /// <summary>Applies one streamed event to the reply, when it belongs to the shown session's running turn.</summary>
    [Reducer]
    public static AgentState On(AgentState s, AgentTurnEvent a) =>
        IsCurrent(s, a.SessionId, a.Turn) && s.Reply is { } reply ? ApplyEvent(s, reply, a.Event) : s;

    /// <summary>
    ///     Ends the turn: the reply joins the transcript unless it produced nothing, and the end's error shows. An error
    ///     the stream sent itself stays when the end carries none.
    /// </summary>
    [Reducer]
    public static AgentState On(AgentState s, AgentTurnEnded a)
    {
        if (!IsCurrent(s, a.SessionId, a.Turn) || s.Reply is not { } reply)
        {
            return s;
        }

        return s with
        {
            Transcript = reply.IsEmpty ? s.Transcript : s.Transcript.Add(reply),
            Reply = null,
            Error = a.Error ?? s.Error,
        };
    }

    [Reducer]
    public static AgentState On(AgentState s, AgentRecalledLoaded a) =>
        IsCurrent(s, a.SessionId, a.Turn) ? s with { Recalled = [.. a.Memories] } : s;

    [Reducer]
    public static AgentState On(AgentState s, AgentErrorDismissed a) => s with { Error = null };

    /// <summary>Marks the newest memories read; a start seen out of order never moves the number back.</summary>
    [Reducer]
    public static AgentState On(AgentState s, AgentMemoriesReadStarted a) =>
        s with { MemoriesSequence = Math.Max(s.MemoriesSequence, a.Sequence) };

    /// <summary>
    ///     Lands the newest memories page. Its total counts store rows before the visibility filter, so it only answers
    ///     "is there another store page", never "how many can I see".
    /// </summary>
    [Reducer]
    public static AgentState On(AgentState s, AgentMemoriesLoaded a) =>
        a.Sequence == s.MemoriesSequence
            ? s with { Memories = [.. a.Page.Items], MemoriesTotal = a.Page.Total, MemoriesPage = a.Page.Page }
            : s;

    [Reducer]
    public static AgentState On(AgentState s, AgentMemoryForgotten a) => s with
    {
        Memories = s.Memories.RemoveAll(m => string.Equals(m.Id, a.Id, StringComparison.Ordinal)),
        Recalled = s.Recalled.RemoveAll(m => string.Equals(m.Id, a.Id, StringComparison.Ordinal)),
        MemoriesTotal = Math.Max(0, s.MemoriesTotal - 1),
    };

    /// <summary>Clears the page and keeps every number; see <see cref="AgentState"/>.</summary>
    [Reducer]
    public static AgentState On(AgentState s, AgentClosed a) => AgentState.Empty with
    {
        Turn = s.Turn,
        SessionsSequence = s.SessionsSequence,
        MemoriesSequence = s.MemoriesSequence,
    };

    private static bool IsShown(AgentState s, string sessionId) => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal);

    private static bool IsCurrent(AgentState s, string sessionId, long turn) => IsShown(s, sessionId) && turn == s.Turn;

    private static AgentState ApplyEvent(AgentState s, AgentChatMessage reply, AgentEventDto evt)
    {
        switch (evt.Kind)
        {
            case "text-delta":
                return s with { Reply = reply with { Content = reply.Content.Append(evt.Text) } };
            case "tool-call" when evt.ToolCall is { } started:
                return s with { Reply = reply with { ToolCalls = reply.ToolCalls.Add(AgentToolCall.Started(started)) } };
            case "tool-result" when evt.ToolCall is { } finished:
                return s with { Reply = reply with { ToolCalls = Finish(reply.ToolCalls, finished) } };
            case "usage" when evt.Usage is { } usage:
                return s with { Reply = reply with { Usage = usage } };
            case "done" when evt.Result is { } result:
                return s with
                {
                    Reply = reply with
                    {
                        Usage = reply.Usage ?? result.Usage,
                        Content = reply.IsTextEmpty ? AgentText.From(result.Text) : reply.Content,
                    },
                };
            case "error":
                return s with
                {
                    Reply = reply with { Usage = reply.Usage ?? evt.Usage },
                    Error = new AgentError(evt.ErrorCode ?? "Error", evt.ErrorMessage ?? "The turn failed.", evt.ErrorDetail),
                };
            case "memory-recalled" when evt.Memory is { } recalled:
                return s with
                {
                    RecalledIds = recalled.Ids is null ? s.RecalledIds : s.RecalledIds.AddRange(recalled.Ids),
                    RecallStatus = $"{recalled.Count ?? recalled.Ids?.Count ?? 0} memories recalled",
                };
            case "memory-stored" when evt.Memory is { } stored:
                return s with { RecallStatus = stored.Deduped == true ? "Memory updated (duplicate merged)" : "New memory stored" };
            case "memory-recall-failed" when evt.Memory is { } failed:
                return s with { RecallStatus = $"Memory recall unavailable ({failed.Code})" };
            case "memory-index-pending":
                return s with { RecallStatus = "Memory stored; indexing pending (embedding service unavailable)" };
            case "memory-quarantined":
                return s with { RecallStatus = "A recalled memory was quarantined and not shown" };
            default:
                return s;
        }
    }

    /// <summary>Finishes the call <paramref name="finished"/> names, adding it first when the stream never announced it.</summary>
    private static ImmutableList<AgentToolCall> Finish(ImmutableList<AgentToolCall> calls, AgentToolCallDto finished)
    {
        var index = calls.FindIndex(c => string.Equals(c.CallId, finished.CallId, StringComparison.Ordinal));
        return index < 0
            ? calls.Add(AgentToolCall.Started(finished).Finish(finished))
            : calls.SetItem(index, calls[index].Finish(finished));
    }
}
