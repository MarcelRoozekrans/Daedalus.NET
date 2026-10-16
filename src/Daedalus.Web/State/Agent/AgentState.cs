using System.Collections.Immutable;
using Daedalus.Application.DTOs.Agents;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Agent;

/// <summary>
///     The Agent chat page: the agents, the session list, the open session with its transcript and the reply streaming
///     into it, the turn's error, what the turn recalled, and the memories panel's page. Whether a new session is being
///     created, the memories panel is open, its kind filter and the composer's text are the page's own: only the page
///     asks, and it shows its own spinner. Outcomes are not state: each effect returns its refusal and the page toasts it.
///     <para>
///         Lifetime: page-only, cleared by <see cref="AgentClosed"/> when the page is disposed. Only that page reads or
///         loads it, so its clear races no other reader. <paramref name="Turn"/>, <paramref name="SessionsSequence"/> and
///         <paramref name="MemoriesSequence"/> survive the clear, so an answer to a request made before it never lands
///         in the page opened after it.
///     </para>
/// </summary>
/// <param name="Agents">The registered agents, for the picker and the session titles.</param>
/// <param name="Sessions">The caller's sessions, newest first.</param>
/// <param name="SessionId">The session the page shows, or <see langword="null"/> on <c>/agent</c>.</param>
/// <param name="Session">The shown session's header, or <see langword="null"/> before its read landed.</param>
/// <param name="Transcript">The shown session's finished messages: the stored ones and this page's finished turns.</param>
/// <param name="Reply">The assistant reply streaming in, or <see langword="null"/> when no turn runs.</param>
/// <param name="Error">The last turn's error, until it is closed, the session changes or a turn starts.</param>
/// <param name="RecallStatus">What the last turn did with memory, or <see langword="null"/>.</param>
/// <param name="RecalledIds">The ids the last turn's <c>memory-recalled</c> events carried, in order.</param>
/// <param name="Recalled">The last turn's recalled memories the caller can still see.</param>
/// <param name="Memories">The memories panel's page.</param>
/// <param name="MemoriesTotal">The store's row count before visibility: only whether another page exists.</param>
/// <param name="MemoriesPage">The memories panel's page number, from 1.</param>
/// <param name="Turn">
///     The number of the newest session open or turn, never reset. Each open and each send takes the next; a stream
///     event, a read or a recall stamped with any other number, or with another session, is dropped.
/// </param>
/// <param name="Opened">The number of the newest session open; the session's read lands only under it.</param>
/// <param name="SessionsSequence">The number of the newest session list read, never reset.</param>
/// <param name="MemoriesSequence">The number of the newest memories page read, never reset.</param>
[Feature(InitialState = nameof(Initial))]
public readonly partial record struct AgentState(
    IReadOnlyList<AgentSummaryDto> Agents,
    IReadOnlyList<AgentSessionDto> Sessions,
    string? SessionId,
    AgentSessionDto? Session,
    ImmutableList<AgentChatMessage> Transcript,
    AgentChatMessage? Reply,
    AgentError? Error,
    string? RecallStatus,
    ImmutableList<string> RecalledIds,
    ImmutableList<MemoryDto> Recalled,
    ImmutableList<MemoryDto> Memories,
    int MemoriesTotal,
    int MemoriesPage,
    long Turn,
    long Opened,
    long SessionsSequence,
    long MemoriesSequence)
{
    /// <summary>The page with nothing read, numbered from the start.</summary>
    public static AgentState Empty { get; } = new([], [], null, null, [], null, null, null, [], [], [], 0, 1, 0, 0, 0, 0);

    /// <summary>Whether a turn is streaming: "Thinking…", the Stop button and the disabled composer.</summary>
    public bool Streaming => Reply is not null;

    /// <summary>The state before any read.</summary>
    public static AgentState Initial(IServiceProvider services) => Empty;
}
