using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Simple text-search database of error→fix pairs extracted from past task executions.
///     Queries TaskExecution records for error patterns and their subsequent resolutions.
///     No vectors, no embeddings — just SQL text search aligned with Ralph philosophy.
/// </summary>
public interface IFailurePatternDatabase
{
    /// <summary>
    ///     Searches for known error patterns matching the given error text.
    ///     Returns error→fix pairs from past task executions where the same error
    ///     was encountered and subsequently resolved.
    /// </summary>
    /// <param name="errorText">The error text to search for (partial match).</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of matching failure pattern records.</returns>
    Task<Result<IReadOnlyList<FailurePatternRecord>>> SearchByErrorAsync(
        string errorText,
        int maxResults,
        CancellationToken ct);

    /// <summary>
    ///     Gets failure patterns relevant to a specific task's prompt content.
    ///     Extracts keywords from the prompt and searches for matching patterns.
    /// </summary>
    /// <param name="promptContent">The task prompt to extract keywords from.</param>
    /// <param name="maxResults">Maximum number of results.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of relevant failure patterns.</returns>
    Task<Result<IReadOnlyList<FailurePatternRecord>>> SearchByPromptContextAsync(
        string promptContent,
        int maxResults,
        CancellationToken ct);
}
