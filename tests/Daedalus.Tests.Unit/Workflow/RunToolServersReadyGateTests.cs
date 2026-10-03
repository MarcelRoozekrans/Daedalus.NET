using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using Thalos.Mcp;
using Thalos.Sandbox;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Task B9: the gate's own contract, over a fake workspace provider and readiness. The dispatch-level behaviour, a
///     refused turn failing the run and nothing being dispatched, is pinned by the integration tests of the same name.
/// </summary>
public sealed class RunToolServersReadyGateTests
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    ///     A readiness implementation whose own timeout escapes as <see cref="OperationCanceledException"/> would otherwise
    ///     be retried by the outbox as a failed attempt and strand the run with no message. Red: remove the catch; the
    ///     exception then escapes.
    /// </summary>
    [Fact]
    public async Task An_operation_cancelled_the_dispatch_did_not_ask_for_is_a_refusal()
    {
        var readiness = Substitute.For<IRunToolServerReadiness>();
        readiness.WaitAllReadyAsync(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<UnitResult<AgentError>>>(_ => throw new TaskCanceledException("the wait timed out"));
        var gate = Gate(workspaceExists: true, readiness);

        var act = async () => await gate.BeforeTaskNodeAsync(Run(), "implement", CancellationToken.None);

        var result = (await act.Should().NotThrowAsync()).Subject;
        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("not ready");
    }

    /// <summary>
    ///     Only a cancelled dispatch token may surface as <see cref="OperationCanceledException"/>. Red: catch every
    ///     <see cref="OperationCanceledException"/>, whatever the token says.
    /// </summary>
    [Fact]
    public async Task Cancelling_the_dispatch_during_the_wait_throws()
    {
        using var cts = new CancellationTokenSource();
        var readiness = Substitute.For<IRunToolServerReadiness>();
        readiness.WaitAllReadyAsync(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask<UnitResult<AgentError>>>(call => throw new OperationCanceledException(call.Arg<CancellationToken>()));
        var gate = Gate(workspaceExists: true, readiness);
        await cts.CancelAsync();

        var act = async () => await gate.BeforeTaskNodeAsync(Run(), "implement", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    ///     Red for the timeout: pass another value than <see cref="WorkflowConfig.RoslynReadyTimeout"/>. Red for the run:
    ///     wait on another id.
    /// </summary>
    [Fact]
    public async Task The_wait_is_for_this_run_and_bounded_by_the_configured_timeout()
    {
        var readiness = Substitute.For<IRunToolServerReadiness>();
        readiness.WaitAllReadyAsync(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Success()));
        var run = Run();

        var result = await Gate(workspaceExists: true, readiness).BeforeTaskNodeAsync(run, "implement", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await readiness.Received(1).WaitAllReadyAsync(run.Id, ReadyTimeout, Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A run with no workspace, at a node no grant names, has nothing to wait for. Red: fail every run with no
    ///     workspace; the review row is then refused.
    /// </summary>
    [Fact]
    public async Task An_ungranted_node_of_a_run_with_no_workspace_passes_without_waiting()
    {
        var readiness = Substitute.For<IRunToolServerReadiness>();

        var result = await Gate(workspaceExists: false, readiness).BeforeTaskNodeAsync(Run(), "review", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await readiness.DidNotReceive().WaitAllReadyAsync(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    ///     A grant for another process names no node of this run. Red: match the grant on the node alone.
    /// </summary>
    [Fact]
    public async Task A_grant_for_another_process_does_not_fail_a_run_with_no_workspace()
    {
        var result = await Gate(workspaceExists: false, readiness: null, grantProcess: "other")
            .BeforeTaskNodeAsync(Run(), "implement", CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
    }

    /// <summary>
    ///     Phase 2.6, task B5: a sandbox whose restore failed is still ready, so the run proceeds, and the failure is
    ///     recorded once with the restore's output tail, however many of the run's nodes pass the gate. The store here
    ///     lists nothing, so only the gate's own once-per-run claim keeps the second node from recording again.
    /// </summary>
    /// <remarks>
    ///     Red for the run proceeding: fail the gate on a restore failure; both results then fail. Red for once: drop the
    ///     claim, reading and recording at every node; the store then receives two appends, and the readiness two reads.
    ///     Red for the read: read another run's readiness. Red for the record, per field: take the detail from
    ///     <see cref="SandboxReadiness.Detail"/>, Roslyn's, not <see cref="SandboxReadiness.RestoreDetail"/>; record another
    ///     kind, another run, <c>run.CurrentNode</c> instead of the gated node, or another principal; each fails its line.
    /// </remarks>
    [Fact]
    public async Task A_failed_restore_is_recorded_once_and_the_run_proceeds()
    {
        var records = new FakeRecords();
        var sandbox = new FakeSandboxReadiness(FailedRestore);
        var gate = Gate(workspaceExists: true, Ready(), sandbox.ReadAsync, records: records);
        var run = Run();

        var first = await gate.BeforeTaskNodeAsync(run, "test", CancellationToken.None);
        var second = await gate.BeforeTaskNodeAsync(run, "review", CancellationToken.None);

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        sandbox.Reads.Should().Be(1);
        sandbox.LastRunId.Should().Be(run.Id);
        var record = records.Appended.Should().ContainSingle().Subject;
        record.Kind.Should().Be(WorkflowRunRecord.SandboxRestoreKind);
        record.RunId.Should().Be(run.Id);
        record.Node.Should().Be("test");
        record.PrincipalId.Should().Be(RunToolServersReadyGate.SandboxPrincipalId);
        using var payload = JsonDocument.Parse(record.PayloadJson);
        payload.RootElement.GetProperty("restore").GetString().Should().Be("failed");
        payload.RootElement.GetProperty("detail").GetString().Should().Be("dotnet restore exited 1\nNU1101: no package");
    }

    /// <summary>
    ///     A restore that succeeded records nothing, and is not read again. Red: record whatever the restore state is.
    /// </summary>
    [Fact]
    public async Task A_restore_that_succeeded_records_nothing()
    {
        var records = new FakeRecords();
        var sandbox = new FakeSandboxReadiness(new SandboxReadiness(true, "ok", "Restored.", "ready", null));
        var gate = Gate(workspaceExists: true, Ready(), sandbox.ReadAsync, records: records);
        var run = Run();

        await gate.BeforeTaskNodeAsync(run, "implement", CancellationToken.None);
        await gate.BeforeTaskNodeAsync(run, "test", CancellationToken.None);

        records.Appended.Should().BeEmpty();
        sandbox.Reads.Should().Be(1);
    }

    /// <summary>
    ///     Two nodes of one run passing the gate at once read the sandbox once: the claim is atomic. Red: check the
    ///     ledger with a lookup and mark the run only after the read; the second caller then reads too, and its read
    ///     waits on the release, so the bounded wait throws <see cref="TimeoutException"/>.
    /// </summary>
    [Fact]
    public async Task Two_nodes_of_a_run_passing_the_gate_at_once_read_the_restore_once()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sandbox = new FakeSandboxReadiness(FailedRestore, release.Task);
        var records = new FakeRecords();
        var gate = Gate(workspaceExists: true, Ready(), sandbox.ReadAsync, records: records);
        var run = Run();

        async Task<Result> PassAsync(string node) => await gate.BeforeTaskNodeAsync(run, node, CancellationToken.None);
        var first = PassAsync("implement");

        // The first node holds the claim and is still reading, so the second must pass without reading at all. A second
        // read would wait on the release too, so the bound turns that into a failure instead of a hang.
        var second = await PassAsync("test").WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        await first;

        second.IsSuccess.Should().BeTrue();
        sandbox.Reads.Should().Be(1);
        records.Appended.Should().ContainSingle();
    }

    /// <summary>
    ///     A read that fails observed nothing, so the next node reads again, and records the failure then. Red: keep the
    ///     claim when the read fails; the second node then reads nothing and nothing is recorded.
    /// </summary>
    [Fact]
    public async Task A_failed_read_is_tried_again_at_the_next_node()
    {
        var records = new FakeRecords();
        var sandbox = new FakeSandboxReadiness(FailedRestore) { FailFirst = true };
        var gate = Gate(workspaceExists: true, Ready(), sandbox.ReadAsync, records: records);
        var run = Run();

        (await gate.BeforeTaskNodeAsync(run, "implement", CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await gate.BeforeTaskNodeAsync(run, "test", CancellationToken.None)).IsSuccess.Should().BeTrue();

        sandbox.Reads.Should().Be(2);
        records.Appended.Should().ContainSingle().Which.Node.Should().Be("test");
    }

    /// <summary>
    ///     A record store that cannot append does not fail the dispatch: the run proceeds, and the next node tries the
    ///     record again. Red for proceeding: let the store's exception escape the gate. Red for the retry: keep the claim
    ///     when the append throws; the second node then never reads.
    /// </summary>
    [Fact]
    public async Task A_record_that_cannot_be_written_does_not_stop_the_run_and_is_tried_again()
    {
        var records = new FakeRecords { ThrowOnFirstAppend = true };
        var sandbox = new FakeSandboxReadiness(FailedRestore);
        var gate = Gate(workspaceExists: true, Ready(), sandbox.ReadAsync, records: records);
        var run = Run();

        (await gate.BeforeTaskNodeAsync(run, "implement", CancellationToken.None)).IsSuccess.Should().BeTrue();
        (await gate.BeforeTaskNodeAsync(run, "test", CancellationToken.None)).IsSuccess.Should().BeTrue();

        sandbox.Reads.Should().Be(2);
        records.Appended.Should().ContainSingle().Which.Node.Should().Be("test");
    }

    /// <summary>
    ///     The record is one per run even when the in-memory claim is gone, as after a host restart or a park that
    ///     replaced the run's sandbox: the store already holds it. Red: skip the store's check; a second record is
    ///     appended.
    /// </summary>
    [Fact]
    public async Task A_restore_already_recorded_for_the_run_is_not_recorded_again()
    {
        var records = new FakeRecords { ListsAppended = true };
        var sandbox = new FakeSandboxReadiness(FailedRestore);
        var run = Run();
        await Gate(workspaceExists: true, Ready(), sandbox.ReadAsync, records: records).BeforeTaskNodeAsync(run, "implement", CancellationToken.None);

        var restarted = Gate(workspaceExists: true, Ready(), sandbox.ReadAsync, records: records);
        await restarted.BeforeTaskNodeAsync(run, "test", CancellationToken.None);

        sandbox.Reads.Should().Be(2, "a fresh ledger reads again");
        records.Appended.Should().ContainSingle();
    }

    /// <summary>
    ///     The ledger forgets a run once its sandbox is removed, as the sweeper removes a finished run's and a park
    ///     deletes a parked run's, so a long-lived host does not keep an entry per run forever. Observed through the
    ///     gate: after the removal, the next node reads again. Red: make <see cref="SandboxRestoreLedger.OnRemovingAsync"/>
    ///     do nothing; the read count then stays at one.
    /// </summary>
    [Fact]
    public async Task A_run_is_forgotten_once_its_sandbox_is_removed()
    {
        var ledger = new SandboxRestoreLedger();
        var sandbox = new FakeSandboxReadiness(new SandboxReadiness(true, "ok", null, "ready", null));
        var gate = Gate(workspaceExists: true, Ready(), sandbox.ReadAsync, ledger: ledger);
        var run = Run();
        await gate.BeforeTaskNodeAsync(run, "implement", CancellationToken.None);

        await ledger.OnRemovingAsync(Workspace(run.Id), CancellationToken.None);
        await ledger.OnRemovingAsync(Workspace(run.Id), CancellationToken.None); // a repeat is a no-op
        await gate.BeforeTaskNodeAsync(run, "test", CancellationToken.None);

        sandbox.Reads.Should().Be(2);
    }

    private static readonly SandboxReadiness FailedRestore =
        new(true, "failed", "dotnet restore exited 1\nNU1101: no package", "ready", "roslyn detail");

    private static IRunToolServerReadiness Ready()
    {
        var readiness = Substitute.For<IRunToolServerReadiness>();
        readiness.WaitAllReadyAsync(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Success()));
        return readiness;
    }

    private static RunWorkspace Workspace(Guid runId) =>
        new(runId, "sandbox", "https://example.invalid/r.git", "main", "b", "/w", "/w/S.sln");

    private static RunToolServersReadyGate Gate(
        bool workspaceExists,
        IRunToolServerReadiness? readiness,
        Func<Guid, CancellationToken, ValueTask<Result<SandboxReadiness, AgentError>>>? sandboxReadiness = null,
        string grantProcess = "manufacture",
        IWorkflowRunRecordStore? records = null,
        SandboxRestoreLedger? ledger = null)
    {
        var workspaces = Substitute.For<IRunWorkspaceProvider>();
        workspaces.FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<RunWorkspace?>(workspaceExists
                ? new RunWorkspace(call.Arg<Guid>(), "sandbox", "https://example.invalid/r.git", "main", "b", "/w", "/w/S.sln")
                : null));
        var config = new WorkflowConfig { RoslynReadyTimeout = ReadyTimeout };
        var grant = new WriteGrantConfig { Process = grantProcess, Node = "implement", AllowedExtensions = [".cs"] };
        config.WriteGrants.Add(grant);
        return new RunToolServersReadyGate(
            workspaces, config, readiness, sandboxReadiness, ledger ?? new SandboxRestoreLedger(), RecordStoreScopes.For(records),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero)), NullLogger<RunToolServersReadyGate>.Instance);
    }

    /// <summary>
    ///     The sandbox's readiness, counted; the first read can fail, and every read can wait on <c>release</c> first.
    /// </summary>
    private sealed class FakeSandboxReadiness(SandboxReadiness answer, Task? release = null)
    {
        private int _reads;

        public int Reads => _reads;

        public Guid? LastRunId { get; private set; }

        public bool FailFirst { get; init; }

        public async ValueTask<Result<SandboxReadiness, AgentError>> ReadAsync(Guid runId, CancellationToken ct)
        {
            var read = Interlocked.Increment(ref _reads);
            LastRunId = runId;
            if (release is not null)
            {
                await release.WaitAsync(ct);
            }

            return FailFirst && read == 1
                ? Result<SandboxReadiness, AgentError>.Failure(AgentError.ProviderError("The run's sandbox could not be found."))
                : Result<SandboxReadiness, AgentError>.Success(answer);
        }
    }

    /// <summary>
    ///     An in-memory record store. It lists what was appended only when <see cref="ListsAppended"/> is set; otherwise
    ///     it lists nothing, so a test sees the gate's own once-per-run claim alone.
    /// </summary>
    private sealed class FakeRecords : IWorkflowRunRecordStore
    {
        private readonly List<WorkflowRunRecord> _appended = [];

        public IReadOnlyList<WorkflowRunRecord> Appended => _appended;

        public bool ThrowOnFirstAppend { get; init; }

        public bool ListsAppended { get; init; }

        private bool _thrown;

        public ValueTask AppendAsync(WorkflowRunRecord record, CancellationToken ct)
        {
            if (ThrowOnFirstAppend && !_thrown)
            {
                _thrown = true;
                throw new InvalidOperationException("the database is unreachable");
            }

            _appended.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<WorkflowRunRecord>> ListAsync(Guid runId, string? kind, CancellationToken ct) =>
            new(ListsAppended
                ? [.. _appended.Where(r => r.RunId == runId && (kind is null || string.Equals(r.Kind, kind, StringComparison.Ordinal)))]
                : []);
    }

    private static WorkflowRun Run() => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 1,
        CurrentNode = "implement",
        CurrentSeq = 1,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
    };
}
