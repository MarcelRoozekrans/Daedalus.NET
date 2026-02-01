using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Tests.Playwright.Browser;

internal class StubMcpAgentSelector : IMcpAgentSelector
{
    public Task<Result<IReadOnlyList<AgentMetadata>>> FindAgentsForPromptAsync(
        string prompt, IReadOnlyList<string>? tags = null, int maxResults = 5, CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<AgentMetadata>>(new List<AgentMetadata>()));

    public Task<Result<IDictionary<string, IReadOnlyList<AgentMetadata>>>> GetAvailableAgentsAsync(
        CancellationToken ct = default) =>
        Task.FromResult(
            Result.Success<IDictionary<string, IReadOnlyList<AgentMetadata>>>(
                new Dictionary<string, IReadOnlyList<AgentMetadata>>()));

    public Task<Result<AgentMetadata>> GetAgentAsync(string agentId, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<AgentMetadata>("No MCP agents available in test environment"));
}
