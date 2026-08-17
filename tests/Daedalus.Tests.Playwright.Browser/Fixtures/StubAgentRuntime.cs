using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Thalos;
using Thalos.Memory;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Playwright.Browser.Fixtures;

/// <summary>
///     Scripted <see cref="IAgentRuntime"/> for browser tests: no model, no MCP servers. Sessions live in the real
///     <see cref="IAgentSessionStore"/> (so the session list and transcript reload work like production); every turn streams
///     the same script — text deltas spelling <see cref="ReplyText"/>, one <c>roslyn__find_callers</c> tool call/result
///     pair, usage and done — and persists the exchange through the store.
/// </summary>
internal sealed class StubAgentRuntime(IAgentSessionStore store, IAgentCatalog catalog, IMemoryStore memories) : IAgentRuntime
{
    /// <summary>Owner of host-written project knowledge; matches <c>Thalos:Memory:SharedOwnerId</c> in the API settings.</summary>
    public const string SharedOwnerId = "daedalus";

    public const string ReplyText = "Hello from Thalos";
    public const string ToolName = "roslyn__find_callers";
    public const string ToolArgumentsJson = "{\"symbol\":\"AgentSessionsController\"}";
    public const string ToolResultPreview = "2 callers: AgentPage.SendAsync, AgentApiClient.StreamTurnAsync";
    public const string ModelId = "stub-model";

    private static readonly string[] Deltas = ["Hello ", "from ", "Thalos"];

    public async ValueTask<Result<SessionId, AgentError>> CreateSessionAsync(AgentId agentId, ISecurityContext caller, CancellationToken ct = default)
    {
        if (!catalog.TryGet(agentId, out _))
        {
            return Result<SessionId, AgentError>.Failure(AgentError.AgentNotFound(agentId));
        }

        var created = await store.CreateAsync(agentId, caller.Id, ct).ConfigureAwait(false);
        return created.IsFailure
            ? Result<SessionId, AgentError>.Failure(created.Error)
            : Result<SessionId, AgentError>.Success(created.Value.Id);
    }

    public async ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
    {
        await foreach (var agentEvent in RunTurnStreamingAsync(request, ct).ConfigureAwait(false))
        {
            switch (agentEvent)
            {
                case TurnCompletedEvent completed:
                    return Result<AgentTurnResult, AgentError>.Success(completed.Result);
                case TurnFailedEvent failed:
                    return Result<AgentTurnResult, AgentError>.Failure(failed.Error);
                default:
                    break;
            }
        }

        return Result<AgentTurnResult, AgentError>.Failure(AgentError.ProviderError("The stub stream ended without a terminal event."));
    }

    public async IAsyncEnumerable<AgentEvent> RunTurnStreamingAsync(AgentTurnRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var sessionId = request.SessionId;
        var turnId = TurnId.New();
        var stopwatch = Stopwatch.StartNew();

        var session = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session.IsFailure || !string.Equals(session.Value.OwnerId, request.Caller.Id, StringComparison.Ordinal))
        {
            // Foreign or unknown session: 404 semantics, never 403 (mirrors ThalosAgentRuntime).
            yield return new TurnFailedEvent(sessionId, turnId, AgentError.SessionNotFound(sessionId));
            yield break;
        }

        if (session.Value.State == SessionState.Closed)
        {
            yield return new TurnFailedEvent(sessionId, turnId, AgentError.SessionClosed(sessionId));
            yield break;
        }

        var claimed = await store.TryTransitionAsync(sessionId, SessionState.Idle, SessionState.Running, ct).ConfigureAwait(false);
        if (claimed.IsFailure || !claimed.Value)
        {
            yield return new TurnFailedEvent(sessionId, turnId, AgentError.SessionBusy(sessionId));
            yield break;
        }

        try
        {
            var callId = ToolCallId.New();

            // Stands in for Thalos' MemoryContextProvider: this host has no embeddings, so "recall" is the caller's own
            // memories plus the shared owner's, straight off the store.
            var recall = await memories.ListAsync(
                new MemoryQuery { OwnerIds = [request.Caller.Id, SharedOwnerId], PageSize = 5 },
                ct).ConfigureAwait(false);
            if (recall.IsSuccess && recall.Value.Items.Count > 0)
            {
                var ids = recall.Value.Items.Select(m => m.Id).ToList();
                yield return new MemoryRecalledEvent(sessionId, turnId, ids, recall.Value.Items.Sum(m => m.Text.Length));
            }

            yield return new TextDeltaEvent(sessionId, turnId, Deltas[0]);
            yield return new ToolCallStartedEvent(sessionId, turnId, callId, ToolName, ToolArgumentsJson);
            await Task.Delay(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false); // let the "running" state render once
            yield return new ToolCallFinishedEvent(sessionId, turnId, callId, ToolName, Succeeded: true, ToolResultPreview, TimeSpan.FromMilliseconds(5));
            yield return new TextDeltaEvent(sessionId, turnId, Deltas[1]);
            yield return new TextDeltaEvent(sessionId, turnId, Deltas[2]);

            var usage = new TurnUsage(InputTokens: 12, OutputTokens: 7, ModelId);
            await store.AppendMessagesAsync(
                sessionId,
                [new ChatMessage(ChatRole.User, request.Text), new ChatMessage(ChatRole.Assistant, ReplyText)],
                ct).ConfigureAwait(false);
            await store.RecordTurnAsync(sessionId, usage, ct).ConfigureAwait(false);

            yield return new UsageEvent(sessionId, turnId, usage);
            yield return new TurnCompletedEvent(
                sessionId,
                turnId,
                new AgentTurnResult(
                    turnId,
                    sessionId,
                    ReplyText,
                    usage,
                    [new ToolCallSummary(callId, ToolName, ToolArgumentsJson, Succeeded: true, ToolResultPreview, TimeSpan.FromMilliseconds(5))],
                    stopwatch.Elapsed));
        }
        finally
        {
            await store.UpdateStateAsync(sessionId, SessionState.Idle, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async ValueTask<UnitResult<AgentError>> CloseSessionAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct = default)
    {
        var session = await store.GetAsync(sessionId, ct).ConfigureAwait(false);
        if (session.IsFailure || !string.Equals(session.Value.OwnerId, caller.Id, StringComparison.Ordinal))
        {
            return UnitResult<AgentError>.Failure(AgentError.SessionNotFound(sessionId));
        }

        return session.Value.State switch
        {
            SessionState.Closed => UnitResult<AgentError>.Failure(AgentError.SessionClosed(sessionId)),
            SessionState.Running => UnitResult<AgentError>.Failure(AgentError.SessionBusy(sessionId)),
            _ => await store.UpdateStateAsync(sessionId, SessionState.Closed, ct).ConfigureAwait(false),
        };
    }
}
