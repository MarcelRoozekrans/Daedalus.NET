using System.Collections.Immutable;
using Daedalus.Application.DTOs.Agents;

namespace Daedalus.Web.State.Agent;

/// <summary>
///     One chat bubble. Immutable: a streamed reply grows by a new record per event, never by changing this one, so a
///     render that holds an older record keeps seeing what it rendered.
/// </summary>
/// <param name="IsUser">Whether the person wrote it; otherwise the assistant did.</param>
/// <param name="Content">The bubble's text as its chunks, so far for a reply still streaming.</param>
/// <param name="ToolCalls">The tool calls under an assistant bubble, in the order they started.</param>
/// <param name="Usage">The model and token counts, once the turn reports them.</param>
public sealed record AgentChatMessage(bool IsUser, AgentText Content, ImmutableList<AgentToolCall> ToolCalls, TurnUsageDto? Usage)
{
    /// <summary>The bubble's text, built from its chunks when the page renders it.</summary>
    public string Text => Content.ToString();

    /// <summary>Whether the bubble has no text yet.</summary>
    public bool IsTextEmpty => Content.Length == 0;

    /// <summary>Whether the bubble has neither text nor a tool call: a reply that produced nothing is not kept.</summary>
    public bool IsEmpty => IsTextEmpty && ToolCalls.IsEmpty;

    /// <summary>The person's message <paramref name="text"/>.</summary>
    public static AgentChatMessage User(string text) => new(true, AgentText.From(text), [], null);

    /// <summary>An assistant reply before its first event.</summary>
    public static AgentChatMessage Assistant() => new(false, AgentText.Empty, [], null);

    /// <summary>A message of the stored transcript.</summary>
    public static AgentChatMessage FromDto(AgentMessageDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new AgentChatMessage(
            string.Equals(dto.Role, "user", StringComparison.OrdinalIgnoreCase),
            AgentText.From(dto.Text),
            [.. dto.ToolCalls.Select(AgentToolCall.FromHistory)],
            null);
    }
}

/// <summary>
///     A tool call under an assistant bubble. <paramref name="Succeeded"/> is <see langword="null"/> while it runs, and
///     for a stored call whose outcome was not recorded.
/// </summary>
public sealed record AgentToolCall(
    string CallId, string ToolName, string? ArgumentsJson, string? ResultPreview, bool? Succeeded, bool Finished)
{
    /// <summary>The <c>data-tool-status</c> value: running, succeeded, failed or done.</summary>
    public string Status => !Finished ? "running" : Succeeded switch { true => "succeeded", false => "failed", null => "done" };

    /// <summary>The glyph after the tool's name.</summary>
    public string StatusGlyph => !Finished ? "…" : Succeeded switch { true => "✓", false => "✗", null => "•" };

    /// <summary>A call the stream says has started.</summary>
    public static AgentToolCall Started(AgentToolCallDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new AgentToolCall(dto.CallId, dto.ToolName, dto.ArgumentsJson, null, null, false);
    }

    /// <summary>A call of the stored transcript, which has finished.</summary>
    public static AgentToolCall FromHistory(AgentToolCallDto dto) => Started(dto).Finish(dto);

    /// <summary>This call, finished with <paramref name="dto"/>'s result.</summary>
    public AgentToolCall Finish(AgentToolCallDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return this with { ResultPreview = dto.ResultPreview, Succeeded = dto.Succeeded, Finished = true };
    }
}

/// <summary>The red alert under the transcript. A quarantined turn gets the shield icon and the sentinel's detail.</summary>
public sealed record AgentError(string Code, string Message, string? Detail)
{
    /// <summary>The alert for a turn the person stopped.</summary>
    public static AgentError Cancelled { get; } = new("Cancelled", "The turn was stopped.", null);

    /// <summary>Whether AI Sentinel quarantined the turn.</summary>
    public bool IsQuarantined => string.Equals(Code, "Quarantined", StringComparison.Ordinal);

    /// <summary>The alert's title.</summary>
    public string Title => IsQuarantined ? "Quarantined by AI Sentinel" : Code;
}
