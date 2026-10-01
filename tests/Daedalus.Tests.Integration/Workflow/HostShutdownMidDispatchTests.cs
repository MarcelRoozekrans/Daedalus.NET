using System.Diagnostics;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Thalos;
using ZeroAlloc.Authorization;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B15 fix round 1: a host stopped while its outbox poller is in the middle of a workflow node's agent turn
///     stops promptly. The stopping token must reach the turn through every layer between the poller and the model:
///     the poller, the outbox dispatcher, Thalos' node dispatcher, the three Daedalus runner decorators and Thalos'
///     subagent runner. A layer that swaps in its own token, or awaits something that ignores it, holds shutdown for
///     the host's whole <see cref="HostOptions.ShutdownTimeout"/>, which this test raises to 60 seconds so such a
///     hang cannot be mistaken for a slow machine.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HostShutdownMidDispatchTests(PostgresFixture fixture)
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Well under <see cref="ShutdownTimeout"/>, and well over what a prompt stop takes on a loaded machine.
    /// </summary>
    private static readonly TimeSpan PromptStop = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Red: forwarding <see cref="CancellationToken.None"/> instead of the dispatch token from any of
    ///     <c>WorkflowOutboxDispatchService.ProcessBatchAsync</c>, <c>WorkflowDispatchOutboxDispatcher</c>,
    ///     <c>StandingInstructionsRunner</c>, <c>ReviewLensRunner</c> or <c>BudgetedSubagentRunner</c> leaves the turn
    ///     running until the shutdown timeout: the token never reaches it, which fails the first assertion, and the
    ///     dispose takes the whole 60 seconds, which fails the second.
    /// </summary>
    [Fact]
    public async Task A_host_stopped_while_a_turn_is_in_flight_stops_well_under_the_shutdown_timeout()
    {
        var runtime = new BlockingRuntime();
        var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime,
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
                services.Configure<HostOptions>(o => o.ShutdownTimeout = ShutdownTimeout);
            });

        TimeSpan elapsed;
        try
        {
            using var client = host.Client("a-developer", "developer");
            var response = await client.PostAsJsonAsync(
                "/api/workflow-runs", new StartWorkflowRunRequest("add a health check endpoint", ScratchWorkflowHost.Repository));
            response.StatusCode.Should().Be(HttpStatusCode.Created);

            await runtime.Entered.Task.WaitAsync(TimeSpan.FromSeconds(120));
        }
        finally
        {
            var stopwatch = Stopwatch.StartNew();
            await host.DisposeAsync();
            elapsed = stopwatch.Elapsed;
        }

        runtime.Cancelled.Task.IsCompletedSuccessfully.Should().BeTrue("the stopping token must reach the in-flight turn");
        elapsed.Should().BeLessThan(PromptStop, "a host whose in-flight turn ignores the stopping token waits out the whole shutdown timeout");
    }

    /// <summary>
    ///     A model call that never answers on its own: it signals <see cref="Entered"/>, then waits on the turn's token,
    ///     and signals <see cref="Cancelled"/> when that token is cancelled.
    /// </summary>
    private sealed class BlockingRuntime : IAgentRuntime
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<Result<SessionId, AgentError>> CreateSessionAsync(AgentId agentId, ISecurityContext caller, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<SessionId, AgentError>.Success(new SessionId(Guid.NewGuid())));

        public ValueTask<UnitResult<AgentError>> CloseSessionAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct = default) =>
            ValueTask.FromResult(UnitResult<AgentError>.Success());

        public IAsyncEnumerable<AgentEvent> RunTurnStreamingAsync(AgentTurnRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("The workflow path runs buffered turns only.");

        public async ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Unreachable: an infinite delay only ends by cancellation.");
        }
    }
}
