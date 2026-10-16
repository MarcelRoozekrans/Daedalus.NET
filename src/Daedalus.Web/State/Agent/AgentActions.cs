using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.Agents;

namespace Daedalus.Web.State.Agent;

/// <summary>The registered agents were read.</summary>
public readonly record struct AgentsLoaded(IReadOnlyList<AgentSummaryDto> Agents);

/// <summary>A read of the session list was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct AgentSessionsReadStarted(long Sequence);

/// <summary>Session list read <paramref name="Sequence"/> answered with <paramref name="Sessions"/>.</summary>
public readonly record struct AgentSessionsLoaded(long Sequence, IReadOnlyList<AgentSessionDto> Sessions);

/// <summary>
///     The page opened session <paramref name="SessionId"/>, or none, as turn <paramref name="Turn"/>: everything of the
///     session shown before goes, including a reply still streaming.
/// </summary>
public readonly record struct AgentSessionOpened(string? SessionId, long Turn);

/// <summary>The read of session <paramref name="SessionId"/> made by open <paramref name="Turn"/> answered.</summary>
public readonly record struct AgentSessionLoaded(string SessionId, long Turn, AgentSessionDetailDto Detail);

/// <summary>Session <paramref name="SessionId"/> was closed.</summary>
public readonly record struct AgentSessionClosed(string SessionId);

/// <summary>
///     After turn <paramref name="Turn"/> of session <paramref name="SessionId"/>, the session list gave its new header:
///     its state and turn count.
/// </summary>
public readonly record struct AgentSessionRefreshed(string SessionId, long Turn, AgentSessionDto Session);

/// <summary>The person sent <paramref name="Text"/> to session <paramref name="SessionId"/>, starting turn <paramref name="Turn"/>.</summary>
public readonly record struct AgentTurnStarted(string SessionId, long Turn, string Text);

/// <summary>Turn <paramref name="Turn"/> of session <paramref name="SessionId"/> streamed <paramref name="Event"/>: one action per event.</summary>
public readonly record struct AgentTurnEvent(string SessionId, long Turn, AgentEventDto Event);

/// <summary>
///     Turn <paramref name="Turn"/> of session <paramref name="SessionId"/> ended: its stream finished, was stopped or
///     failed with <paramref name="Error"/>. An error the stream itself sent is kept when this carries none.
/// </summary>
public readonly record struct AgentTurnEnded(string SessionId, long Turn, AgentError? Error);

/// <summary>The memories recalled by turn <paramref name="Turn"/> of session <paramref name="SessionId"/> were read.</summary>
public readonly record struct AgentRecalledLoaded(string SessionId, long Turn, IReadOnlyList<MemoryDto> Memories);

/// <summary>The person closed the error alert.</summary>
public readonly record struct AgentErrorDismissed;

/// <summary>A read of a memories page was started, numbered <paramref name="Sequence"/>.</summary>
public readonly record struct AgentMemoriesReadStarted(long Sequence);

/// <summary>Memories read <paramref name="Sequence"/> answered with <paramref name="Page"/>.</summary>
public readonly record struct AgentMemoriesLoaded(long Sequence, PagedResultDto<MemoryDto> Page);

/// <summary>Memory <paramref name="Id"/> was forgotten: it leaves both lists without a round trip.</summary>
public readonly record struct AgentMemoryForgotten(string Id);

/// <summary>The Agent page closed: what it showed goes.</summary>
public readonly record struct AgentClosed;
