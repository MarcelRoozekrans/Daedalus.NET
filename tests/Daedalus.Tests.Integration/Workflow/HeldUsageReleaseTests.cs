using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
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

    /// <summary>
    ///     Phase 2.15 final review: against the real store, a failed node's held usage is appended only when the run's failure
    ///     commits. <c>FailAsync</c> under a cancelled token, a graceful stop's dispatch token, throws before it commits and
    ///     appends nothing; the same call under a live token commits, and the store's observer appends the one record.
    ///     Red: append in <c>HoldFailure</c> instead of waiting for <c>OnChangedAsync</c>; the record exists after the
    ///     cancelled call, and the first empty assertion fails.
    ///     Red: drop the recorder's <c>IWorkflowRunObserver</c> registration in <c>AddDaedalusAgents</c>; nobody appends, and
    ///     the single-record assertion fails.
    /// </summary>
    [Fact]
    public async Task A_held_failure_is_recorded_only_once_the_store_commits_the_failure()
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
                CorrelationKey = $"held-failure:{runId:N}",
                StartNode = "gate",
                InitialVariables = new Dictionary<string, object?>(StringComparer.Ordinal),
                StartedBy = new RunPrincipal("seed", []),
                RunId = runId,
            },
            CancellationToken.None);
        started.IsSuccess.Should().BeTrue();
        var run = (await host.Store.FindAsync(runId, CancellationToken.None))!;
        var recorder = host.Factory.Services.GetRequiredService<NodeUsageRecorder>();
        recorder.HoldFailure(runId, run.CurrentSeq, run.CurrentNode, "seed", [new TurnUsage(150_000, 5_138, "m")]);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        await host.Store.Invoking(s => s.FailAsync(runId, "stopped", stopping.Token).AsTask())
            .Should().ThrowAsync<OperationCanceledException>();
        (await host.RecordsAsync(runId, WorkflowRunRecord.NodeUsageKind)).Should().BeEmpty("the failure did not commit");

        await host.Store.FailAsync(runId, "stopped", CancellationToken.None);

        (await host.RecordsAsync(runId, WorkflowRunRecord.NodeUsageKind)).Should().ContainSingle();
    }

    /// <summary>
    ///     Fix round 2, against the real store: the stranded sweep fails the run while its node's turn runs, the turn then
    ///     fails and holds its usage, and the dispatcher's <c>FailAsync</c> through <see cref="ReviewHandoffWorkflowStore"/>
    ///     reaches a run that is already terminal. The real store commits nothing and notifies nobody, so the wrapper reads
    ///     the run back as Failed and appends the one record.
    ///     Red: drop the settle call from <c>ReviewHandoffWorkflowStore.FailAsync</c>; no record is written.
    /// </summary>
    [Fact]
    public async Task A_failure_held_after_the_sweep_failed_the_run_is_appended_when_the_dispatcher_fails_it()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime: null,
            settings: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Thalos:Workflow:Sandbox:Enabled"] = "false" });
        var run = await StartRunAsync(host);
        (await host.Store.FailStrandedAsync(run.Id, run.CurrentSeq, "stranded", CancellationToken.None)).Should().BeTrue();
        var recorder = host.Factory.Services.GetRequiredService<NodeUsageRecorder>();
        recorder.HoldFailure(run.Id, run.CurrentSeq, run.CurrentNode, "seed", [new TurnUsage(150_000, 5_138, "m")]);

        await DispatcherStore(host).FailAsync(run.Id, "the turn failed", CancellationToken.None);

        (await host.RecordsAsync(run.Id, WorkflowRunRecord.NodeUsageKind)).Should().ContainSingle();
    }

    /// <summary>
    ///     Fix round 2, against the real store: a run cancelled while its node's turn runs. The dispatcher's
    ///     <c>FailAsync</c> through <see cref="ReviewHandoffWorkflowStore"/> reads the run back as Cancelled and releases the
    ///     hold without a record.
    ///     Red: drop the settle call from <c>ReviewHandoffWorkflowStore.FailAsync</c>; the hold count stays 1.
    ///     Red: settle a Cancelled run as a Failed one; a record is written, and the empty assertion fails.
    /// </summary>
    [Fact]
    public async Task A_failure_held_after_the_run_was_cancelled_is_released_when_the_dispatcher_fails_it()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime: null,
            settings: new Dictionary<string, string?>(StringComparer.Ordinal) { ["Thalos:Workflow:Sandbox:Enabled"] = "false" });
        var run = await StartRunAsync(host);
        await host.Store.CancelAsync(run.Id, "cancelled mid-turn", CancellationToken.None);
        var recorder = host.Factory.Services.GetRequiredService<NodeUsageRecorder>();
        recorder.HoldFailure(run.Id, run.CurrentSeq, run.CurrentNode, "seed", [new TurnUsage(150_000, 5_138, "m")]);

        await DispatcherStore(host).FailAsync(run.Id, "the turn failed", CancellationToken.None);

        recorder.HeldFailureCount.Should().Be(0);
        (await host.RecordsAsync(run.Id, WorkflowRunRecord.NodeUsageKind)).Should().BeEmpty();
    }

    /// <summary>The store wrapper the dispatcher fails runs through, over the host's real store.</summary>
    private static ReviewHandoffWorkflowStore DispatcherStore(ScratchWorkflowHost host) => new(
        host.Store,
        host.Factory.Services.GetRequiredService<IProcessDefinitionStore>(),
        host.Factory.Services.GetRequiredService<NodeUsageRecorder>(),
        host.Factory.Services.GetRequiredService<ReviewResponseRecorder>());

    /// <summary>Starts a manufacture run parked nowhere, at its start node, and reads it back.</summary>
    private static async Task<WorkflowRun> StartRunAsync(ScratchWorkflowHost host)
    {
        var runId = Guid.NewGuid();
        var started = await host.Store.StartAsync(
            new WorkflowStartRequest
            {
                Process = "manufacture",
                Version = 1,
                CorrelationKey = $"settle:{runId:N}",
                StartNode = "gate",
                InitialVariables = new Dictionary<string, object?>(StringComparer.Ordinal),
                StartedBy = new RunPrincipal("seed", []),
                RunId = runId,
            },
            CancellationToken.None);
        started.IsSuccess.Should().BeTrue();
        return (await host.Store.FindAsync(runId, CancellationToken.None))!;
    }
}
