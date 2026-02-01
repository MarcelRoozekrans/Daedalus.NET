namespace Daedalus.Application.Abstractions;

/// <summary>
///     Structured learnings extracted from a single LLM iteration response.
///     Designed for zero-allocation-friendly inline use — no files, no external calls.
/// </summary>
public sealed record ParsedIterationLearnings
{
    /// <summary>Error patterns detected in the response (compiler errors, runtime exceptions, etc.).</summary>
    public required IReadOnlyList<string> ErrorPatterns { get; init; }

    /// <summary>Approach signals — what the LLM tried or plans to try.</summary>
    public required IReadOnlyList<string> ApproachSignals { get; init; }

    /// <summary>Files or areas the LLM mentioned modifying.</summary>
    public required IReadOnlyList<string> ModifiedAreas { get; init; }

    /// <summary>Whether the response indicates the LLM is stuck in a loop (repeating itself).</summary>
    public bool StuckDetected { get; init; }

    /// <summary>How close the response is to containing the completion promise (0.0–1.0 heuristic).</summary>
    public double ProgressSignal { get; init; }

    /// <summary>Compact text summary suitable for injection into the next iteration's prompt.</summary>
    public required string CompactSummary { get; init; }

    /// <summary>Whether any meaningful learnings were extracted.</summary>
    public bool HasLearnings => ErrorPatterns.Count > 0 || ApproachSignals.Count > 0 || ModifiedAreas.Count > 0;
}
