using System.Diagnostics.CodeAnalysis;

using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     A structured learning entry that captures knowledge from task executions.
///     Unlike raw text learnings on the Task entity, these entries are categorized,
///     tagged, and searchable across tasks — enabling cross-task knowledge transfer.
///     Aligned with Ralph philosophy: simple persistence of what worked/failed,
///     with optional vector embeddings for semantic search via pgvector.
/// </summary>
public sealed class StructuredLearningEntry : Entity<Guid>
{
    private readonly List<string> _tags = [];

    /// <summary>Gets the category of this learning (error pattern, success pattern, etc.).</summary>
    public LearningCategory Category { get; private set; }

    /// <summary>Gets the pattern description — what was observed (e.g., "CS1061: type does not contain definition").</summary>
    public string Pattern { get; private set; } = string.Empty;

    /// <summary>Gets the resolution — what fixed it or what worked (e.g., "Add missing using directive for ZLinq").</summary>
    public string Resolution { get; private set; } = string.Empty;

    /// <summary>Gets the tags for text-based search and filtering (e.g., ["EF Core", "migration", "PostgreSQL"]).</summary>
    public IReadOnlyList<string> Tags => _tags.AsReadOnly();

    /// <summary>Gets the ID of the task that produced this learning. Null for manually added learnings.</summary>
    public Guid? SourceTaskId { get; private set; }

    /// <summary>Gets the project ID this learning belongs to, for scoping learnings to a project.</summary>
    public Guid? ProjectId { get; private set; }

    /// <summary>Gets the severity level — higher severity learnings are prioritized in prompts.</summary>
    public LearningSeverity Severity { get; private set; } = LearningSeverity.Medium;

    /// <summary>Gets the number of times this learning has been applied/referenced.</summary>
    public int HitCount { get; private set; }

    /// <summary>Gets when this learning was created.</summary>
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    /// <summary>Gets when this learning was last referenced in a prompt.</summary>
    public DateTime? LastReferencedAt { get; private set; }

    /// <summary>Gets the vector embedding for semantic search. Null if embedding not yet generated.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays",
        Justification = "Required for EF Core pgvector column mapping")]
    public float[]? Embedding { get; private set; }

    /// <summary>
    ///     Creates a new structured learning entry.
    /// </summary>
    public static Result<StructuredLearningEntry> Create(
        LearningCategory category,
        string pattern,
        string resolution,
        IEnumerable<string>? tags = null,
        Guid? sourceTaskId = null,
        Guid? projectId = null,
        LearningSeverity severity = LearningSeverity.Medium,
        DateTime? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return Result.Failure<StructuredLearningEntry>("Pattern cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(resolution))
        {
            return Result.Failure<StructuredLearningEntry>("Resolution cannot be empty");
        }

        var entry = new StructuredLearningEntry
        {
            Id = Guid.NewGuid(),
            Category = category,
            Pattern = pattern.Trim(),
            Resolution = resolution.Trim(),
            SourceTaskId = sourceTaskId,
            ProjectId = projectId,
            Severity = severity,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };

        if (tags != null)
        {
            foreach (var tag in tags.Where(t => !string.IsNullOrWhiteSpace(t)))
            {
                entry._tags.Add(tag.Trim().ToUpperInvariant());
            }
        }

        return Result.Success(entry);
    }

    /// <summary>
    ///     Records that this learning was referenced in a prompt enrichment.
    /// </summary>
    public void RecordReference()
    {
        HitCount++;
        LastReferencedAt = DateTime.UtcNow;
    }

    /// <summary>
    ///     Updates the resolution text (e.g., when a better fix is discovered).
    /// </summary>
    public Result UpdateResolution(string newResolution)
    {
        if (string.IsNullOrWhiteSpace(newResolution))
        {
            return Result.Failure("Resolution cannot be empty");
        }

        Resolution = newResolution.Trim();
        return Result.Success();
    }

    /// <summary>
    ///     Adds a tag to this learning entry.
    /// </summary>
    public Result AddTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return Result.Failure("Tag cannot be empty");
        }

        var normalizedTag = tag.Trim().ToUpperInvariant();
        if (_tags.Contains(normalizedTag, StringComparer.Ordinal))
        {
            return Result.Failure($"Tag '{normalizedTag}' already exists");
        }

        _tags.Add(normalizedTag);
        return Result.Success();
    }

    /// <summary>
    ///     Sets the embedding vector for semantic search.
    /// </summary>
    public void SetEmbedding(float[] embedding)
    {
        ArgumentNullException.ThrowIfNull(embedding);
        Embedding = embedding;
    }
}
