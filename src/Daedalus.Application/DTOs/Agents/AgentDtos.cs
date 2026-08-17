namespace Daedalus.Application.DTOs.Agents;

// Plain records shared by the API and the WASM client. Application stays Thalos-free: ids are strings, enums are their names.

/// <summary>A registered agent definition as listed by the API.</summary>
public sealed record AgentSummaryDto(string Id, string Name, string Description, IReadOnlyList<string> Tools);

/// <summary>Session header. <paramref name="State"/> is the <c>SessionState</c> name (Idle, Running, Closed).</summary>
public sealed record AgentSessionDto(
    string Id,
    string AgentId,
    string OwnerId,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int TurnCount,
    long TotalInputTokens,
    long TotalOutputTokens);

/// <summary>A display message: user/assistant text plus the tool calls the assistant made (tool results folded in).</summary>
public sealed record AgentMessageDto(string Role, string Text, IReadOnlyList<AgentToolCallDto> ToolCalls, DateTimeOffset? CreatedAt);

/// <summary>
///     One tool invocation; <paramref name="ResultPreview"/> is null while the call is still running.
///     <paramref name="Succeeded"/> is null while the call is running or when the outcome is unknown (transcript history).
/// </summary>
public sealed record AgentToolCallDto(string CallId, string ToolName, string? ArgumentsJson, string? ResultPreview, bool? Succeeded = null);

/// <summary>Session header plus its transcript.</summary>
public sealed record AgentSessionDetailDto(AgentSessionDto Session, IReadOnlyList<AgentMessageDto> Messages);

/// <summary>Response of <c>POST /api/agents/{agentId}/sessions</c>.</summary>
public sealed record CreateAgentSessionResponseDto(string SessionId);

/// <summary>Body of <c>POST /api/agentsessions/{id}/turns</c>.</summary>
public sealed record SendTurnRequestDto(string Text);

/// <summary>Token usage of one turn (summed over all model round-trips).</summary>
public sealed record TurnUsageDto(int InputTokens, int OutputTokens, string ModelId);

/// <summary>Buffered outcome of a successful turn.</summary>
public sealed record AgentTurnResultDto(string TurnId, string Text, TurnUsageDto Usage, IReadOnlyList<AgentToolCallDto> ToolCalls, double ElapsedMs);

/// <summary>
///     Memory payload of the <c>memory-*</c> SSE kinds; only the members relevant to the kind are set:
///     <c>memory-recalled</c> → <paramref name="Count"/>, <paramref name="Ids"/>, <paramref name="Chars"/>;
///     <c>memory-stored</c> → <paramref name="MemoryId"/>, <paramref name="Kind"/>, <paramref name="Deduped"/>;
///     <c>memory-recall-failed</c> → <paramref name="Code"/>; <c>memory-index-pending</c> → <paramref name="MemoryId"/>;
///     <c>memory-quarantined</c> → <paramref name="MemoryId"/>, <paramref name="Detail"/>.
/// </summary>
public sealed record MemoryEventDto(
    int? Count = null,
    IReadOnlyList<string>? Ids = null,
    int? Chars = null,
    string? MemoryId = null,
    string? Kind = null,
    bool? Deduped = null,
    string? Code = null,
    string? Detail = null);

/// <summary>
///     SSE payload. <paramref name="Kind"/> mirrors the SSE event name: <c>text-delta</c> | <c>tool-call</c> | <c>tool-result</c> |
///     <c>usage</c> | <c>done</c> | <c>error</c> | <c>memory-recalled</c> | <c>memory-stored</c> | <c>memory-recall-failed</c> |
///     <c>memory-index-pending</c> | <c>memory-quarantined</c>. Only the members relevant to the kind are set (<c>error</c> also
///     carries the usage accumulated before the failure; the <c>memory-*</c> kinds carry <paramref name="Memory"/>). Unknown
///     kinds arrive with only <paramref name="Kind"/> set — clients ignore what they do not know.
/// </summary>
public sealed record AgentEventDto(
    string Kind,
    string? Text = null,
    AgentToolCallDto? ToolCall = null,
    TurnUsageDto? Usage = null,
    AgentTurnResultDto? Result = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ErrorDetail = null,
    MemoryEventDto? Memory = null);
