using System.Collections.ObjectModel;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.ExternalServices.Mcp;

/// <summary>
///     No-op implementation of <see cref="IMcpAgentSelector" /> that returns empty results.
///     Use this when no MCP server is configured.
///     Replace with a real implementation when an MCP agent backend is available.
/// </summary>
public sealed partial class NoOpMcpAgentSelector(
    ILogger<NoOpMcpAgentSelector> logger) : IMcpAgentSelector
{
    private static readonly IReadOnlyList<AgentMetadata> _emptyAgentList =
        new ReadOnlyCollection<AgentMetadata>([]);

    private static readonly IDictionary<string, IReadOnlyList<AgentMetadata>> _emptyAgentDict =
        new ReadOnlyDictionary<string, IReadOnlyList<AgentMetadata>>(
            new Dictionary<string, IReadOnlyList<AgentMetadata>>(StringComparer.Ordinal));

    public Task<Result<IReadOnlyList<AgentMetadata>>> FindAgentsForPromptAsync(
        string prompt,
        IReadOnlyList<string>? tags = null,
        int maxResults = 5,
        CancellationToken ct = default)
    {
        LogNoOpFind(logger, prompt.Length);
        return Task.FromResult(Result.Success(_emptyAgentList));
    }

    public Task<Result<IDictionary<string, IReadOnlyList<AgentMetadata>>>> GetAvailableAgentsAsync(
        CancellationToken ct = default)
    {
        LogNoOpList(logger);
        return Task.FromResult(Result.Success(_emptyAgentDict));
    }

    public Task<Result<AgentMetadata>> GetAgentAsync(
        string agentId,
        CancellationToken ct = default)
    {
        return Task.FromResult(
            Result.Failure<AgentMetadata>($"No MCP agent backend configured. Cannot retrieve agent '{agentId}'"));
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message =
            "NoOp MCP agent selector: FindAgentsForPrompt called (prompt length={PromptLength}). No agents available")]
    private static partial void LogNoOpFind(ILogger logger, int promptLength);

    [LoggerMessage(EventId = 2, Level = LogLevel.Debug,
        Message = "NoOp MCP agent selector: GetAvailableAgents called. No agents available")]
    private static partial void LogNoOpList(ILogger logger);
}
