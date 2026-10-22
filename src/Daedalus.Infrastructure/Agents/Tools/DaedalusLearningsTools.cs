using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Daedalus.Infrastructure.Agents.Tools;

/// <summary>
///     MCP tool: semantic recall of shared learnings from the agent memory (<see cref="ILearningsMemory"/>).
///     Its data predates the phase 2.8 retirement: see <c>FrozenHistory</c>.
/// </summary>
/// <param name="memory">The learnings memory port (Thalos memory behind the adapter in <c>Daedalus.Agents</c>).</param>
/// <param name="logger">Logger.</param>
[McpServerToolType]
public sealed partial class DaedalusLearningsTools(ILearningsMemory memory, ILogger<DaedalusLearningsTools> logger)
{
    /// <summary>The tool's name, as the model sees it.</summary>
    internal const string ToolName = "search_learnings";

    /// <summary>The tool's description.</summary>
    internal const string ToolDescription =
        "Search past learnings from previous task executions using semantic similarity. " +
        "Use this when you encounter errors, need context about the codebase, or want to " +
        "learn from previous approaches that worked or failed. " +
        FrozenHistory.Notice;

    /// <summary>The <c>query</c> parameter's description.</summary>
    internal const string QueryDescription = "Natural language description of what you're looking for";

    /// <summary>The <c>maxResults</c> parameter's description.</summary>
    internal const string MaxResultsDescription = "Maximum number of results (default: 5)";

    /// <summary>The <c>maxResults</c> parameter's default.</summary>
    internal const int DefaultMaxResults = 5;

    /// <summary>Recalls learnings relevant to <paramref name="query"/> as a JSON array; never throws at the model.</summary>
    /// <param name="query">Natural-language description of what to look for.</param>
    /// <param name="maxResults">Maximum number of results (clamped to the learnings recall range).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [McpServerTool(
        Name = ToolName,
        ReadOnly = true,
        Idempotent = true)]
    [Description(ToolDescription)]
    public async Task<string> SearchLearnings(
        [Description(QueryDescription)] string query,
        [Description(MaxResultsDescription)] int maxResults = DefaultMaxResults,
        CancellationToken cancellationToken = default)
    {
        var recalled = await memory.RecallAsync(
            query,
            Math.Clamp(maxResults, LearningsRecallConfiguration.MinTopK, LearningsRecallConfiguration.MaxToolTopK),
            cancellationToken).ConfigureAwait(false);
        if (recalled.IsFailure)
        {
            LogRecallFailed(logger, query, recalled.Error);
            return $"Learnings memory unavailable ({recalled.Error}). Proceed with available context.";
        }

        if (recalled.Value.Count == 0)
        {
            return "No matching learnings found. " + FrozenHistory.Notice;
        }

        LogRecalled(logger, query, recalled.Value.Count);
        var results = recalled.Value.Select(l => new LearningResult(
            l.Id,
            l.Text,
            l.Tags,
            Math.Round(l.Score, 3),
            l.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))).ToArray();

        return JsonSerializer.Serialize(results, McpToolsJsonContext.Default.LearningResultArray);
    }

    /// <summary>One recalled learning as the tool returns it; the JSON names are the ones the tool has always used.</summary>
    internal sealed record LearningResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
        [property: JsonPropertyName("score")] double Score,
        [property: JsonPropertyName("createdAt")] string CreatedAt);

    [LoggerMessage(EventId = 400, Level = LogLevel.Debug,
        Message = "Recalled {Count} learnings for query '{Query}'")]
    private static partial void LogRecalled(ILogger logger, string query, int count);

    [LoggerMessage(EventId = 402, Level = LogLevel.Warning,
        Message = "Recalling learnings for query '{Query}' failed: {Error}")]
    private static partial void LogRecallFailed(ILogger logger, string query, string error);
}
