namespace Daedalus.Application.Abstractions;

/// <summary>
///     A record of an error pattern and its resolution from past task executions.
///     Simple data structure — no AI, no embeddings, just facts.
/// </summary>
public record FailurePatternRecord
{
    /// <summary>The error text from the failed iteration.</summary>
    public required string ErrorText { get; init; }

    /// <summary>The resolution — what the LLM did in the iteration that succeeded.</summary>
    public required string Resolution { get; init; }

    /// <summary>The task ID where this pattern was observed.</summary>
    public required Guid SourceTaskId { get; init; }

    /// <summary>The iteration number where the error occurred.</summary>
    public required int ErrorIteration { get; init; }

    /// <summary>The iteration number where the fix was applied (null if not yet resolved).</summary>
    public int? ResolutionIteration { get; init; }

    /// <summary>When this pattern was recorded.</summary>
    public required DateTime ObservedAt { get; init; }
}
