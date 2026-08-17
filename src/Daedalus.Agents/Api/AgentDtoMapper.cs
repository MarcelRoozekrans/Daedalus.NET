using System.Text.Json;
using Daedalus.Application.DTOs.Agents;
using Microsoft.Extensions.AI;
using Thalos;
using Thalos.Memory;

namespace Daedalus.Agents.Api;

/// <summary>
///     Thalos records → Application DTOs. Hand-written on purpose: these are typed-id records mapped to string-id DTOs
///     (ZeroAlloc.Mapping is aimed at Command → Domain → DTO shapes), so an explicit mapper is smaller and clearer.
/// </summary>
public static class AgentDtoMapper
{
    // M.E.AI's defaults know how to write JsonElement/AIContent arguments; compact output for the wire.
    private static readonly JsonSerializerOptions CompactJson = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };

    /// <summary>Maps an agent definition to its list DTO.</summary>
    public static AgentSummaryDto ToDto(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new AgentSummaryDto(definition.Id.ToString(), definition.Name, definition.Description, definition.Tools);
    }

    /// <summary>Maps a session header.</summary>
    public static AgentSessionDto ToDto(AgentSessionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new AgentSessionDto(
            record.Id.ToString(),
            record.AgentId.ToString(),
            record.OwnerId,
            record.State.ToString(),
            record.CreatedAt,
            record.LastActivityAt,
            record.TurnCount,
            record.TotalInputTokens,
            record.TotalOutputTokens);
    }

    /// <summary>Maps turn usage. A <see langword="default"/> <see cref="TurnUsage"/> (failed turn before any model call) has a null model id → empty string.</summary>
    public static TurnUsageDto ToDto(TurnUsage usage) => new(usage.InputTokens, usage.OutputTokens, usage.ModelId ?? "");

    /// <summary>Maps a completed tool call.</summary>
    public static AgentToolCallDto ToDto(ToolCallSummary call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return new AgentToolCallDto(call.Id.ToString(), call.ToolName, call.ArgumentsJson, call.ResultPreview, call.Succeeded);
    }

    /// <summary>Maps the buffered result of a successful turn.</summary>
    public static AgentTurnResultDto ToDto(AgentTurnResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new AgentTurnResultDto(
            result.TurnId.ToString(),
            result.Text,
            ToDto(result.Usage),
            result.ToolCalls.Select(ToDto).ToList(),
            result.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    ///     Maps a streaming event to its SSE payload; <see cref="AgentEventDto.Kind"/> is the SSE event name. The five
    ///     <c>memory-*</c> events (Thalos.NET.Memory) carry <see cref="AgentEventDto.Memory"/>; an event type this adapter does
    ///     not know yet is passed through by kind only, so a newer Thalos never kills the SSE stream.
    /// </summary>
    public static AgentEventDto ToDto(AgentEvent agentEvent)
    {
        ArgumentNullException.ThrowIfNull(agentEvent);
        return agentEvent switch
        {
            TextDeltaEvent t => new AgentEventDto(t.Kind, Text: t.Text),
            ToolCallStartedEvent c => new AgentEventDto(c.Kind, ToolCall: new AgentToolCallDto(c.CallId.ToString(), c.ToolName, c.ArgumentsJson, null)),
            ToolCallFinishedEvent f => new AgentEventDto(f.Kind, ToolCall: new AgentToolCallDto(f.CallId.ToString(), f.ToolName, null, f.ResultPreview, f.Succeeded)),
            UsageEvent u => new AgentEventDto(u.Kind, Usage: ToDto(u.Usage)),
            TurnCompletedEvent d => new AgentEventDto(d.Kind, Result: ToDto(d.Result)),
            TurnFailedEvent x => new AgentEventDto(x.Kind, Usage: ToDto(x.Usage), ErrorCode: x.Error.Code.ToString(), ErrorMessage: x.Error.Message, ErrorDetail: x.Error.Detail),
            MemoryRecalledEvent r => new AgentEventDto(r.Kind, Memory: new MemoryEventDto(Count: r.Count, Ids: r.MemoryIds.Select(i => i.ToString()).ToList(), Chars: r.Chars)),
            MemoryStoredEvent s => new AgentEventDto(s.Kind, Memory: new MemoryEventDto(MemoryId: s.MemoryId.ToString(), Kind: s.MemoryKind, Deduped: s.Deduped)),
            MemoryRecallFailedEvent f => new AgentEventDto(f.Kind, Memory: new MemoryEventDto(Code: f.Code.ToString())),
            MemoryIndexPendingEvent p => new AgentEventDto(p.Kind, Memory: new MemoryEventDto(MemoryId: p.MemoryId.ToString())),
            MemoryQuarantinedEvent q => new AgentEventDto(q.Kind, Memory: new MemoryEventDto(MemoryId: q.MemoryId.ToString(), Detail: q.Detail)),
            // Forward-compatible: a Thalos event kind this adapter does not know yet still reaches the client by name.
            _ => new AgentEventDto(agentEvent.Kind),
        };
    }

    /// <summary>
    ///     Maps a stored memory. <paramref name="sharedOwnerId"/> is the owner of host-written project knowledge (Ralph
    ///     learnings): a record owned by it is flagged <c>IsShared</c> so the UI can tell "mine" from "the project's" apart.
    /// </summary>
    public static MemoryDto ToDto(MemoryRecord record, string? sharedOwnerId)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new MemoryDto(
            record.Id.ToString(),
            record.OwnerId,
            record.AgentId?.ToString(),
            record.Kind.Value,
            record.Text,
            record.Tags,
            record.Source,
            record.Importance,
            record.CreatedAt,
            record.UpdatedAt,
            record.LastRecalledAt,
            record.RecallCount,
            record.IsArchived,
            record.IndexPending,
            sharedOwnerId is not null && string.Equals(record.OwnerId, sharedOwnerId, StringComparison.Ordinal));
    }

    /// <summary>
    ///     Collapses stored <see cref="ChatMessage"/>s into display messages: user/assistant text plus the assistant's tool
    ///     calls with their results. Tool-role messages are folded into the assistant message that made the call; messages
    ///     with neither text nor tool calls are dropped.
    /// </summary>
    public static IReadOnlyList<AgentMessageDto> ToDtos(IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var results = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var result in messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()))
        {
            results.TryAdd(result.CallId, result.Result?.ToString());
        }

        var list = new List<AgentMessageDto>(messages.Count);
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.Tool)
            {
                continue;
            }

            var calls = message.Contents.OfType<FunctionCallContent>()
                .Select(c => new AgentToolCallDto(c.CallId, c.Name, SerializeArguments(c.Arguments), results.GetValueOrDefault(c.CallId)))
                .ToList();

            if (string.IsNullOrEmpty(message.Text) && calls.Count == 0)
            {
                continue;
            }

            list.Add(new AgentMessageDto(message.Role.Value, message.Text, calls, message.CreatedAt));
        }

        return list;
    }

    private static string? SerializeArguments(IDictionary<string, object?>? arguments) =>
        arguments is null ? null : JsonSerializer.Serialize(arguments, CompactJson);
}
