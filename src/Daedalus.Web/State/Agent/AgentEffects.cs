using Daedalus.Application.DTOs.Agents;
using Daedalus.Web.Auth;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Flux;

namespace Daedalus.Web.State.Agent;

/// <summary>
///     The Agent page's I/O: the agents, the session list, a session's open, create and close, the memories panel, and
///     the streamed turn. Each returns its outcome, done or refused with the API's reason, for the page to toast; a
///     session being renewed, or a caller gone, is no outcome, <see langword="null"/>. A caller already gone dispatches
///     nothing, and an answer whose caller went while it was in flight stores nothing. A session being renewed stores
///     nothing and logs nothing. Every other failure is logged as a warning with its text only: never the person's
///     message, the model's reply, a tool's output or a memory.
///     <para>
///         A turn dispatches one <see cref="AgentTurnEvent"/> per streamed event, stamped with its session and turn, so
///         the reducer drops what a stopped turn or another session sends late. The effect owns the turn's
///         cancellation: <see cref="StopTurn"/> cancels it, and so does opening another session.
///     </para>
/// </summary>
public sealed partial class AgentEffects(
    AgentApiClient api, IDispatcher dispatcher, IStore<AgentState> agent, ILogger<AgentEffects> logger)
{
    private CancellationTokenSource? _turn;

    /// <summary>Reads the registered agents; an answer after <paramref name="ct"/> was cancelled stores nothing.</summary>
    public async Task<AgentOutcome?> LoadAgentsAsync(CancellationToken ct)
    {
        var result = await api.GetAgentsAsync(ct);
        if (result.IsSuccess && !ct.IsCancellationRequested)
        {
            await dispatcher.DispatchAsync(new AgentsLoaded(result.Value));
            return AgentOutcome.Done;
        }

        return result.IsSuccess ? null : Failed(result.Error, ct);
    }

    /// <summary>Reads the caller's sessions.</summary>
    public async Task<AgentOutcome?> RefreshSessionsAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        // Read and dispatched with no await between, so the next read takes the number after this one's.
        var sequence = agent.Value.SessionsSequence + 1;
        await dispatcher.DispatchAsync(new AgentSessionsReadStarted(sequence));
        var result = await api.ListSessionsAsync(ct: ct);
        if (result.IsSuccess && !ct.IsCancellationRequested)
        {
            await dispatcher.DispatchAsync(new AgentSessionsLoaded(sequence, result.Value));
            return AgentOutcome.Done;
        }

        return result.IsSuccess ? null : Failed(result.Error, ct);
    }

    /// <summary>
    ///     Shows session <paramref name="sessionId"/>, or none, and reads it. A turn still streaming is stopped; nothing it
    ///     sends after lands here.
    /// </summary>
    public async Task<AgentOutcome?> OpenSessionAsync(string? sessionId, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        // Opened before the stop, so the stopped turn's end is already stamped with an old turn when it lands.
        var turn = agent.Value.Turn + 1;
        await dispatcher.DispatchAsync(new AgentSessionOpened(sessionId, turn));
        StopTurn();
        if (sessionId is null)
        {
            return AgentOutcome.Done;
        }

        var result = await api.GetSessionAsync(sessionId, ct);
        if (result.IsSuccess && !ct.IsCancellationRequested)
        {
            await dispatcher.DispatchAsync(new AgentSessionLoaded(sessionId, turn, result.Value));
            return AgentOutcome.Done;
        }

        return result.IsSuccess ? null : Failed(result.Error, ct);
    }

    /// <summary>Creates a session for agent <paramref name="agentId"/>; the outcome names it, for the page to open.</summary>
    public async Task<AgentOutcome?> CreateSessionAsync(string agentId, CancellationToken ct)
    {
        var created = await api.CreateSessionAsync(agentId, ct);
        if (created.IsFailure)
        {
            return Failed(created.Error, ct);
        }

        var refreshed = await RefreshSessionsAsync(ct);
        return refreshed is null ? null : refreshed.Value with { SessionId = created.Value };
    }

    /// <summary>Closes session <paramref name="sessionId"/>; the list is read again and the shown header says Closed.</summary>
    public async Task<AgentOutcome?> CloseSessionAsync(string sessionId, CancellationToken ct)
    {
        var closed = await api.CloseSessionAsync(sessionId, ct);
        if (closed.IsFailure)
        {
            return Failed(closed.Error, ct);
        }

        var refreshed = await RefreshSessionsAsync(ct);
        if (!ct.IsCancellationRequested)
        {
            await dispatcher.DispatchAsync(new AgentSessionClosed(sessionId));
        }

        return refreshed;
    }

    /// <summary>
    ///     Reads memories page <paramref name="page"/>, of kind <paramref name="kind"/> or all, in the shown session's agent
    ///     context: that adds the agent's pinned memories to the caller's own and the shared owner's.
    /// </summary>
    public async Task<AgentOutcome?> LoadMemoriesAsync(string? kind, int page, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        var sequence = agent.Value.MemoriesSequence + 1;
        await dispatcher.DispatchAsync(new AgentMemoriesReadStarted(sequence));
        var result = await api.ListMemoriesAsync(
            kind: kind, agentId: agent.Value.Session?.AgentId, page: page, pageSize: AgentView.MemoriesPageSize, ct: ct);
        if (result.IsSuccess && !ct.IsCancellationRequested)
        {
            await dispatcher.DispatchAsync(new AgentMemoriesLoaded(sequence, result.Value));
            return AgentOutcome.Done;
        }

        return result.IsSuccess ? null : Failed(result.Error, ct);
    }

    /// <summary>
    ///     Archives memory <paramref name="id"/>, which is recoverable, and drops it from both lists without a round trip.
    ///     Forgetting the last of a page steps back one, read with <paramref name="kind"/>.
    /// </summary>
    public async Task<AgentOutcome?> ForgetAsync(string id, string? kind, CancellationToken ct)
    {
        var result = await api.ForgetMemoryAsync(id, hard: false, ct);
        if (result.IsFailure)
        {
            return Failed(result.Error, ct);
        }

        if (ct.IsCancellationRequested)
        {
            return null;
        }

        await dispatcher.DispatchAsync(new AgentMemoryForgotten(id));
        var state = agent.Value;
        return state.Memories.IsEmpty && state.MemoriesPage > 1
            ? await LoadMemoriesAsync(kind, state.MemoriesPage - 1, ct)
            : AgentOutcome.Done;
    }

    /// <summary>
    ///     Runs a turn: sends <paramref name="text"/> to session <paramref name="sessionId"/> and streams the reply in, one
    ///     action per event. A stop, a lost connection or an unexpected failure ends the turn with its error, as the page
    ///     always showed it. After the turn the session list is read again and, if the page still shows the turn, the
    ///     session's header and what the turn recalled. A caller gone, or a session being renewed, ends nothing more.
    /// </summary>
    public async Task<AgentOutcome?> SendAsync(string sessionId, string text, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || agent.Value.Streaming)
        {
            return null;
        }

        var turn = agent.Value.Turn + 1;
        await dispatcher.DispatchAsync(new AgentTurnStarted(sessionId, turn, text));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _turn = cts;
        AgentError? error;
        try
        {
            error = await StreamAsync(sessionId, turn, text, ct, cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The page went: nothing left to show.
            return null;
        }
        catch (AccessTokenNotAvailableException)
        {
            // The client's SessionExpiryHandler is already sending the person to sign in; the layout says so.
            await dispatcher.DispatchAsync(new AgentTurnEnded(sessionId, turn, null));
            return null;
        }
        finally
        {
            if (ReferenceEquals(_turn, cts))
            {
                _turn = null;
            }
        }

        await dispatcher.DispatchAsync(new AgentTurnEnded(sessionId, turn, error));
        return await AfterTurnAsync(sessionId, turn, ct);
    }

    /// <summary>Stops the turn streaming, if any; its end shows "The turn was stopped." when the page still shows it.</summary>
    public void StopTurn()
    {
        if (_turn is { IsCancellationRequested: false } turn)
        {
            turn.Cancel();
        }
    }

    /// <summary>
    ///     Streams the turn and returns the error that ended it, or <see langword="null"/> when it finished. A stop, or a
    ///     cancellation the page did not cause, is "The turn was stopped."; the page going, or a session being renewed,
    ///     propagates to the caller.
    /// </summary>
    private async Task<AgentError?> StreamAsync(
        string sessionId, long turn, string text, CancellationToken page, CancellationToken token)
    {
        try
        {
            await foreach (var evt in api.StreamTurnAsync(sessionId, text, token))
            {
                await dispatcher.DispatchAsync(new AgentTurnEvent(sessionId, turn, evt));
            }

            return null;
        }
        catch (OperationCanceledException) when (!page.IsCancellationRequested)
        {
            return AgentError.Cancelled;
        }
        catch (HttpRequestException ex)
        {
            LogTurnTransportFailed(logger, ex.Message);
            return new AgentError("Transport", $"Connection lost: {ex.Message}", null);
        }
#pragma warning disable CA1031 // A failed turn must never take the page down: surface it, keep the composer usable.
        catch (Exception ex) when (ex is not OperationCanceledException and not AccessTokenNotAvailableException)
#pragma warning restore CA1031
        {
            // The resilience handler's rejections (an open circuit, its timeout) land here, as they always did.
            LogTurnFailed(logger, ex.GetType().Name);
            return new AgentError("Error", $"Unexpected error while streaming: {ex.GetType().Name}", null);
        }
    }

    /// <summary>Reads the session list, then the turn's header and recall if the page still shows the turn.</summary>
    private async Task<AgentOutcome?> AfterTurnAsync(string sessionId, long turn, CancellationToken ct)
    {
        var refreshed = await RefreshSessionsAsync(ct);
        if (ct.IsCancellationRequested || !IsCurrent(sessionId, turn))
        {
            return refreshed;
        }

        if (agent.Value.Sessions.FirstOrDefault(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal)) is { } header)
        {
            await dispatcher.DispatchAsync(new AgentSessionRefreshed(sessionId, turn, header));
        }

        await LoadRecalledAsync(sessionId, turn, ct);
        return refreshed;
    }

    /// <summary>
    ///     Reads the memories the turn's <c>memory-recalled</c> events named, in the session's agent context; one the
    ///     caller can no longer see is skipped.
    /// </summary>
    private async Task LoadRecalledAsync(string sessionId, long turn, CancellationToken ct)
    {
        var state = agent.Value;
        var recalled = new List<MemoryDto>();
        foreach (var id in state.RecalledIds.Distinct(StringComparer.Ordinal))
        {
            var memory = await api.GetMemoryAsync(id, state.Session?.AgentId, ct);
            if (memory.IsSuccess)
            {
                recalled.Add(memory.Value);
            }
        }

        if (!ct.IsCancellationRequested)
        {
            await dispatcher.DispatchAsync(new AgentRecalledLoaded(sessionId, turn, recalled));
        }
    }

    private bool IsCurrent(string sessionId, long turn) =>
        string.Equals(agent.Value.SessionId, sessionId, StringComparison.Ordinal) && agent.Value.Turn == turn;

    /// <summary>A failed request's outcome: none for a caller gone or a session being renewed, else its logged reason.</summary>
    private AgentOutcome? Failed(string error, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || SessionExpiry.IsSigningIn(error))
        {
            return null;
        }

        LogReadFailed(logger, error);
        return new AgentOutcome(error);
    }

    [LoggerMessage(EventId = 3900, Level = LogLevel.Warning, Message = "An Agent page request failed: {Error}")]
    private static partial void LogReadFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3901, Level = LogLevel.Warning, Message = "An agent turn lost its connection: {Error}")]
    private static partial void LogTurnTransportFailed(ILogger logger, string error);

    [LoggerMessage(EventId = 3902, Level = LogLevel.Warning, Message = "An agent turn failed unexpectedly: {ExceptionType}")]
    private static partial void LogTurnFailed(ILogger logger, string exceptionType);
}
