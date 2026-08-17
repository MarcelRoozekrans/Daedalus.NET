#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Daedalus.Infrastructure.Agents.Tools;

/// <summary>
///     MCP tools for searching structured learnings from the knowledge base.
///     Used by the Ralph Loop LLM to query relevant learnings on-demand.
/// </summary>
[McpServerToolType]
public sealed partial class DaedalusLearningsTools(
    ILearningsRepository learningsRepository,
    IEmbeddingService embeddingService,
    ILogger<DaedalusLearningsTools> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    [McpServerTool(
        Name = "search_learnings",
        ReadOnly = true,
        Idempotent = true)]
    [Description(
        "Search past learnings from previous task executions using semantic similarity. " +
        "Use this when you encounter errors, need context about the codebase, or want to " +
        "learn from previous approaches that worked or failed.")]
    public async Task<string> SearchLearnings(
        [Description("Natural language description of what you're looking for")] string query,
        [Description("Filter to learnings from a specific project (optional)")] string? projectId = null,
        [Description("Maximum number of results (default: 5)")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Guid? parsedProjectId = null;
            if (!string.IsNullOrEmpty(projectId) && Guid.TryParse(projectId, out var parsed))
            {
                parsedProjectId = parsed;
            }

            // Try semantic search first
            if (embeddingService.IsAvailable)
            {
                var embeddingResult = await embeddingService.GenerateEmbeddingAsync(query, cancellationToken);
                if (embeddingResult.IsSuccess)
                {
                    var semanticResult = await learningsRepository.SemanticSearchAsync(
                        embeddingResult.Value, parsedProjectId, maxResults, cancellationToken);

                    if (semanticResult.IsSuccess && semanticResult.Value.Count > 0)
                    {
                        LogSemanticSearchUsed(logger, query, semanticResult.Value.Count);
                        return FormatResults(semanticResult.Value);
                    }
                }
            }

            // Fallback: keyword search
            LogKeywordFallback(logger, query);
            var keywordResult = await learningsRepository.SearchByKeywordAsync(
                query, maxResults, cancellationToken);

            if (keywordResult.IsSuccess && keywordResult.Value.Count > 0)
            {
                return FormatResults(keywordResult.Value);
            }

            return "No matching learnings found.";
        }
        catch (Exception ex)
        {
            LogSearchError(logger, ex, query);
            return $"Error searching learnings: {ex.Message}. Proceed with available context.";
        }
    }

    private static string FormatResults(IReadOnlyList<StructuredLearningEntry> entries)
    {
        var results = entries.Select(e => new
        {
            category = e.Category.ToString(),
            pattern = e.Pattern,
            resolution = e.Resolution,
            severity = e.Severity.ToString(),
            hitCount = e.HitCount,
            createdAt = e.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        });

        return JsonSerializer.Serialize(results, SerializerOptions);
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Debug,
        Message = "Semantic search used for query '{Query}', found {Count} results")]
    private static partial void LogSemanticSearchUsed(ILogger logger, string query, int count);

    [LoggerMessage(EventId = 401, Level = LogLevel.Debug,
        Message = "Falling back to keyword search for query '{Query}'")]
    private static partial void LogKeywordFallback(ILogger logger, string query);

    [LoggerMessage(EventId = 402, Level = LogLevel.Warning,
        Message = "Error searching learnings for query '{Query}'")]
    private static partial void LogSearchError(ILogger logger, Exception exception, string query);
}
