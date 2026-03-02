#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Daedalus.Infrastructure.Agents.Tools;

/// <summary>
///     MCP tools for searching known failure patterns and their solutions.
///     Used by the Ralph Loop LLM to find relevant fixes when encountering errors.
/// </summary>
[McpServerToolType]
public sealed partial class DaedalusFailurePatternsTools(
    IFailurePatternDatabase failurePatternDatabase,
    ILogger<DaedalusFailurePatternsTools> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    [McpServerTool(
        Name = "search_failure_patterns",
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Search known failure patterns and their solutions. Use this when you encounter " +
        "a build error, test failure, or runtime exception to find previously discovered fixes.")]
    public async Task<string> SearchFailurePatterns(
        [Description("The error message or pattern to search for")] string errorMessage,
        [Description("Maximum number of results (default: 3)")] int maxResults = 3,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await failurePatternDatabase.SearchByErrorAsync(
                errorMessage, maxResults, cancellationToken);

            if (result.IsSuccess && result.Value.Count > 0)
            {
                LogPatternsFound(logger, errorMessage, result.Value.Count);
                return FormatResults(result.Value);
            }

            return "No matching failure patterns found.";
        }
        catch (Exception ex)
        {
            LogSearchError(logger, ex, errorMessage);
            return $"Error searching failure patterns: {ex.Message}. Proceed with available context.";
        }
    }

    private static string FormatResults(IReadOnlyList<FailurePatternRecord> patterns)
    {
        var results = patterns.Select(p => new
        {
            error = p.ErrorText.Length > 300 ? p.ErrorText[..300] + "..." : p.ErrorText,
            solution = p.Resolution,
            sourceTaskId = p.SourceTaskId,
            errorIteration = p.ErrorIteration,
            resolutionIteration = p.ResolutionIteration,
            observedAt = p.ObservedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });

        return JsonSerializer.Serialize(results, SerializerOptions);
    }

    [LoggerMessage(EventId = 410, Level = LogLevel.Debug,
        Message = "Found {Count} failure patterns for error '{ErrorMessage}'")]
    private static partial void LogPatternsFound(ILogger logger, string errorMessage, int count);

    [LoggerMessage(EventId = 411, Level = LogLevel.Warning,
        Message = "Error searching failure patterns for '{ErrorMessage}'")]
    private static partial void LogSearchError(ILogger logger, Exception exception, string errorMessage);
}
