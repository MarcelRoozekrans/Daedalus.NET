namespace Daedalus.Application.Abstractions;

/// <summary>
///     Represents a single entry in the execution history.
///     Stores compact summaries for context window preservation.
///     Full prompt/response data is persisted in TaskExecutions table.
/// </summary>
public record PromptHistoryEntry
{
    /// <summary>The iteration number when this was executed.</summary>
    public int Iteration { get; set; }

    /// <summary>The prompt that was sent to the LLM (stored but not included in subsequent prompts).</summary>
    public string Prompt { get; set; } = string.Empty;

    /// <summary>The LLM's response (stored but only snippets used in subsequent prompts).</summary>
    public string Response { get; set; } = string.Empty;

    /// <summary>
    ///     Compact summary of this iteration's outcome for context window preservation.
    ///     This is what gets injected into subsequent prompts instead of the full response.
    /// </summary>
    public string CompactSummary { get; set; } = string.Empty;

    /// <summary>Whether the completion promise was found.</summary>
    public bool CompletionPromiseFound { get; set; }

    /// <summary>Execution duration.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Any error that occurred during execution.</summary>
    public string? ExecutionError { get; set; }

    /// <summary>When this entry was created.</summary>
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    ///     Generates a compact summary from the response for context window efficiency.
    ///     The article's key insight: "use as little of [the context window] as possible".
    /// </summary>
    public static string BuildCompactSummary(string response, string? error, bool completionFound, TimeSpan duration)
    {
        if (!string.IsNullOrEmpty(error))
        {
            var errorSnippet = error.Length > 300 ? error[..300] + "..." : error;
            return $"ERROR ({duration.TotalSeconds:F1}s): {errorSnippet}";
        }

        if (completionFound)
        {
            return
                $"SUCCESS ({duration.TotalSeconds:F1}s): Completion promise found. Response: {response.Length} chars";
        }

        // For incomplete attempts, include a meaningful snippet
        var snippet = response.Length > 500 ? response[..500] + "..." : response;
        return $"INCOMPLETE ({duration.TotalSeconds:F1}s, {response.Length} chars): {snippet}";
    }
}
