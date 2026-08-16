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

/// <summary>One tool invocation; <paramref name="ResultPreview"/> is null while the call is still running.</summary>
public sealed record AgentToolCallDto(string CallId, string ToolName, string? ArgumentsJson, string? ResultPreview);

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
///     SSE payload. <paramref name="Kind"/> mirrors the SSE event name: <c>text-delta</c> | <c>tool-call</c> | <c>tool-result</c> |
///     <c>usage</c> | <c>done</c> | <c>error</c>. Only the members relevant to the kind are set (<c>error</c> also carries the
///     usage accumulated before the failure).
/// </summary>
public sealed record AgentEventDto(
    string Kind,
    string? Text = null,
    AgentToolCallDto? ToolCall = null,
    TurnUsageDto? Usage = null,
    AgentTurnResultDto? Result = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    string? ErrorDetail = null);
