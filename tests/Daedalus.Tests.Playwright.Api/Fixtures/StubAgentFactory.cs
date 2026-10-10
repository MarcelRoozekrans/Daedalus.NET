using ZeroAlloc.Results;
using Daedalus.Application.Abstractions;

namespace Daedalus.Tests.Playwright.Api;

/// <summary>
///     Stub implementation of IAgentFactory for E2E testing.
///     Returns deterministic responses without making real LLM calls.
/// </summary>
internal class StubAgentFactory : IAgentFactory
{
    public Task<Result<LlmInvocationResult>> InvokeAsync(string prompt, CancellationToken ct = default) =>
        Task.FromResult(Result<LlmInvocationResult>.Success(new LlmInvocationResult { Response = "This is a test response from StubAgentFactory" }));
}
