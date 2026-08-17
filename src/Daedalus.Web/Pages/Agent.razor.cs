using System.Text;
using Daedalus.Application.DTOs.Agents;
using Microsoft.JSInterop;

namespace Daedalus.Web.Pages;

/// <summary>Code-behind for <c>Agent.razor</c>: the transcript view models and the JS-interop teardown.</summary>
public partial class Agent
{
    /// <summary>Releases the Escape listener and the module; JS errors during teardown (e.g. circuit gone) are swallowed.</summary>
    private async ValueTask DisposeInteropAsync()
    {
        try
        {
            if (_escapeHandle is not null)
            {
                await _escapeHandle.InvokeVoidAsync("dispose");
                await _escapeHandle.DisposeAsync();
            }

            if (_module is not null)
            {
                await _module.DisposeAsync();
            }
        }
        catch (JSException)
        {
            // Nothing left to clean up on the JS side.
        }
        catch (JSDisconnectedException)
        {
            // Browser already navigated away.
        }
        finally
        {
            _selfRef?.Dispose();
        }
    }

    /// <summary>One chat bubble; assistant bubbles grow while streaming.</summary>
    private sealed class ChatMessage
    {
        private readonly StringBuilder _text = new();

        private ChatMessage(bool isUser) => IsUser = isUser;

        public bool IsUser { get; }
        public string Text => _text.ToString();
        public bool IsTextEmpty => _text.Length == 0;
        public bool IsEmpty => IsTextEmpty && ToolCalls.Count == 0;
        public List<ToolCallView> ToolCalls { get; } = [];
        public TurnUsageDto? Usage { get; set; }

        public static ChatMessage User(string text)
        {
            var message = new ChatMessage(isUser: true);
            message.Append(text);
            return message;
        }

        public static ChatMessage Assistant() => new(isUser: false);

        public static ChatMessage FromDto(AgentMessageDto dto)
        {
            var message = new ChatMessage(string.Equals(dto.Role, "user", StringComparison.OrdinalIgnoreCase));
            message.Append(dto.Text);
            message.ToolCalls.AddRange(dto.ToolCalls.Select(ToolCallView.FromHistory));
            return message;
        }

        public void Append(string? text) => _text.Append(text);
    }

    /// <summary>A tool call under an assistant bubble; <see cref="Succeeded"/> is null while running (or unknown for history).</summary>
    private sealed class ToolCallView(string callId, string toolName, string? argumentsJson)
    {
        public string CallId { get; } = callId;
        public string ToolName { get; } = toolName;
        public string? ArgumentsJson { get; } = argumentsJson;
        public string? ResultPreview { get; private set; }
        public bool? Succeeded { get; private set; }
        public bool Finished { get; private set; }

        public string Status => !Finished ? "running" : Succeeded switch { true => "succeeded", false => "failed", null => "done" };
        public string StatusGlyph => !Finished ? "…" : Succeeded switch { true => "✓", false => "✗", null => "•" };

        public static ToolCallView Started(AgentToolCallDto dto) => new(dto.CallId, dto.ToolName, dto.ArgumentsJson);

        public static ToolCallView FromHistory(AgentToolCallDto dto)
        {
            var view = new ToolCallView(dto.CallId, dto.ToolName, dto.ArgumentsJson);
            view.Finish(dto);
            return view;
        }

        public void Finish(AgentToolCallDto dto)
        {
            ResultPreview = dto.ResultPreview;
            Succeeded = dto.Succeeded;
            Finished = true;
        }
    }

    /// <summary>The red alert under the transcript. Quarantined turns get the shield icon plus the sentinel detail.</summary>
    private sealed record ErrorView(string Code, string Message, string? Detail)
    {
        public bool IsQuarantined => string.Equals(Code, "Quarantined", StringComparison.Ordinal);
        public string Title => IsQuarantined ? "Quarantined by AI Sentinel" : Code;
    }
}
