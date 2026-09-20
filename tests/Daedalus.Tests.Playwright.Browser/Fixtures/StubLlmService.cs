using ZeroAlloc.Results;
using Daedalus.Application.Abstractions;

namespace Daedalus.Tests.Playwright.Browser;

/// <summary>
///     Stub implementation of IRalphAgentFactory for E2E testing.
///     Returns deterministic responses without making real LLM calls.
/// </summary>
internal class StubRalphAgentFactory : IRalphAgentFactory
{
    public Task<Result<LlmInvocationResult>> InvokeAsync(string prompt, CancellationToken ct = default) =>
        Task.FromResult(Result<LlmInvocationResult>.Success(new LlmInvocationResult { Response = "This is a test response from StubRalphAgentFactory" }));

    public Task<Result<SubagentResult>> InvokeSubagentAsync(
        string prompt, SubagentOptions options, CancellationToken ct = default) =>
        Task.FromResult(Result<SubagentResult>.Failure("Subagents not supported in test environment"));

    public Task<Result<IReadOnlyList<SubagentResult>>> RunParallelSubagentsAsync(
        IReadOnlyList<string> prompts, SubagentOptions options, int maxParallelism = 10,
        CancellationToken ct = default) =>
        Task.FromResult(
            Result<IReadOnlyList<SubagentResult>>.Failure("Parallel subagents not supported in test environment"));
}
