namespace Daedalus.Application.Abstractions;

/// <summary>
///     Represents the context and history of a Ralph loop prompt execution.
///     Persisted to track the conversation state across iterations.
/// </summary>
#pragma warning disable CA1002, MA0016 // List used for mutability in context tracking
public record PromptContext
{
    /// <summary>The unique identifier for this prompt context.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The task being executed.</summary>
    public Guid TaskId { get; set; }

    /// <summary>The execution session.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Current iteration number (1-based).</summary>
    public int Iteration { get; set; }

    /// <summary>The original prompt template from the task.</summary>
    public string OriginalPrompt { get; set; } = string.Empty;

    /// <summary>The completion promise to search for.</summary>
    public string CompletionPromise { get; set; } = string.Empty;

    /// <summary>Execution history - previous LLM responses for context building.</summary>
    public List<PromptHistoryEntry> History { get; } = [];

    /// <summary>Metadata about the current execution context.</summary>
    public Dictionary<string, string> Metadata { get; } = new(StringComparer.Ordinal);

    /// <summary>When this context was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this context was last updated.</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Accumulated learnings from previous executions.</summary>
    public string? AccumulatedLearnings { get; set; }

    /// <summary>Loaded workspace context (specs, plan, AGENT.md) for prompt grounding.</summary>
    public WorkspaceContext? WorkspaceContext { get; set; }

    /// <summary>Template options for structured prompt assembly.</summary>
    public RalphPromptTemplateOptions? TemplateOptions { get; set; }
}
