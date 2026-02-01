using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Repository for cross-task structured learnings persistence and retrieval.
///     Uses simple text search (SQL LIKE / PostgreSQL full-text search) — no vectors.
///     Aligned with Ralph philosophy: simple persistence beats complex retrieval.
/// </summary>
public interface ILearningsRepository
{
    /// <summary>
    ///     Adds a new structured learning entry.
    /// </summary>
    Task<Result<StructuredLearningEntry>> AddAsync(StructuredLearningEntry entry, CancellationToken ct);

    /// <summary>
    ///     Gets all learnings for a specific category, ordered by severity (descending) then recency.
    /// </summary>
    Task<Result<IReadOnlyList<StructuredLearningEntry>>> GetByCategoryAsync(
        LearningCategory category,
        int maxResults,
        CancellationToken ct);

    /// <summary>
    ///     Searches learnings by keyword across pattern, resolution, and tags.
    ///     Uses simple text matching (case-insensitive) — no vector search.
    /// </summary>
    Task<Result<IReadOnlyList<StructuredLearningEntry>>> SearchByKeywordAsync(
        string keyword,
        int maxResults,
        CancellationToken ct);

    /// <summary>
    ///     Gets learnings relevant to a specific project, ordered by severity and hit count.
    /// </summary>
    Task<Result<IReadOnlyList<StructuredLearningEntry>>> GetByProjectIdAsync(
        Guid projectId,
        int maxResults,
        CancellationToken ct);

    /// <summary>
    ///     Gets the most impactful learnings (highest severity + hit count) across all projects.
    /// </summary>
    Task<Result<IReadOnlyList<StructuredLearningEntry>>> GetTopLearningsAsync(
        int maxResults,
        CancellationToken ct);

    /// <summary>
    ///     Updates an existing learning entry (e.g., increments hit count).
    /// </summary>
    Task<Result> UpdateAsync(StructuredLearningEntry entry, CancellationToken ct);

    /// <summary>
    ///     Searches for learnings matching specific tags.
    /// </summary>
    Task<Result<IReadOnlyList<StructuredLearningEntry>>> SearchByTagsAsync(
        IEnumerable<string> tags,
        int maxResults,
        CancellationToken ct);
}
