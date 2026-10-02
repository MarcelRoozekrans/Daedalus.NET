using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Thalos;
using Thalos.Mcp;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Results;

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

    private static RunToolServersReadyGate Gate(bool workspaceExists, IRunToolServerReadiness? readiness, string grantProcess = "manufacture")
    {
        var workspaces = Substitute.For<IRunWorkspaceProvider>();
        workspaces.FindAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<RunWorkspace?>(workspaceExists
                ? new RunWorkspace(call.Arg<Guid>(), "sandbox", "https://example.invalid/r.git", "main", "b", "/w", "/w/S.sln")
                : null));
        var config = new WorkflowConfig { RoslynReadyTimeout = ReadyTimeout };
        var grant = new WriteGrantConfig { Process = grantProcess, Node = "implement", AllowedExtensions = [".cs"] };
        config.WriteGrants.Add(grant);
        return new RunToolServersReadyGate(workspaces, config, readiness);
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
