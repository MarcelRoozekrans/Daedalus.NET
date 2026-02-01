using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;

namespace Daedalus.Tests.Playwright.Browser;

internal class StubLlmService : ILlmService
{
    public string ProviderName => "stub";
    public bool SupportsSubagents => false;

    public Task<Result<string>> InvokeAsync(string prompt, CancellationToken ct) =>
        Task.FromResult(Result.Success("This is a test response from StubLlmService"));

    public Task<Result<string>> InvokeWithMcpAsync(string prompt, McpIntegrationOptions mcpOptions,
        CancellationToken ct) =>
        Task.FromResult(Result.Success("This is a test response with MCP from StubLlmService"));

    public Task<Result<SubagentResult>> InvokeSubagentAsync(string prompt, SubagentOptions options,
        CancellationToken ct) =>
        Task.FromResult(Result.Failure<SubagentResult>("Subagents not supported in test environment"));

    public Task<Result<IReadOnlyList<SubagentResult>>> InvokeParallelSubagentsAsync(
        IReadOnlyList<string> prompts, SubagentOptions options, int maxParallelism = 10,
        CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<IReadOnlyList<SubagentResult>>("Subagents not supported in test environment"));
}
