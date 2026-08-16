namespace Daedalus.Application.Abstractions;

/// <summary>
///     Represents a chain/sequence of agent executions.
/// </summary>
public record AgentExecutionChain
{
    /// <summary>Unique identifier for this execution chain.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The original prompt that started the chain.</summary>
    public string OriginalPrompt { get; set; } = string.Empty;

    /// <summary>All agent executions in order.</summary>
    public ICollection<AgentExecutionContext> ExecutionHistory { get; } = new List<AgentExecutionContext>();

    /// <summary>Final output after all agents have executed.</summary>
    public string FinalOutput { get; set; } = string.Empty;

    /// <summary>Whether the chain completed successfully.</summary>
    public bool IsCompleted { get; set; }

    /// <summary>Overall execution time for the chain.</summary>
    public TimeSpan TotalDuration { get; set; }

    /// <summary>When the chain was initiated.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
