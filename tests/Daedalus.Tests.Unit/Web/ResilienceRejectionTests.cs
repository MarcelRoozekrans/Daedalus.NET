using Daedalus.Web.Services;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Daedalus.Tests.Unit.Web;

/// <summary>
///     The resilience handler production adds rejects a request with an <c>ExecutionRejectedException</c>: its own timeout,
///     or an open circuit. Neither derives from HttpRequestException, so every client helper maps it to a failure as it
///     maps a request that failed, and none lets it escape to Blazor's error UI.
/// </summary>
public sealed class ResilienceRejectionTests
{
    private const string Circuit = "The circuit is now open.";

    private static HttpClient Http() =>
        new(new ThrowingHandler()) { BaseAddress = new Uri("http://localhost") };

    /// <summary>Each helper by name, through a client whose every request the circuit rejects.</summary>
    private static Task<string?> FailureOf(string helper) => helper switch
    {
        "ApiClient.SendAsync<T>" => Error(new ApiClient(Http()).ManufactureTaskAsync(Guid.NewGuid())),
        "ApiClient.SendAsync" => Error(new ApiClient(Http()).RetryRunAsync(Guid.NewGuid())),
        "ApiClient.GetRunAsync" => Error(new ApiClient(Http()).GetRunAsync(Guid.NewGuid())),
        "AgentApiClient.CreateSessionAsync" => Error(new AgentApiClient(Http()).CreateSessionAsync("agent")),
        "AgentApiClient.CloseSessionAsync" => Error(new AgentApiClient(Http()).CloseSessionAsync("session")),
        "AgentApiClient.ForgetMemoryAsync" => Error(new AgentApiClient(Http()).ForgetMemoryAsync("memory")),
        "AgentApiClient.GetAsync" => Error(new AgentApiClient(Http()).ListSessionsAsync()),
        _ => throw new ArgumentOutOfRangeException(nameof(helper), helper, "Not a helper."),
    };

    private static async Task<string?> Error<T>(Task<Result<T>> call) => (await call).Error;

    private static async Task<string?> Error(Task<Result> call) => (await call).Error;

    /// <summary>
    ///     An open circuit is a failure that names the circuit, worded as a request that failed.
    ///     Red: drop ExecutionRejectedException from the filter of the helper named; the call throws.
    /// </summary>
    [Theory]
    [InlineData("ApiClient.SendAsync<T>")]
    [InlineData("ApiClient.SendAsync")]
    [InlineData("ApiClient.GetRunAsync")]
    [InlineData("AgentApiClient.CreateSessionAsync")]
    [InlineData("AgentApiClient.CloseSessionAsync")]
    [InlineData("AgentApiClient.ForgetMemoryAsync")]
    [InlineData("AgentApiClient.GetAsync")]
    public async Task A_rejection_is_a_failure_not_an_exception(string helper)
    {
        (await FailureOf(helper)).Should().Be($"API error: {Circuit}");
    }

    /// <summary>
    ///     The resilience timeout is a rejection too, and is mapped by the same filter.
    ///     Red: catch BrokenCircuitException alone instead of ExecutionRejectedException in ApiClient.SendAsync; the
    ///     timeout throws.
    /// </summary>
    [Fact]
    public async Task A_resilience_timeout_is_a_failure_not_an_exception()
    {
        var client = new ApiClient(new HttpClient(new ThrowingHandler(new TimeoutRejectedException("Timed out.")))
        {
            BaseAddress = new Uri("http://localhost"),
        });

        (await client.RetryRunAsync(Guid.NewGuid())).Error.Should().Be("API error: Timed out.");
    }

    private sealed class ThrowingHandler(Exception? rejection = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw rejection ?? new BrokenCircuitException(Circuit);
    }
}
