using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Thalos.Workflow;
using Thalos.Workspaces;

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

        var caller1 = new WorkflowCaller(run1, grant: null);
        var caller2 = new WorkflowCaller(run2, grant: null);

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
        var caller = new WorkflowCaller(NewRun("manufacture", Guid.NewGuid()), grant: null);

        // Falsifiable: PinMemoriesToAgent => false is the exact reverting edit task-B3-brief.md names for the
        // cross-role leak (memory tests cover the resulting behaviour end to end); this assertion catches the
        // property itself regressing even before any behavioural test would notice.
        caller.PinMemoriesToAgent.Should().BeTrue();
    }

    /// <summary>
    ///     The three claims come from the run row: its id routes the workspace and run-scoped MCP calls, and an
    ///     ungranted caller holds exactly <c>{workflow}</c> and no write-extension claim (ruling R29).
    /// </summary>
    [Fact]
    public void An_ungranted_caller_carries_the_run_claims_and_only_the_workflow_role()
    {
        var run = NewRun("manufacture", Guid.NewGuid()) with
        {
            CurrentNode = "review",
            StartedBy = new RunPrincipal("u-admin", ["admin"]),
        };

        var caller = new WorkflowCaller(run, grant: null);

        // Red: dropping the run-id claim, or taking it from anywhere but run.Id.
        caller.Claims.Should().ContainKey(RunWorkspaceClaims.RunId).WhoseValue.Should().Be(run.Id.ToString());
        // Red: dropping the node claim, or reading it from a node other than the run's current one.
        caller.Claims.Should().ContainKey("node").WhoseValue.Should().Be("review");
        // Red: dropping the starter claim.
        caller.Claims.Should().ContainKey("started_by").WhoseValue.Should().Be("u-admin");
        // Red: granting workspace-writer unconditionally.
        caller.Roles.Should().BeEquivalentTo(["workflow"]);
        // Red: emitting the claim as "" for an ungranted caller, which is a present grant of zero extensions.
        caller.Claims.Should().NotContainKey(RunWorkspaceClaims.WriteExtensions);
    }

    [Fact]
    public void A_granted_caller_holds_workspace_writer_and_the_lower_cased_extensions()
    {
        var grant = new WriteGrantConfig { Process = "manufacture", Node = "implement" };
        grant.AllowedExtensions.Add(".CS");
        grant.AllowedExtensions.Add(".md");

        var caller = new WorkflowCaller(NewRun("manufacture", Guid.NewGuid()), grant);

        // Red: dropping the claim, or not lower-casing it.
        caller.Claims.Should().ContainKey(RunWorkspaceClaims.WriteExtensions).WhoseValue.Should().Be(".cs;.md");
        // Red: not adding workspace-writer for a granted caller.
        caller.Roles.Should().BeEquivalentTo(["workflow", "workspace-writer"]);
    }

    /// <summary>
    ///     Config validation admits only a dot followed by ASCII letters and digits, so any other entry can only come from
    ///     a grant built around it. It is left out of the claim, narrowing the grant, never widening it.
    /// </summary>
    /// <remarks>
    ///     Red for the non-ASCII row: drop the filter; the entry reaches <c>Ascii.ToLower</c>, which stops at the first
    ///     non-ASCII character and leaves the rest unwritten. Red for the <c>';'</c> row: filter with
    ///     <c>Ascii.IsValid</c> instead of the config pattern; the claim becomes <c>.cs;.cs;.props</c>, which the tools
    ///     parse as a grant of <c>.props</c> too.
    /// </remarks>
    [Theory]
    [InlineData(".\u00C7s")]
    [InlineData(".cs;.props")]
    public void An_entry_the_config_pattern_rejects_is_left_out_of_the_claim(string entry)
    {
        var grant = new WriteGrantConfig { Process = "manufacture", Node = "implement" };
        grant.AllowedExtensions.Add(".CS");
        grant.AllowedExtensions.Add(entry);

        var caller = new WorkflowCaller(NewRun("manufacture", Guid.NewGuid()), grant);

        caller.Claims.Should().ContainKey(RunWorkspaceClaims.WriteExtensions).WhoseValue.Should().Be(".cs");
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
