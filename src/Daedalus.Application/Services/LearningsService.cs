using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Daedalus.Application.Services;

/// <summary>
///     Parses raw learnings text into structured, categorized entries and provides
///     enrichment context for prompt building. Uses simple text parsing and keyword
///     extraction — no ML models, no embeddings, aligned with Ralph philosophy.
/// </summary>
public sealed partial class LearningsService(
    ILearningsRepository learningsRepository,
    IFailurePatternDatabase failurePatternDatabase,
    ILogger<LearningsService> logger) : ILearningsService
{
    /// <summary>
    ///     Keywords that indicate error patterns in raw learnings text.
    /// </summary>
    private static readonly string[] _errorIndicators =
        ["error", "failed", "exception", "⚠", "ERROR", "crash", "timeout", "CS", "FS", "NU"];

    /// <summary>
    ///     Keywords that indicate success patterns in raw learnings text.
    /// </summary>
    private static readonly string[] _successIndicators =
        ["success", "✓", "worked", "resolved", "fixed", "completed", "passed"];

    /// <summary>
    ///     Keywords that indicate architecture decisions.
    /// </summary>
    private static readonly string[] _architectureIndicators =
        ["pattern", "architecture", "design", "structure", "layer", "pipeline", "middleware"];

    /// <summary>
    ///     Keywords that indicate dependency information.
    /// </summary>
    private static readonly string[] _dependencyIndicators =
        ["package", "nuget", "library", "dependency", "version", "using", "import"];

    /// <summary>
    ///     Common emoji/symbol prefixes to strip from learnings text.
    /// </summary>
    private static readonly string[] _emojiPrefixes =
    [
        "\u2713 ", "\u26A0 ", "\u2139 ", "\uD83D\uDCCA ", "\uD83D\uDD34 ", "\uD83D\uDFE0 ", "\uD83D\uDFE1 ",
        "\uD83D\uDFE2 "
    ];

    public async Task<Result<int>> ParseAndPersistLearningsAsync(
        string rawLearnings,
        Guid sourceTaskId,
        Guid? projectId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rawLearnings))
        {
            return Result.Success(0);
        }

        var entries = ParseRawLearnings(rawLearnings, sourceTaskId, projectId);
        var persistedCount = 0;

        foreach (var entry in entries)
        {
            var addResult = await learningsRepository.AddAsync(entry, ct);
            if (addResult.IsSuccess)
            {
                persistedCount++;
            }
            else
            {
                LogFailedPersistLearning(logger, entry.Pattern, addResult.Error);
            }
        }

        LogLearningsParsed(logger, persistedCount, entries.Count, sourceTaskId);
        return Result.Success(persistedCount);
    }

    public async Task<Result<string>> GetEnrichmentContextAsync(
        string taskPrompt,
        Guid? projectId,
        Guid currentTaskId,
        int maxLearnings,
        int maxFailurePatterns,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        var hasContent = false;

        // 1. Get top learnings by project (if available) or global
        var learningsResult = projectId.HasValue
            ? await learningsRepository.GetByProjectIdAsync(projectId.Value, maxLearnings, ct)
            : await learningsRepository.GetTopLearningsAsync(maxLearnings, ct);

        if (learningsResult.IsSuccess && learningsResult.Value.Count > 0)
        {
            // Filter out self-referencing learnings
            var relevantLearnings = learningsResult.Value
                .AsValueEnumerable()
                .Where(l => l.SourceTaskId != currentTaskId)
                .ToList();

            if (relevantLearnings.Count > 0)
            {
                sb.AppendLine("=== CROSS-TASK LEARNINGS ===");
                sb.AppendLine("Knowledge from previous task executions in this project:");
                sb.AppendLine();

                // Group by category for structured presentation
                // Materialize to List to avoid ZLinq ValueEnumerable crossing await boundary
                var grouped = relevantLearnings
                    .GroupBy(l => l.Category)
                    .OrderByDescending(g => g.Key switch
                    {
                        LearningCategory.ErrorPattern => 4,
                        LearningCategory.SuccessPattern => 3,
                        LearningCategory.ArchitectureDecision => 2,
                        LearningCategory.CodeConvention => 1,
                        _ => 0
                    })
                    .ToList();

                foreach (var group in grouped)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"[{FormatCategory(group.Key)}]");
                    sb.AppendLine();

                    foreach (var learning in group)
                    {
                        var severityIcon = learning.Severity switch
                        {
                            LearningSeverity.Critical => "🔴",
                            LearningSeverity.High => "🟠",
                            LearningSeverity.Medium => "🟡",
                            _ => "🟢"
                        };

                        sb.Append(CultureInfo.InvariantCulture,
                            $"  {severityIcon} Pattern: {learning.Pattern}");
                        sb.AppendLine();
                        sb.Append(CultureInfo.InvariantCulture,
                            $"     Fix: {learning.Resolution}");
                        sb.AppendLine();

                        // Record that this learning was referenced
                        learning.RecordReference();
                        await learningsRepository.UpdateAsync(learning, ct);
                    }

                    sb.AppendLine();
                }

                hasContent = true;
            }
        }

        // 2. Get relevant failure patterns from past executions
        var failureResult = await failurePatternDatabase.SearchByPromptContextAsync(
            taskPrompt, maxFailurePatterns, ct);

        if (failureResult.IsSuccess && failureResult.Value.Count > 0)
        {
            sb.AppendLine("=== KNOWN FAILURE PATTERNS ===");
            sb.AppendLine("Error→Fix pairs from past task executions:");
            sb.AppendLine();

            foreach (var pattern in failureResult.Value)
            {
                var errorSnippet = pattern.ErrorText.Length > 200
                    ? pattern.ErrorText[..200] + "..."
                    : pattern.ErrorText;
                var resolutionSnippet = pattern.Resolution.Length > 300
                    ? pattern.Resolution[..300] + "..."
                    : pattern.Resolution;

                sb.Append(CultureInfo.InvariantCulture,
                    $"  ⚠ Error: {errorSnippet}");
                sb.AppendLine();
                sb.Append(CultureInfo.InvariantCulture,
                    $"    Fix: {resolutionSnippet}");
                sb.AppendLine();
                sb.AppendLine();
            }

            hasContent = true;
        }

        if (!hasContent)
        {
            return Result.Success(string.Empty);
        }

        return Result.Success(sb.ToString());
    }

    /// <summary>
    ///     Parses raw learnings text into structured entries using simple line-by-line analysis.
    ///     No ML, no NLP — just pattern matching aligned with Ralph simplicity.
    /// </summary>
    internal static List<StructuredLearningEntry> ParseRawLearnings(
        string rawLearnings,
        Guid sourceTaskId,
        Guid? projectId)
    {
        var entries = new List<StructuredLearningEntry>();
        var lines = rawLearnings.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var index = 0;

        while (index < lines.Length)
        {
            var line = lines[index].Trim();
            index++;

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            // Detect category from line content
            var category = ClassifyLine(line);
            var severity = DetermineSeverity(line, category);
            var tags = ExtractTags(line);

            // Look for pattern→resolution pairs (current line is pattern, next is resolution)
            string pattern;
            string resolution;

            if (index < lines.Length && IsResolutionLine(lines[index].Trim()))
            {
                pattern = CleanLearningText(line);
                resolution = CleanLearningText(lines[index].Trim());
                index++; // Skip the resolution line
            }
            else
            {
                // Single line — use as both pattern and resolution
                pattern = CleanLearningText(line);
                resolution = pattern;
            }

            if (pattern.Length < 5)
            {
                continue; // Skip very short entries
            }

            var entryResult = StructuredLearningEntry.Create(
                category, pattern, resolution, tags, sourceTaskId, projectId, severity);

            if (entryResult.IsSuccess)
            {
                entries.Add(entryResult.Value);
            }
        }

        return entries;
    }

    /// <summary>
    ///     Classifies a line of raw learnings text into a learning category.
    /// </summary>
    internal static LearningCategory ClassifyLine(string line)
    {
        if (_errorIndicators.AsValueEnumerable().Any(ind => line.Contains(ind, StringComparison.OrdinalIgnoreCase)))
        {
            return LearningCategory.ErrorPattern;
        }

        if (_successIndicators.AsValueEnumerable().Any(ind => line.Contains(ind, StringComparison.OrdinalIgnoreCase)))
        {
            return LearningCategory.SuccessPattern;
        }

        if (_architectureIndicators.AsValueEnumerable()
            .Any(ind => line.Contains(ind, StringComparison.OrdinalIgnoreCase)))
        {
            return LearningCategory.ArchitectureDecision;
        }

        if (_dependencyIndicators.AsValueEnumerable()
            .Any(ind => line.Contains(ind, StringComparison.OrdinalIgnoreCase)))
        {
            return LearningCategory.DependencyInfo;
        }

        return LearningCategory.CodeConvention;
    }

    /// <summary>
    ///     Determines severity based on line content and category.
    /// </summary>
    internal static LearningSeverity DetermineSeverity(string line, LearningCategory category)
    {
        // Critical: build failures, compilation errors
        if (line.Contains("build fail", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("compilation error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("fatal", StringComparison.OrdinalIgnoreCase))
        {
            return LearningSeverity.Critical;
        }

        // High: errors and exceptions
        if (category == LearningCategory.ErrorPattern)
        {
            return LearningSeverity.High;
        }

        // Medium: success patterns and architecture decisions
        if (category is LearningCategory.SuccessPattern or LearningCategory.ArchitectureDecision)
        {
            return LearningSeverity.Medium;
        }

        return LearningSeverity.Low;
    }

    /// <summary>
    ///     Extracts keyword tags from a line of text using simple word extraction.
    /// </summary>
    internal static List<string> ExtractTags(string line)
    {
        // Extract common technology keywords
        ReadOnlySpan<string> techKeywords =
        [
            "ef core", "postgresql", "blazor", "aspire", "keycloak",
            "zlinq", "polly", "result", "middleware", "pipeline",
            "async", "cancellation", "migration", "docker", "api",
            "controller", "repository", "service", "dto", "cqrs"
        ];

        var tags = new List<string>();
        foreach (var keyword in techKeywords)
        {
            if (line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(keyword);
            }
        }

        return tags;
    }

    /// <summary>
    ///     Checks if a line looks like a resolution/fix description.
    /// </summary>
    private static bool IsResolutionLine(string line)
    {
        return line.StartsWith("- use", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("  - ", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("fix:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("resolution:", StringComparison.OrdinalIgnoreCase) ||
               line.StartsWith("solution:", StringComparison.OrdinalIgnoreCase) ||
               line.Contains('\u2713', StringComparison.Ordinal);
    }

    /// <summary>
    ///     Removes common prefixes and formatting from raw learnings text.
    /// </summary>
    private static string CleanLearningText(string text)
    {
        var cleaned = text
            .TrimStart('-', '*', ' ', '\t')
            .TrimStart();

        // Remove common emoji/symbol prefixes
        var matchingPrefix = _emojiPrefixes
            .FirstOrDefault(p => cleaned.StartsWith(p, StringComparison.Ordinal));

        if (matchingPrefix != null)
        {
            cleaned = cleaned[matchingPrefix.Length..].TrimStart();
        }

        // Remove iteration markers like "[Iteration 3]"
        if (cleaned.StartsWith("[Iteration", StringComparison.OrdinalIgnoreCase))
        {
            var closeBracket = cleaned.IndexOf(']', StringComparison.Ordinal);
            if (closeBracket >= 0 && closeBracket + 1 < cleaned.Length)
            {
                cleaned = cleaned[(closeBracket + 1)..].TrimStart();
            }
        }

        return cleaned;
    }

    private static string FormatCategory(LearningCategory category) => category switch
    {
        LearningCategory.ErrorPattern => "⚠ Error Patterns",
        LearningCategory.SuccessPattern => "✓ Success Patterns",
        LearningCategory.CodeConvention => "📝 Code Conventions",
        LearningCategory.DependencyInfo => "📦 Dependencies",
        LearningCategory.ArchitectureDecision => "🏛 Architecture Decisions",
        _ => "📋 General"
    };

    [LoggerMessage(EventId = 100, Level = LogLevel.Debug,
        Message = "Parsed {Persisted}/{Total} learnings from task {TaskId}")]
    private static partial void LogLearningsParsed(ILogger logger, int persisted, int total, Guid taskId);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning,
        Message = "Failed to persist learning entry '{Pattern}': {Error}")]
    private static partial void LogFailedPersistLearning(ILogger logger, string pattern, string error);
}
