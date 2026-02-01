namespace Daedalus.Application.Abstractions;

/// <summary>
///     Represents the execution context and results from an agent run.
/// </summary>
public record AgentExecutionContext
{
    /// <summary>The agent that was executed.</summary>
    public AgentMetadata Agent { get; set; } = new();

    /// <summary>The prompt sent to the agent.</summary>
    public string InputPrompt { get; set; } = string.Empty;

    /// <summary>The agent's response/output.</summary>
    public string OutputResponse { get; set; } = string.Empty;

    /// <summary>Whether the agent successfully completed.</summary>
    public bool IsSuccessful { get; set; }

    /// <summary>Any error message from execution.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Time taken to execute.</summary>
    public TimeSpan ExecutionDuration { get; set; }

    /// <summary>When the execution occurred.</summary>
    public DateTime ExecutedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Additional metadata from the agent.</summary>
    public IDictionary<string, object> Metadata { get; } = new Dictionary<string, object>(StringComparer.Ordinal);
}
