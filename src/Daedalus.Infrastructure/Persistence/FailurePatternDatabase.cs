using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.FailurePatterns;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Queries TaskExecution records for error→fix pairs from past task executions.
///     Uses simple SQL text search — no vectors, no embeddings.
///     Finds iterations where an error occurred, then looks for subsequent
///     successful iterations on the same task to extract the resolution.
/// </summary>
public sealed partial class FailurePatternDatabase(
    DaedalusDb db,
    ILogger<FailurePatternDatabase> logger) : IFailurePatternDatabase
{
    public async Task<Result<IReadOnlyList<FailurePatternRecord>>> SearchByErrorAsync(
        string errorText, int maxResults, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(errorText))
            {
                return Result<IReadOnlyList<FailurePatternRecord>>.Success([]);
            }

            // Fetch extra errors to allow for resolution matching
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var patterns = await new FailurePatternSql(connection)
                .SearchAsync($"%{errorText}%", maxResults * 2, maxResults, ct).ConfigureAwait(false);

            return Result<IReadOnlyList<FailurePatternRecord>>.Success(patterns);
        }
        catch (Exception ex)
        {
            LogSearchByErrorFailed(logger, ex, errorText);
            return Result<IReadOnlyList<FailurePatternRecord>>.Failure(
                $"Failed to search failure patterns: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<FailurePatternRecord>>> SearchByPromptContextAsync(
        string promptContent, int maxResults, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(promptContent))
            {
                return Result<IReadOnlyList<FailurePatternRecord>>.Success([]);
            }

            // Extract meaningful keywords from prompt for search
            var keywords = ExtractSearchKeywords(promptContent);
            if (keywords.Count == 0)
            {
                return Result<IReadOnlyList<FailurePatternRecord>>.Success([]);
            }

            // Search for error executions matching any of the extracted keywords
            // Use the first few most relevant keywords to avoid overly broad searches
            var searchKeywords = keywords.Take(5).ToList();
            var patterns = new List<FailurePatternRecord>();

            foreach (var keyword in searchKeywords)
            {
                if (patterns.Count >= maxResults)
                {
                    break;
                }

                var remaining = maxResults - patterns.Count;
                var result = await SearchByErrorAsync(keyword, remaining, ct).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    patterns.AddRange(result.Value);
                }
            }

            // Deduplicate by source task + error iteration
            var deduplicated = patterns
                .GroupBy(p => (p.SourceTaskId, p.ErrorIteration))
                .Select(g => g.First())
                .Take(maxResults)
                .ToList();

            return Result<IReadOnlyList<FailurePatternRecord>>.Success(deduplicated);
        }
        catch (Exception ex)
        {
            LogSearchByPromptFailed(logger, ex);
            return Result<IReadOnlyList<FailurePatternRecord>>.Failure(
                $"Failed to search failure patterns by prompt context: {ex.Message}");
        }
    }

    /// <summary>
    ///     Extracts meaningful technical keywords from prompt content for failure pattern search.
    ///     Simple word extraction — no NLP, deliberately simple.
    /// </summary>
    internal static List<string> ExtractSearchKeywords(string promptContent)
    {
        var keywords = new List<string>();

        // Look for common error code patterns (CS1061, NU1605, etc.)
        var words = promptContent.Split([' ', '\n', '\r', '\t', ',', '.', ';', ':', '(', ')'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var word in words)
        {
            // C# compiler errors (CS####), NuGet errors (NU####), MSBuild errors (MSB####)
            if (word.Length >= 4 && word.Length <= 10 &&
                (word.StartsWith("CS", StringComparison.OrdinalIgnoreCase) ||
                 word.StartsWith("NU", StringComparison.OrdinalIgnoreCase) ||
                 word.StartsWith("MSB", StringComparison.OrdinalIgnoreCase)))
            {
                keywords.Add(word);
            }
        }

        // Look for technology-specific terms
        ReadOnlySpan<string> techTerms =
        [
            "migration", "DbContext", "middleware", "controller",
            "repository", "authentication", "authorization",
            "serialization", "dependency injection"
        ];

        foreach (var term in techTerms)
        {
            if (promptContent.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                keywords.Add(term);
            }
        }

        return keywords;
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Error,
        Message = "Failed to search failure patterns by error: {ErrorText}")]
    private static partial void LogSearchByErrorFailed(ILogger logger, Exception exception, string errorText);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error,
        Message = "Failed to search failure patterns by prompt context")]
    private static partial void LogSearchByPromptFailed(ILogger logger, Exception exception);
}
