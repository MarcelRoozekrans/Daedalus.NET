namespace Daedalus.Domain.Entities;

/// <summary>
///     Records a single iteration of Ralph loop execution.
/// </summary>
public sealed class TaskExecution : Entity<Guid>
{
    /// <summary>Gets the task this execution belongs to.</summary>
    public Guid TaskId { get; init; }

    /// <summary>Gets the session that performed this execution.</summary>
    public Guid SessionId { get; init; }

    /// <summary>Gets which iteration this is (1-based).</summary>
    public int IterationNumber { get; init; }

    /// <summary>Gets the prompt sent to the LLM.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Gets the complete LLM response.</summary>
    public string LlmResponse { get; init; } = string.Empty;

    /// <summary>Gets whether the completion promise was found in the response.</summary>
    public bool CompletionPromiseFound { get; init; }

    /// <summary>Gets when this execution occurred.</summary>
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Gets how long the LLM invocation took.</summary>
    public TimeSpan ExecutionDuration { get; init; }

    /// <summary>Gets any error that occurred during execution.</summary>
    public string? Error { get; init; }
}
