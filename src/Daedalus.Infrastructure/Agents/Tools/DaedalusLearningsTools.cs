#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute

using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace Daedalus.Infrastructure.Agents.Tools;

/// <summary>MCP tool for the Ralph Loop: semantic recall of shared learnings from the agent memory (<see cref="ILearningsMemory"/>).</summary>
/// <param name="memory">The learnings memory port (Thalos memory behind the adapter in <c>Daedalus.Agents</c>).</param>
/// <param name="logger">Logger.</param>
[McpServerToolType]
public sealed partial class DaedalusLearningsTools(ILearningsMemory memory, ILogger<DaedalusLearningsTools> logger)
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>Recalls learnings relevant to <paramref name="query"/> as a JSON array; never throws at the model.</summary>
    /// <param name="query">Natural-language description of what to look for.</param>
    /// <param name="maxResults">Maximum number of results (clamped to the Ralph recall range).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
        [Description("Maximum number of results (default: 5)")] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var recalled = await memory.RecallAsync(
            query,
            Math.Clamp(maxResults, RalphRecallConfiguration.MinTopK, RalphRecallConfiguration.MaxToolTopK),
            cancellationToken).ConfigureAwait(false);
        if (recalled.IsFailure)
        {
            LogRecallFailed(logger, query, recalled.Error);
            return $"Learnings memory unavailable ({recalled.Error}). Proceed with available context.";
        }

        if (recalled.Value.Count == 0)
        {
            return "No matching learnings found.";
        }

        LogRecalled(logger, query, recalled.Value.Count);
        var results = recalled.Value.Select(l => new
        {
            id = l.Id,
            text = l.Text,
            tags = l.Tags,
            score = Math.Round(l.Score, 3),
            createdAt = l.CreatedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });

        return JsonSerializer.Serialize(results, SerializerOptions);
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Debug,
        Message = "Recalled {Count} learnings for query '{Query}'")]
    private static partial void LogRecalled(ILogger logger, string query, int count);

    [LoggerMessage(EventId = 402, Level = LogLevel.Warning,
        Message = "Recalling learnings for query '{Query}' failed: {Error}")]
    private static partial void LogRecallFailed(ILogger logger, string query, string error);
}
