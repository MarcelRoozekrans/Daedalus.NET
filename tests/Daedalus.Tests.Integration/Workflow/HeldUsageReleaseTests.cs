using Daedalus.Agents.Workflow;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Thalos;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.10 final review, round 2: the per-model usage a review holds is released when the real workflow store commits
///     the run's cancellation, because the host registers <see cref="NodeUsageRecorder"/> as an observer of that store.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class HeldUsageReleaseTests(PostgresFixture fixture)
{
    /// <summary>
    ///     Red: drop the recorder's <c>IWorkflowRunObserver</c> registration in <c>AddDaedalusAgents</c>; the store tells
    ///     nobody, and the hold stays.
    /// </summary>
    [Fact]
    public async Task Cancelling_a_run_through_the_store_releases_what_its_review_held()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime: null,
            settings: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Thalos:Workflow:Sandbox:Enabled"] = "false" });
        var runId = Guid.NewGuid();
        var started = await host.Store.StartAsync(
            new WorkflowStartRequest
            {
                Process = "manufacture",
                Version = 1,
                CorrelationKey = $"held:{runId:N}",
                StartNode = "gate",
                InitialVariables = new Dictionary<string, object?>(StringComparer.Ordinal),
                StartedBy = new RunPrincipal("seed", []),
                RunId = runId,
            },
            CancellationToken.None);
        started.IsSuccess.Should().BeTrue();
        var recorder = host.Factory.Services.GetRequiredService<NodeUsageRecorder>();
        recorder.HoldPasses(runId, 4, [new TurnUsage(1_000, 100, "decided"), new TurnUsage(1_000, 100, "pinned")]);
        recorder.HeldCount.Should().Be(1, "otherwise the release below is not what empties it");

        await host.Store.CancelAsync(runId, "cancelled mid-review", CancellationToken.None);

        recorder.HeldCount.Should().Be(0);
    }
}
