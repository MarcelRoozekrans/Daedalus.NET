using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Application service for managing structured learnings across tasks.
///     Parses raw learnings text into categorized entries and provides
///     enrichment data for prompt building. Aligned with Ralph philosophy:
///     simple text parsing, no ML models — persists to the agent memory
///     (<see cref="ILearningsMemory"/>), which handles indexing and recall.
/// </summary>
public interface ILearningsService
{
    /// <summary>
    ///     Parses raw learnings text (from Task.Learnings) into structured entries
    ///     and persists them for cross-task retrieval.
    /// </summary>
    /// <param name="rawLearnings">The raw learnings text accumulated from task executions.</param>
    /// <param name="sourceTaskId">The task that produced these learnings.</param>
    /// <param name="projectId">The project scope for these learnings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of structured entries created.</returns>
    Task<Result<int>> ParseAndPersistLearningsAsync(
        string rawLearnings,
        Guid sourceTaskId,
        Guid? projectId,
        CancellationToken ct);

    /// <summary>
    ///     Gets enrichment context for a task iteration — structured learnings + failure patterns
    ///     relevant to the current task, formatted for prompt injection.
    /// </summary>
    /// <param name="taskPrompt">The current task prompt for keyword extraction.</param>
    /// <param name="projectId">Project scope for relevant learnings.</param>
    /// <param name="currentTaskId">Current task ID (to avoid self-referencing).</param>
    /// <param name="maxLearnings">Maximum learnings entries to include.</param>
    /// <param name="maxFailurePatterns">Maximum failure patterns to include.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Formatted enrichment text for prompt injection.</returns>
    Task<Result<string>> GetEnrichmentContextAsync(
        string taskPrompt,
        Guid? projectId,
        Guid currentTaskId,
        int maxLearnings,
        int maxFailurePatterns,
        CancellationToken ct);
}
