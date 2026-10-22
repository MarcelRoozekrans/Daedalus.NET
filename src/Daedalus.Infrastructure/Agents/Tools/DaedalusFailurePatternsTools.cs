using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Daedalus.Infrastructure.Agents.Tools;

/// <summary>
///     MCP tools for searching known failure patterns and their solutions.
///     Its data predates the phase 2.8 retirement: see <c>FrozenHistory</c>.
/// </summary>
[McpServerToolType]
public sealed partial class DaedalusFailurePatternsTools(
    IFailurePatternDatabase failurePatternDatabase,
    ILogger<DaedalusFailurePatternsTools> logger)
{
    /// <summary>The tool's name, as the model sees it.</summary>
    internal const string ToolName = "search_failure_patterns";

    /// <summary>The tool's description.</summary>
    internal const string ToolDescription =
        "Search known failure patterns and their solutions. Use this when you encounter " +
        "a build error, test failure, or runtime exception to find previously discovered fixes. " +
        FrozenHistory.Notice;

    /// <summary>The <c>errorMessage</c> parameter's description.</summary>
    internal const string ErrorMessageDescription = "The error message or pattern to search for";

    /// <summary>The <c>maxResults</c> parameter's description.</summary>
    internal const string MaxResultsDescription = "Maximum number of results (default: 3)";

    /// <summary>The <c>maxResults</c> parameter's default.</summary>
    internal const int DefaultMaxResults = 3;

    [McpServerTool(
        Name = ToolName,
        ReadOnly = true,
        Idempotent = true)]
    [Description(ToolDescription)]
    public async Task<string> SearchFailurePatterns(
        [Description(ErrorMessageDescription)] string errorMessage,
        [Description(MaxResultsDescription)] int maxResults = DefaultMaxResults,
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

            return "No matching failure patterns found. " + FrozenHistory.Notice;
        }
        catch (Exception ex)
        {
            LogSearchError(logger, ex, errorMessage);
            return $"Error searching failure patterns: {ex.Message}. Proceed with available context.";
        }
    }

    private static string FormatResults(IReadOnlyList<FailurePatternRecord> patterns)
    {
        var results = patterns.Select(p => new FailurePatternResult(
            p.ErrorText.Length > 300 ? p.ErrorText[..300] + "..." : p.ErrorText,
            p.Resolution,
            p.SourceTaskId,
            p.ErrorIteration,
            p.ResolutionIteration,
            p.ObservedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).ToArray();

        return JsonSerializer.Serialize(results, McpToolsJsonContext.Default.FailurePatternResultArray);
    }

    /// <summary>One failure pattern as the tool returns it; the JSON names are the ones the tool has always used.</summary>
    internal sealed record FailurePatternResult(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("solution")] string Solution,
        [property: JsonPropertyName("sourceTaskId")] Guid SourceTaskId,
        [property: JsonPropertyName("errorIteration")] int ErrorIteration,
        [property: JsonPropertyName("resolutionIteration")] int? ResolutionIteration,
        [property: JsonPropertyName("observedAt")] string ObservedAt);

    [LoggerMessage(EventId = 410, Level = LogLevel.Debug,
        Message = "Found {Count} failure patterns for error '{ErrorMessage}'")]
    private static partial void LogPatternsFound(ILogger logger, string errorMessage, int count);

    [LoggerMessage(EventId = 411, Level = LogLevel.Warning,
        Message = "Error searching failure patterns for '{ErrorMessage}'")]
    private static partial void LogSearchError(ILogger logger, Exception exception, string errorMessage);
}
