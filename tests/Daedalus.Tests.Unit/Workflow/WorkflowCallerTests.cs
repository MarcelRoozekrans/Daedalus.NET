using Daedalus.Agents.Workflow;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Task B3's guard against "the tempting wrong fix": <see cref="WorkflowCaller.Id"/> (the authorization
///     identity <c>Thalos.Tools.DefaultToolAuthorizer</c> evaluates) must keep changing every run, while
///     <see cref="WorkflowCaller.MemoryOwnerId"/> (what <c>Thalos.Memory.MemoryOwnerResolver.Resolve</c> reads
///     instead) must not — a memory owner that changed every run would never be readable again past the run that
///     wrote it. Collapsing the two onto one string — e.g. dropping the run id from <c>Id</c> itself instead of
///     adding a second property — would make every assertion here pass except the one on <see cref="WorkflowCaller.Id"/>
///     differing, which is exactly why that assertion is here rather than trusting <see cref="WorkflowCaller.MemoryOwnerId"/>
///     alone.
/// </summary>
public sealed class WorkflowCallerTests
{
    [Fact]
    public void Id_differs_between_two_runs_of_the_same_process_while_MemoryOwnerId_is_identical()
    {
        var run1 = NewRun("manufacture", Guid.NewGuid());
        var run2 = NewRun("manufacture", Guid.NewGuid());

        var caller1 = new WorkflowCaller(run1);
        var caller2 = new WorkflowCaller(run2);

        // Falsifiable via the run id: NewRun("manufacture", sameId) for both callers would collapse this to a
        // false positive, which is why run1 and run2 are given distinct ids above.
        caller1.Id.Should().NotBe(caller2.Id,
            "Id is what DefaultToolAuthorizer evaluates and what makes a run auditable; it must stay per-run");

        caller1.MemoryOwnerId.Should().Be(caller2.MemoryOwnerId,
            "the memory owner must survive past the run that wrote a memory, or nothing is ever readable again");
        caller1.MemoryOwnerId.Should().Be("workflow:manufacture");
    }

    [Fact]
    public void PinMemoriesToAgent_is_true()
    {
        var caller = new WorkflowCaller(NewRun("manufacture", Guid.NewGuid()));

        // Falsifiable: PinMemoriesToAgent => false is the exact reverting edit task-B3-brief.md names for the
        // cross-role leak (memory tests cover the resulting behaviour end to end); this assertion catches the
        // property itself regressing even before any behavioural test would notice.
        caller.PinMemoriesToAgent.Should().BeTrue();
    }

    private static WorkflowRun NewRun(string process, Guid runId) => new()
    {
        Id = runId,
        Process = process,
        ProcessVersion = 1,
        CurrentNode = "start",
        CurrentSeq = 0,
        Status = WorkflowStatus.Running,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
    };
}
