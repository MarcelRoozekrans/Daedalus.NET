using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Hosting;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Fix round 1 of task B5. <see cref="WorkflowRunGateway"/>'s five-argument
///     <see cref="WorkflowRunGateway.ResumeAsync(Guid,string,string?,bool,CancellationToken)"/> overload must not
///     let a <see cref="WorkflowConcurrencyException"/> thrown by the underlying store's own resume propagate as
///     an unhandled exception once <see cref="StandingInstructionsWriter.ApplyAsync"/> has already written the
///     file — see that method's own remarks on why the write is not, and cannot cheaply be, rolled back.
/// </summary>
public sealed class WorkflowRunGatewayTests
{
    private const string Signal = "human_approval";

    /// <summary>
    ///     Falsifiable: removing the <c>catch (WorkflowConcurrencyException)</c> block from the five-argument
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
        store.ResumeAsync(run.Id, Signal, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Result>(_ => throw new WorkflowConcurrencyException("another writer already resumed this run"));

        var writer = new StandingInstructionsWriter(
            new WorkflowConfig { StandingInstructionsPath = dir.Path("AGENT.md") }, Substitute.For<IHostEnvironment>());
        var gateway = new WorkflowRunGateway(store, writer);

        var result = await gateway.ResumeAsync(run.Id, Signal, null, applyStandingInstructions: true, CancellationToken.None);

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
        store.ResumeAsync(run.Id, Signal, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Result>(_ => throw new WorkflowConcurrencyException("another writer already resumed this run"));

        var writer = new StandingInstructionsWriter(
            new WorkflowConfig { StandingInstructionsPath = dir.Path("AGENT.md") }, Substitute.For<IHostEnvironment>());
        var gateway = new WorkflowRunGateway(store, writer);

        await gateway.ResumeAsync(run.Id, Signal, null, applyStandingInstructions: true, CancellationToken.None);

        File.ReadAllText(dir.Path("AGENT.md")).Should().Be("New instructions.");
    }

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
