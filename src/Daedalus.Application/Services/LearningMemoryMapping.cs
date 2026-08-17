using Daedalus.Domain.Entities;

namespace Daedalus.Application.Services;

/// <summary>
///     How a Ralph learning becomes a memory record. Mirrors the SQL in the <c>AddAgentMemories</c> migration (text, tags,
///     importance, source) — change both together.
/// </summary>
public static class LearningMemoryMapping
{
    /// <summary>Free tags kept per learning; plus the category and severity tags that makes the 10-tag limit.</summary>
    public const int MaxFreeTags = 8;

    /// <summary>The memory text: the pattern, and the resolution on a second line when it adds anything.</summary>
    /// <param name="pattern">The learning's pattern half.</param>
    /// <param name="resolution">The learning's resolution half.</param>
    public static string Text(string pattern, string resolution) =>
        string.Equals(pattern, resolution, StringComparison.Ordinal) ? pattern : $"{pattern}\n{resolution}";

    /// <summary>Category and severity first (they are queryable facets), then the lower-cased free tags.</summary>
    /// <param name="category">The learning category.</param>
    /// <param name="severity">The learning severity.</param>
    /// <param name="tags">Free tags extracted from the raw line; blanks and duplicates are dropped.</param>
    public static IReadOnlyList<string> Tags(LearningCategory category, LearningSeverity severity, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var list = new List<string>
        {
#pragma warning disable CA1308 // Memory tags are lower-case by convention (Thalos normalises the same way), not for security decisions.
            category.ToString().ToLowerInvariant(),
            severity.ToString().ToLowerInvariant(),
        };

        list.AddRange(tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
#pragma warning restore CA1308
            .Distinct(StringComparer.Ordinal)
            .Take(MaxFreeTags));

        return list;
    }

    /// <summary>Memory importance in [0,1] derived from the learning severity.</summary>
    /// <param name="severity">The learning severity.</param>
    public static double Importance(LearningSeverity severity) => severity switch
    {
        LearningSeverity.Critical => 1.0,
        LearningSeverity.High => 0.8,
        LearningSeverity.Medium => 0.5,
        _ => 0.3,
    };

    /// <summary>The memory source that names the task the learning came from.</summary>
    /// <param name="sourceTaskId">The task that produced the learning.</param>
    public static string Source(Guid sourceTaskId) => $"ralph:task/{sourceTaskId}";
}
