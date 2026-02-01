using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Application.Services;

/// <summary>
///     Parses LLM responses inline using simple pattern matching to extract
///     structured learnings without file I/O or subagent calls.
///     Aligned with Ralph philosophy: simple text parsing, no ML, no embeddings.
///     Runs after every iteration at near-zero cost.
/// </summary>
public sealed partial class LlmResponseParser(
    ILogger<LlmResponseParser> logger) : ILlmResponseParser
{
    /// <summary>Error indicators found in LLM output (compiler errors, exceptions, etc.).</summary>
    private static readonly string[] _errorMarkers =
    [
        "error CS", "error FS", "error NU", "error TS", // Compiler errors
        "Build FAILED", "build failed", "dotnet build",
        "Exception:", "exception:", "NullReferenceException",
        "System.InvalidOperationException", "ArgumentException",
        "FAILED:", "FAIL:", "✗", "❌",
        "cannot find", "does not exist", "is not defined",
        "missing reference", "unresolved", "compilation error"
    ];

    /// <summary>Approach/strategy signals in LLM responses.</summary>
    private static readonly string[] _approachMarkers =
    [
        "I'll ", "I will ", "Let me ", "My approach",
        "First, ", "Next, ", "Then, ", "Finally, ",
        "The fix is", "The solution is", "To resolve this",
        "I need to ", "We should ", "The issue is",
        "refactor", "restructure", "replace", "add", "remove",
        "implement", "create", "update", "modify", "change"
    ];

    /// <summary>File modification signals.</summary>
    private static readonly string[] _fileMarkers =
    [
        ".cs", ".csproj", ".json", ".md", ".yaml", ".yml",
        ".ts", ".js", ".tsx", ".jsx", ".html", ".css",
        "src/", "tests/", "Controllers/", "Services/",
        "appsettings", "Program.cs", "Startup.cs"
    ];

    /// <summary>Stuck/loop indicators.</summary>
    private static readonly string[] _stuckMarkers =
    [
        "as mentioned before", "as I said", "same approach",
        "tried this already", "previously attempted",
        "still getting", "same error", "persists"
    ];

    public Result<ParsedIterationLearnings> ParseResponse(
        string llmResponse,
        int iteration,
        string completionPromise,
        string? previousLearnings)
    {
        if (string.IsNullOrWhiteSpace(llmResponse))
        {
            return Result.Success(CreateEmptyLearnings(iteration));
        }

        var responseSpan = llmResponse.AsSpan();
        var lines = llmResponse.Split('\n');
        var errors = ExtractPatterns(lines, _errorMarkers, 5);
        var approaches = ExtractPatterns(lines, _approachMarkers, 5);
        var modifiedAreas = ExtractFileReferences(lines);
        var stuckDetected = DetectStuckPattern(llmResponse, previousLearnings);
        var progressSignal = EstimateProgress(responseSpan, completionPromise.AsSpan());

        var summary = BuildCompactSummary(
            iteration, errors, approaches, modifiedAreas, stuckDetected, progressSignal);

        var learnings = new ParsedIterationLearnings
        {
            ErrorPatterns = errors,
            ApproachSignals = approaches,
            ModifiedAreas = modifiedAreas,
            StuckDetected = stuckDetected,
            ProgressSignal = progressSignal,
            CompactSummary = summary
        };

        if (learnings.HasLearnings)
        {
            LogLearningsParsed(logger, iteration, errors.Count, approaches.Count, modifiedAreas.Count);
        }

        return Result.Success(learnings);
    }

    /// <summary>
    ///     Extracts lines containing any of the marker patterns.
    ///     Returns the relevant line snippets, not the full response.
    /// </summary>
    private static List<string> ExtractPatterns(string[] lines, string[] markers, int maxResults)
    {
        var results = new List<string>(maxResults);

        foreach (var line in lines)
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length < 5 || trimmed.Length > 500)
            {
                continue;
            }

            foreach (var marker in markers)
            {
                if (!trimmed.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Take up to 200 chars of the matched line
                var snippet = trimmed.Length > 200 ? trimmed[..200] + "..." : trimmed;
                results.Add(snippet);
                break; // One match per line is enough
            }
        }

        return results;
    }

    /// <summary>
    ///     Extracts file paths and areas referenced in the response.
    /// </summary>
    private static List<string> ExtractFileReferences(string[] lines)
    {
        var results = new List<string>(10);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            foreach (var marker in _fileMarkers)
            {
                var index = trimmed.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                // Extract a reasonable file reference around the marker
                var start = FindWordStart(trimmed, index);
                var end = FindWordEnd(trimmed, index + marker.Length);
                var reference = trimmed[start..end].Trim('`', '\'', '"', '(', ')', '[', ']', ',', ' ');

                if (reference.Length is >= 3 and <= 200 && seen.Add(reference))
                {
                    results.Add(reference);
                    if (results.Count >= 10)
                    {
                        return results;
                    }
                }

                break;
            }
        }

        return results;
    }

    /// <summary>
    ///     Detects whether the LLM appears stuck (repeating previous approaches).
    /// </summary>
    private static bool DetectStuckPattern(string response, string? previousLearnings)
    {
        // Check for explicit stuck markers
        if (_stuckMarkers.Any(marker => response.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // If we have previous learnings, check if this response is suspiciously similar
        if (!string.IsNullOrEmpty(previousLearnings) && previousLearnings.Length > 100)
        {
            // Simple heuristic: if the first 200 chars of the response closely match
            // a segment in previous learnings, the LLM is likely repeating itself
            var responseStart = response.Length > 200 ? response[..200] : response;
            if (previousLearnings.Contains(responseStart, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Estimates how close the LLM response is to the completion promise.
    ///     Uses keyword overlap as a heuristic — not an exact measure.
    /// </summary>
    private static double EstimateProgress(ReadOnlySpan<char> response, ReadOnlySpan<char> completionPromise)
    {
        if (completionPromise.IsEmpty)
        {
            return 0.0;
        }

        // Check for exact match first
        if (response.Contains(completionPromise, StringComparison.OrdinalIgnoreCase))
        {
            return 1.0;
        }

        // Split completion promise into words and check how many appear in the response
        var promiseStr = completionPromise.ToString();
        var words = promiseStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return 0.0;
        }

        var matchCount = 0;
        foreach (var word in words)
        {
            if (word.Length >= 3 && response.Contains(word.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                matchCount++;
            }
        }

        return (double)matchCount / words.Length;
    }

    /// <summary>
    ///     Builds a compact text summary for prompt injection.
    ///     Designed to be short and actionable — not a full recap.
    /// </summary>
    private static string BuildCompactSummary(
        int iteration,
        List<string> errors,
        List<string> approaches,
        List<string> modifiedAreas,
        bool stuckDetected,
        double progressSignal)
    {
        var sb = new StringBuilder(256);
        sb.Append(CultureInfo.InvariantCulture, $"[Iter {iteration}]");

        if (stuckDetected)
        {
            sb.Append(" ⚠ STUCK DETECTED — try a fundamentally different approach.");
        }

        if (errors.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" Errors({errors.Count}): ");
            sb.Append(errors[0].Length > 100 ? errors[0][..100] + "..." : errors[0]);
        }

        if (approaches.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" Approach: ");
            sb.Append(approaches[0].Length > 100 ? approaches[0][..100] + "..." : approaches[0]);
        }

        if (modifiedAreas.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $" Files({modifiedAreas.Count}): ");
            sb.AppendJoin(", ", modifiedAreas.Take(3));
        }

        sb.Append(CultureInfo.InvariantCulture, $" Progress: {progressSignal:P0}");

        return sb.ToString();
    }

    private static ParsedIterationLearnings CreateEmptyLearnings(int iteration) =>
        new()
        {
            ErrorPatterns = [],
            ApproachSignals = [],
            ModifiedAreas = [],
            StuckDetected = false,
            ProgressSignal = 0.0,
            CompactSummary = $"[Iter {iteration}] Empty response"
        };

    private static int FindWordStart(string text, int index)
    {
        while (index > 0 && !char.IsWhiteSpace(text[index - 1]))
        {
            index--;
        }

        return index;
    }

    private static int FindWordEnd(string text, int index)
    {
        while (index < text.Length && !char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message =
            "Inline learnings parsed for iteration {Iteration}: errors={Errors}, approaches={Approaches}, files={Files}")]
    private static partial void
        LogLearningsParsed(ILogger logger, int iteration, int errors, int approaches, int files);
}
