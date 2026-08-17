using Daedalus.Domain.Entities;

namespace Daedalus.Application.Services;

/// <summary>
///     How a Ralph learning becomes a memory record. Mirrors the SQL in the <c>AddAgentMemories</c> migration (text, tags,
///     importance, source) — change both together.
/// </summary>
/// <remarks>
///     The parser feeds this from raw LLM output, so every value is truncated to the memory limits
///     (<see cref="AgentMemory.MaxTextLength"/>, <see cref="AgentMemory.MaxTagLength"/>, <see cref="AgentMemory.MaxTags"/>)
///     rather than left to fail validation: one over-long stack trace or keyword must not cost the whole learning.
/// </remarks>
public static class LearningMemoryMapping
{
    /// <summary>Free tags kept per learning; plus the category and severity tags that makes the 10-tag limit.</summary>
    public const int MaxFreeTags = AgentMemory.MaxTags - 2;

    /// <summary>
    ///     The memory text: the pattern, and the resolution on a second line when it adds anything. Truncated to
    ///     <see cref="AgentMemory.MaxTextLength"/>.
    /// </summary>
    /// <param name="pattern">The learning's pattern half.</param>
    /// <param name="resolution">The learning's resolution half.</param>
    public static string Text(string pattern, string resolution)
    {
        var text = string.Equals(pattern, resolution, StringComparison.Ordinal) ? pattern : $"{pattern}\n{resolution}";
        return Truncate(text, AgentMemory.MaxTextLength);
    }

    /// <summary>
    ///     Category and severity first (they are queryable facets), then the lower-cased free tags. Blanks and duplicates are
    ///     dropped, each tag is truncated to <see cref="AgentMemory.MaxTagLength"/>, and at most
    ///     <see cref="AgentMemory.MaxTags"/> tags come back.
    /// </summary>
    /// <param name="category">The learning category.</param>
    /// <param name="severity">The learning severity.</param>
    /// <param name="tags">Free tags extracted from the raw line.</param>
    public static IReadOnlyList<string> Tags(LearningCategory category, LearningSeverity severity, IEnumerable<string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var list = new List<string> { Tag(category.ToString()), Tag(severity.ToString()) };

        list.AddRange(tags
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(Tag)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxFreeTags));

        return list;

        // Memory tags are lower-case by convention (Thalos normalises the same way), not for security decisions.
#pragma warning disable CA1308
        static string Tag(string value) => Truncate(value.Trim().ToLowerInvariant(), AgentMemory.MaxTagLength);
#pragma warning restore CA1308
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

    // Cuts one char short when the boundary would split a surrogate pair: the aggregate validates on
    // string.Length, so a lone surrogate passes validation but is not encodable as UTF-8 and Npgsql throws
    // when writing it. Reachable in practice — the parser's input is raw LLM output and the severity
    // prefixes it strips are emoji.
    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..cut];
    }
}
