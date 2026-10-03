using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Fix round 1 of task B5. <see cref="WorkflowRunGateway"/>'s public
///     <see cref="WorkflowRunGateway.ResumeAsync(Guid,string,string?,bool,RunPrincipal,CancellationToken)"/> overload must not
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
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For());

        var act = async () => await gateway.ResumeAsync(
            Guid.NewGuid(), Signal, null, applyStandingInstructions: true, Approver, CancellationToken.None);

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

        var writer = new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, Workspaces(run, dir));
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), writer);

        var result = await gateway.ResumeAsync(run.Id, Signal, null, applyStandingInstructions: true, Approver, CancellationToken.None);

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

        var writer = new StandingInstructionsWriter(new WorkflowConfig { StandingInstructionsPath = "AGENT.md" }, Workspaces(run, dir));
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For(), writer);

        await gateway.ResumeAsync(run.Id, Signal, null, applyStandingInstructions: true, Approver, CancellationToken.None);

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

        var result = await new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For()).ResumeAsync(
            run.Id, Signal, null, applyStandingInstructions: false, Approver, CancellationToken.None);

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
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For());

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
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For());

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
        var gateway = new WorkflowRunGateway(store, Substitute.For<IWorkflowRunHistory>(), RecordStoreScopes.For());

        var result = await gateway.RetryAsync(run.Id, Approver, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("run changed since it was read");
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
