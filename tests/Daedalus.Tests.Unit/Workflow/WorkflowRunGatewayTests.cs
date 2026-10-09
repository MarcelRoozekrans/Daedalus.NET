using System.Text.Json;
using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Fix round 1 of task B5. <see cref="WorkflowRunGateway"/>'s public
///     <see cref="WorkflowRunGateway.ResumeAsync(Guid,string,string?,bool,IReadOnlyList{string},RunPrincipal,CancellationToken)"/> overload must not
///     let a <see cref="WorkflowConcurrencyException"/> thrown by the underlying store's own resume propagate as
///     an unhandled exception once <see cref="StandingInstructionsWriter.ApplyAsync"/> has already written the
///     file — see that method's own remarks on why the write is not, and cannot cheaply be, rolled back.
/// </summary>
public sealed class WorkflowRunGatewayTests
{
    private const string Signal = "human_approval";

    private static readonly RunPrincipal Approver = new("u-admin", ["admin"]) { DisplayName = "admin" };

    /// <summary>
    ///     The gateway's misconfiguration guard, deferred from task B5. A gateway built without a writer must refuse
    ///     an apply loudly, naming the missing writer. Without the guard the call would dereference a null writer
    ///     instead.
    /// </summary>
    [Fact]
    public async Task An_apply_on_a_gateway_built_without_a_writer_fails_loudly()
    {
        var store = Substitute.For<IWorkflowStore>();
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);

        var act = async () => await gateway.ResumeAsync(
            Guid.NewGuid(), Signal, null, applyStandingInstructions: true, null, Approver, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*without a StandingInstructionsWriter*");
    }

    /// <summary>
    ///     Falsifiable: removing the <c>catch (WorkflowConcurrencyException)</c> block from the public
    ///     <c>ResumeAsync</c> turns this red — the exception would propagate out of <c>ApplyAsync</c>'s caller
    ///     unhandled, and <c>await gateway.ResumeAsync(...)</c> would itself throw instead of returning a failed
    ///     <c>UnitResult</c>.
    /// </summary>
    [Fact]
    public async Task A_concurrency_conflict_after_a_successful_write_is_reported_not_thrown()
    {
        using var dir = new TempDirectory();
        var run = RunWith(pinned: "", proposal: "New instructions.");

        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        store.ResumeAsync(run.Id, Arg.Is<WorkflowResumeRequest>(r => r.Signal == Signal), Arg.Any<CancellationToken>())
            .Returns<Result>(_ => throw new WorkflowConcurrencyException("another writer already resumed this run"));

        var writer = new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, Workspaces(run, dir), NullLogger<StandingInstructionsWriter>.Instance);
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance, writer);

        var result = await gateway.ResumeAsync(run.Id, Signal, null, applyStandingInstructions: true, null, Approver, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ResumeRefusal.EngineRefused);
        result.Error.Detail.Should().Contain("already written",
            "an operator reading this failure must know the file was written even though the resume itself failed");
    }

    /// <summary>
    ///     Pairs with the test above: the write really did land, and stays landed — the exception this test throws
    ///     happens strictly after <c>ApplyAsync</c> already returned success, so nothing rolls it back. Falsifiable:
    ///     this only fails if a future change makes the write conditional on the engine resume also succeeding
    ///     (e.g. by moving the write after the engine call) — this test and
    ///     <c>WorkflowRunsController.ResumeBoundaryTests</c>'s ordering tests together pin the write-first order.
    /// </summary>
    [Fact]
    public async Task The_write_is_not_rolled_back_when_the_engine_resume_then_loses_the_race()
    {
        using var dir = new TempDirectory();
        var run = RunWith(pinned: "", proposal: "New instructions.");

        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        store.ResumeAsync(run.Id, Arg.Is<WorkflowResumeRequest>(r => r.Signal == Signal), Arg.Any<CancellationToken>())
            .Returns<Result>(_ => throw new WorkflowConcurrencyException("another writer already resumed this run"));

        var writer = new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, Workspaces(run, dir), NullLogger<StandingInstructionsWriter>.Instance);
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance, writer);

        await gateway.ResumeAsync(run.Id, Signal, null, applyStandingInstructions: true, null, Approver, CancellationToken.None);

        // Task B11: checked on its own first, so a write that never reached the run's worktree fails this assertion
        // rather than throwing from the read below.
        File.Exists(dir.Path("AGENT.md")).Should().BeTrue("the write goes to the worktree the provider reports for the run");
        (await File.ReadAllTextAsync(dir.Path("AGENT.md"))).Should().Be("New instructions.");
    }

    /// <summary>
    ///     The approver the controller supplies is the one the store records. Falsifiable: building the
    ///     <see cref="WorkflowResumeRequest"/> in the gateway from a constant principal instead of the one passed in
    ///     turns the <c>Received</c> check red.
    /// </summary>
    [Fact]
    public async Task The_approver_reaches_the_store_with_the_resume()
    {
        // The flag stays off, so the standing-instructions writer is never reached.
        var run = RunWith(pinned: "", proposal: "New instructions.");
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        store.ResumeAsync(run.Id, Arg.Any<WorkflowResumeRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result>(Result.Success()));

        var result = await new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance).ResumeAsync(
            run.Id, Signal, null, applyStandingInstructions: false, null, Approver, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await store.Received(1).ResumeAsync(
            run.Id,
            Arg.Is<WorkflowResumeRequest>(r => r.Signal == Signal && r.ResumedBy == Approver),
            Arg.Any<CancellationToken>());
    }

    /// <summary>A handoff whose only worktree is <paramref name="dir"/>, handed off for <paramref name="run"/>'s id.</summary>
    private static IRunWorkspaceHandoff Workspaces(WorkflowRun run, TempDirectory dir)
    {
        var handoff = Substitute.For<IRunWorkspaceHandoff>();
        handoff.CheckoutForPublishAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
            Result<RunWorkspace, AgentError>.Success(new RunWorkspace(run.Id, "sandbox", "unused", "main", $"manufacture/{run.Id}", dir.Root, null))));
        return handoff;
    }

    /// <summary>
    ///     Falsifiable: calling the store before the lookup turns this red, because the store would then receive
    ///     <c>RetryFailedNodeAsync</c> for a run that does not exist.
    /// </summary>
    [Fact]
    public async Task A_retry_of_a_run_that_does_not_exist_fails_without_calling_the_store()
    {
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>((WorkflowRun?)null));
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);

        var result = await gateway.RetryAsync(Guid.NewGuid(), Approver, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Contain("was not found");
        await store.DidNotReceiveWithAnyArgs().RetryFailedNodeAsync(Guid.Empty, default!, default);
    }

    /// <summary>
    ///     Falsifiable: passing a fixed or default seq, or a different principal, turns this red, because the
    ///     predicate pins the seq the run was read at and the identity of the principal. The store's result is
    ///     returned unchanged.
    /// </summary>
    [Fact]
    public async Task A_retry_passes_the_seq_the_run_was_read_at_and_the_caller_to_the_store()
    {
        var run = FailedRunAtSeq(7);
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        store.RetryFailedNodeAsync(run.Id, Arg.Any<WorkflowRetryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result>(Result.Success()));
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);

        var result = await gateway.RetryAsync(run.Id, Approver, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await store.Received(1).RetryFailedNodeAsync(
            run.Id,
            Arg.Is<WorkflowRetryRequest>(r => r.ExpectedSeq == 7 && ReferenceEquals(r.RetriedBy, Approver)),
            Arg.Any<CancellationToken>());
    }

    /// <summary>Falsifiable: swallowing the store's error turns this red.</summary>
    [Fact]
    public async Task A_store_refusal_of_a_retry_is_returned_with_its_message()
    {
        var run = FailedRunAtSeq(7);
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        store.RetryFailedNodeAsync(run.Id, Arg.Any<WorkflowRetryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Result>(Result.Failure("run changed since it was read")));
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);

        var result = await gateway.RetryAsync(run.Id, Approver, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("run changed since it was read");
    }

    private static readonly Guid GateRunId = Guid.NewGuid();

    private static WorkflowRun AtGate() => new()
    {
        Id = GateRunId,
        Process = "manufacture",
        ProcessVersion = 9,
        CurrentNode = "gate",
        CurrentSeq = 12,
        Status = WorkflowStatus.Awaiting,
        AwaitingSignal = Signal,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
    };

    private static WorkflowRunRecord ApprovedWithDeferred() =>
        WorkflowRunRecord.Create(GateRunId, 9, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null,
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked"}] }""",
            DateTime.UtcNow).Value;

    private static (WorkflowRunGateway Gateway, IWorkflowStore Store, IWorkflowRunRecordStore Records) GateWith(params WorkflowRunRecord[] evidence)
    {
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(GateRunId, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(AtGate()));
        store.ResumeAsync(GateRunId, Arg.Any<WorkflowResumeRequest>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<Result>(Result.Success()));
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(GateRunId, WorkflowRunRecord.ReviewEvidenceKind, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunRecord>>(evidence));
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(records), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);
        return (gateway, store, records);
    }

    /// <summary>A typo must never file a finding. Red: skip the unknown-id check; the resume then succeeds.</summary>
    [Fact]
    public async Task Dropping_an_unknown_finding_is_refused_and_the_run_is_not_resumed()
    {
        var (gateway, store, records) = GateWith(ApprovedWithDeferred());

        var result = await gateway.ResumeAsync(GateRunId, Signal, null, false, ["correctness-9"], Approver, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(ResumeRefusal.UnknownFinding);
        result.Error.Detail.Should().Contain("correctness-9").And.Contain("correctness-1");
        await store.DidNotReceiveWithAnyArgs().ResumeAsync(Guid.Empty, default!, default);
        await records.DidNotReceiveWithAnyArgs().AppendAsync(default!, default);
    }

    /// <summary>
    ///     Explicitly dropped, recorded with who dropped it. Red: skip the append; the InOrder assertion fails. Red: use
    ///     the run's starter as principal; the PrincipalId assertion fails. Red: move the append after the engine resume;
    ///     the InOrder assertion fails.
    /// </summary>
    [Fact]
    public async Task A_drop_is_recorded_with_the_resumer_before_the_run_resumes()
    {
        var (gateway, store, records) = GateWith(ApprovedWithDeferred());

        var result = await gateway.ResumeAsync(GateRunId, Signal, null, false, ["correctness-1"], Approver, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        Received.InOrder(() =>
        {
            _ = records.AppendAsync(
                Arg.Is<WorkflowRunRecord>(r => r.Kind == WorkflowRunRecord.FindingsDroppedKind && r.PrincipalId == Approver.Id
                                               && r.PayloadJson.Contains("correctness-1")),
                Arg.Any<CancellationToken>());
            _ = store.ResumeAsync(GateRunId, Arg.Any<WorkflowResumeRequest>(), Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    ///     A run with deferred findings always gets a record, even an empty one, so a failed earlier resume's drop list
    ///     can never apply to a later one. Red: append only when the list is non-empty. Red: record the unknown-checked list
    ///     as the deferred ids; the empty-list assertion fails.
    /// </summary>
    [Fact]
    public async Task A_resume_that_drops_nothing_still_records_that_it_dropped_nothing()
    {
        var (gateway, _, records) = GateWith(ApprovedWithDeferred());

        await gateway.ResumeAsync(GateRunId, Signal, null, false, null, Approver, CancellationToken.None);

        await records.Received(1).AppendAsync(
            Arg.Is<WorkflowRunRecord>(r => r.Kind == WorkflowRunRecord.FindingsDroppedKind
                                           && r.PayloadJson.Contains("\"dropped\":[]", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    private static (WorkflowRunGateway Gateway, IWorkflowStore Store, List<WorkflowRunRecord> Appended, CapturingLogger Log) LosingGate(
        bool throwConcurrency, bool voidAppendFails = false)
    {
        var (_, store, records) = GateWith(ApprovedWithDeferred());
        if (throwConcurrency)
        {
            store.ResumeAsync(GateRunId, Arg.Any<WorkflowResumeRequest>(), Arg.Any<CancellationToken>())
                .Returns<Result>(_ => throw new WorkflowConcurrencyException("another writer already resumed this run"));
        }
        else
        {
            store.ResumeAsync(GateRunId, Arg.Any<WorkflowResumeRequest>(), Arg.Any<CancellationToken>())
                .Returns(new ValueTask<Result>(Result.Failure("lost the race")));
        }

        var appended = new List<WorkflowRunRecord>();
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var record = call.Arg<WorkflowRunRecord>();
            if (voidAppendFails && string.Equals(record.Kind, WorkflowRunRecord.FindingsDropVoidedKind, StringComparison.Ordinal))
                throw new InvalidOperationException("database down");

            appended.Add(record);
            return ValueTask.CompletedTask;
        });
        var log = new CapturingLogger();
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(records), TimeProvider.System, log);
        return (gateway, store, appended, log);
    }

    private static string AttemptOf(WorkflowRunRecord record) =>
        JsonDocument.Parse(record.PayloadJson).RootElement.GetProperty("attempt").GetString()!;

    /// <summary>
    ///     A resume that loses the engine race must void the drop it recorded, or its list stays latest over the winner's.
    ///     Red: skip the <c>VoidDroppedAsync</c> call after a failed result; no void record exists.
    /// </summary>
    [Fact]
    public async Task A_resume_the_engine_refuses_voids_the_drop_it_recorded()
    {
        var (gateway, _, appended, _) = LosingGate(throwConcurrency: false);

        var result = await gateway.ResumeAsync(GateRunId, Signal, null, false, ["correctness-1"], Approver, CancellationToken.None);

        result.Error.Kind.Should().Be(ResumeRefusal.EngineRefused);
        var drop = appended.Should().ContainSingle(r => r.Kind == WorkflowRunRecord.FindingsDroppedKind).Subject;
        var voided = appended.Should().ContainSingle(r => r.Kind == WorkflowRunRecord.FindingsDropVoidedKind).Subject;
        AttemptOf(voided).Should().Be(AttemptOf(drop));
        FindingRecords.ReadDropped(appended).Value.Should().Be(DroppedFindings.None);
    }

    /// <summary>Red: rethrow from the <c>WorkflowConcurrencyException</c> catch, or skip the void on a failed outcome.</summary>
    [Fact]
    public async Task A_resume_that_loses_a_concurrency_race_voids_the_drop_it_recorded()
    {
        var (gateway, _, appended, _) = LosingGate(throwConcurrency: true);

        var result = await gateway.ResumeAsync(GateRunId, Signal, null, false, ["correctness-1"], Approver, CancellationToken.None);

        result.Error.Kind.Should().Be(ResumeRefusal.EngineRefused);
        var drop = appended.Should().ContainSingle(r => r.Kind == WorkflowRunRecord.FindingsDroppedKind).Subject;
        AttemptOf(appended.Should().ContainSingle(r => r.Kind == WorkflowRunRecord.FindingsDropVoidedKind).Subject).Should().Be(AttemptOf(drop));
    }

    /// <summary>
    ///     A void that cannot be written is logged and the engine's own failure still comes back. Red: let the void
    ///     append's exception escape; the call throws. Red: drop the log call; the logger sees no error.
    /// </summary>
    [Fact]
    public async Task A_void_that_cannot_be_written_is_logged_and_the_engine_failure_is_returned()
    {
        var (gateway, _, _, log) = LosingGate(throwConcurrency: false, voidAppendFails: true);

        var result = await gateway.ResumeAsync(GateRunId, Signal, null, false, ["correctness-1"], Approver, CancellationToken.None);

        result.Error.Kind.Should().Be(ResumeRefusal.EngineRefused);
        result.Error.Detail.Should().Contain("lost the race");
        log.Levels.Should().Contain(LogLevel.Error);
    }

    /// <summary>Red: drop the log call in <c>RecordDroppedAsync</c>'s catch; the logger sees no error.</summary>
    [Fact]
    public async Task A_drop_that_cannot_be_recorded_is_logged_and_refuses_the_resume()
    {
        var (_, store, records) = GateWith(ApprovedWithDeferred());
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>()).Returns<ValueTask>(_ => throw new InvalidOperationException("database down"));
        var log = new CapturingLogger();
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(records), TimeProvider.System, log);

        var result = await gateway.ResumeAsync(GateRunId, Signal, null, false, ["correctness-1"], Approver, CancellationToken.None);

        result.Error.Kind.Should().Be(ResumeRefusal.RecordFailed);
        log.Levels.Should().Contain(LogLevel.Error);
        await store.DidNotReceiveWithAnyArgs().ResumeAsync(Guid.Empty, default!, default);
    }

    /// <summary>
    ///     A typo must not write the standing-instructions file either. Red: move the drop check below the apply; the
    ///     file exists.
    /// </summary>
    [Fact]
    public async Task An_unknown_finding_with_apply_standing_instructions_writes_no_file()
    {
        using var dir = new TempDirectory();
        var run = RunWith(pinned: "", proposal: "New instructions.");
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        var evidence = WorkflowRunRecord.Create(run.Id, 1, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null,
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked"}] }""",
            DateTime.UtcNow).Value;
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(run.Id, WorkflowRunRecord.ReviewEvidenceKind, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([evidence]));
        var writer = new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, Workspaces(run, dir), NullLogger<StandingInstructionsWriter>.Instance);
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(records), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance, writer);

        var result = await gateway.ResumeAsync(run.Id, Signal, null, applyStandingInstructions: true, ["nope-1"], Approver, CancellationToken.None);

        result.Error.Kind.Should().Be(ResumeRefusal.UnknownFinding);
        File.Exists(dir.Path("AGENT.md")).Should().BeFalse("a refused resume writes nothing");
    }

    /// <summary>
    ///     A resume whose standing-instructions apply refuses never reaches the engine, and its drop list must not stay
    ///     latest over one that took effect. Red: skip the void after a failed apply; no void record exists.
    /// </summary>
    [Fact]
    public async Task A_refused_standing_instructions_apply_voids_the_drop_it_recorded()
    {
        var run = RunWith(pinned: "", proposal: "New instructions.");
        var store = Substitute.For<IWorkflowStore>();
        store.FindAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<WorkflowRun?>(run));
        var evidence = WorkflowRunRecord.Create(run.Id, 1, "review", WorkflowRunRecord.ReviewEvidenceKind, "p", null,
            """{ "lens": "correctness", "verdict": "approved", "checked": ["x"], "findings": [], "deferred": [{"file":"a.cs","line":1,"title":"t","scenario":"s","reason":"blocked"}] }""",
            DateTime.UtcNow).Value;
        var appended = new List<WorkflowRunRecord>();
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.ListAsync(run.Id, WorkflowRunRecord.ReviewEvidenceKind, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunRecord>>([evidence]));
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            appended.Add(call.Arg<WorkflowRunRecord>());
            return ValueTask.CompletedTask;
        });
        var handoff = Substitute.For<IRunWorkspaceHandoff>();
        handoff.CheckoutForPublishAsync(run.Id, Arg.Any<CancellationToken>()).Returns(new ValueTask<Result<RunWorkspace, AgentError>>(
            Result<RunWorkspace, AgentError>.Failure(AgentError.Validation("the change touches protected path '.github/workflows/ci.yml'; publish refused"))));
        var writer = new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, handoff, NullLogger<StandingInstructionsWriter>.Instance);
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(records), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance, writer);

        var result = await gateway.ResumeAsync(run.Id, Signal, null, applyStandingInstructions: true, ["correctness-1"], Approver, CancellationToken.None);

        result.Error.Kind.Should().Be(ResumeRefusal.PublishRefused);
        var drop = appended.Should().ContainSingle(r => r.Kind == WorkflowRunRecord.FindingsDroppedKind).Subject;
        AttemptOf(appended.Should().ContainSingle(r => r.Kind == WorkflowRunRecord.FindingsDropVoidedKind).Subject).Should().Be(AttemptOf(drop));
        await store.DidNotReceiveWithAnyArgs().ResumeAsync(Guid.Empty, default!, default);
    }

    /// <summary>
    ///     A client that aborts after the engine failed must not lose the void or replace the engine failure with a
    ///     cancellation. Red: append the void with the request token; the append throws and the call throws.
    /// </summary>
    [Fact]
    public async Task A_void_is_written_even_when_the_request_is_cancelled_after_the_engine_failed()
    {
        using var cts = new CancellationTokenSource();
        var (_, store, records) = GateWith(ApprovedWithDeferred());
        async ValueTask<Result> LoseTheRace()
        {
            await cts.CancelAsync();
            return Result.Failure("lost the race");
        }

        store.ResumeAsync(GateRunId, Arg.Any<WorkflowResumeRequest>(), Arg.Any<CancellationToken>()).Returns(_ => LoseTheRace());
        var appended = new List<WorkflowRunRecord>();
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            appended.Add(call.Arg<WorkflowRunRecord>());
            return ValueTask.CompletedTask;
        });
        // The drop is appended before the cancel, so only the void sees a cancelled request token.
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(records), TimeProvider.System, NullLogger<WorkflowRunGateway>.Instance);

        var result = await gateway.ResumeAsync(GateRunId, Signal, null, false, ["correctness-1"], Approver, cts.Token);

        result.Error.Kind.Should().Be(ResumeRefusal.EngineRefused);
        result.Error.Detail.Should().Contain("lost the race");
        appended.Should().ContainSingle(r => r.Kind == WorkflowRunRecord.FindingsDropVoidedKind);
    }

    /// <summary>Keeps the levels a logger saw.</summary>
    private sealed class CapturingLogger : ILogger<WorkflowRunGateway>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }

    /// <summary>Red: append even when there is nothing deferred; the DidNotReceive assertion fails.</summary>
    [Fact]
    public async Task A_run_with_no_deferred_findings_records_nothing()
    {
        var (gateway, _, records) = GateWith();

        var result = await gateway.ResumeAsync(GateRunId, Signal, null, false, null, Approver, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await records.DidNotReceiveWithAnyArgs().AppendAsync(default!, default);
    }

    private static WorkflowRun FailedRunAtSeq(long seq) => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 5,
        CurrentNode = "publish",
        CurrentSeq = seq,
        Status = WorkflowStatus.Failed,
        LastError = "git push refused",
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["publish"] = 1 },
    };

    private static WorkflowRun RunWith(string pinned, string proposal) => new()
    {
        Id = Guid.NewGuid(),
        Process = "manufacture",
        ProcessVersion = 5,
        CurrentNode = "gate",
        CurrentSeq = 1,
        Status = WorkflowStatus.Awaiting,
        AwaitingSignal = Signal,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal) { ["gate"] = 1 },
        Manifest = new RunManifest
        {
            Nodes = new Dictionary<string, NodePin>(StringComparer.Ordinal),
            Documents = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ManufactureRunStarter.StandingInstructionsDocument] = pinned,
            },
        },
        Variables = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ReviewHandoff.ProposedStandingInstructionsKey] = proposal,
        },
    };
}
