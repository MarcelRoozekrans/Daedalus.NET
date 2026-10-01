using System.Collections.Concurrent;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Mcp;
using Thalos.Workflow;
using Thalos.Workspaces;
using ZeroAlloc.Authorization;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B9: the readiness gate in a real, workflow-enabled Api host. Each run is started through the registered
///     <see cref="IManufactureRunStarter"/>, so it has a real worktree, and the host's own outbox poller dispatches it to
///     <c>implement</c> through the real <see cref="WorkflowNodeDispatcher"/>, whose gates are whatever the host
///     registered. Only <see cref="IAgentRuntime"/> is replaced, by <see cref="RecordingRuntime"/>, which records each
///     node it is asked to run and then holds the turn open, so a dispatched run stays <c>Running</c> at that node.
/// </summary>
/// <remarks>
///     The scratch host's <c>.mcp.json</c> declares no run-scoped server (ruling R24), so the host registers no
///     <see cref="IRunToolServerReadiness"/> of its own. Tests 1 to 3 register a fake one through
///     <c>configureServices</c>; test 4 registers none.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class RunToolServersReadyGateTests(PostgresFixture fixture)
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     Red: a gate that returns success without awaiting readiness; implement is dispatched within the first
    ///     three seconds. Red: pass an empty gate list in <see cref="WorkflowNodeDispatcherFactory"/>.
    /// </summary>
    [Fact]
    public async Task Implement_is_not_dispatched_before_the_run_servers_are_ready()
    {
        var readiness = new ControllableReadiness();
        await using var host = await HostWith(readiness, TimeSpan.FromMinutes(1));
        var runId = await host.StartAsync();

        (await Completes(readiness.Waiting)).Should().BeTrue("the dispatch must wait on the run's servers");
        await Task.Delay(TimeSpan.FromSeconds(3));
        host.Runtime.TasksFor("implement").Should().BeEmpty("the run's servers are not ready yet");

        readiness.Release();
        await host.WaitForAsync(runId, _ => host.Runtime.TasksFor("implement").Count == 1, "dispatched implement");
    }

    /// <summary>
    ///     Red: return success from the gate whatever readiness returns; implement is dispatched and the run keeps
    ///     running. Red: pass an empty gate list in the factory.
    /// </summary>
    [Fact]
    public async Task A_timeout_fails_the_run_with_an_explicit_error()
    {
        await using var host = await HostWith(new NeverReady(), TimeSpan.FromSeconds(2));
        var runId = await host.StartAsync();

        var run = await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Failed, "failed");

        run.LastError.Should().Contain("implement").And.Contain("not ready");
        host.Runtime.TasksFor("implement").Should().BeEmpty();
    }

    /// <summary>
    ///     Ruling R9. A hold gate, registered ahead of the one under test, keeps the first dispatch back until the
    ///     worktree is gone. Red: return success when the workspace is missing, the rule before R9; implement is then
    ///     dispatched, because the fake readiness says ready. Red: pass an empty gate list in the factory.
    /// </summary>
    [Fact]
    public async Task A_granted_node_whose_run_has_no_workspace_fails_the_run_and_runs_no_turn()
    {
        var hold = new HoldGate();
        var readiness = new ControllableReadiness();
        readiness.Release();
        await using var host = await HostWith(readiness, TimeSpan.FromMinutes(1), hold);
        var runId = await host.StartAsync();

        (await Completes(hold.Reached)).Should().BeTrue("the dispatch must run the host's gates");
        (await host.Workspaces.RemoveAsync(runId, CancellationToken.None)).IsSuccess.Should().BeTrue("the worktree must be gone first");
        hold.Release();

        var run = await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Failed, "failed");
        run.LastError.Should().Contain("implement").And.Contain("no workspace");
        host.Runtime.TasksFor("implement").Should().BeEmpty();
    }

    /// <summary>
    ///     Ruling R24. Red: fail the gate when <c>readiness</c> is null; the run then fails and implement is never
    ///     dispatched, so the wait times out.
    /// </summary>
    [Fact]
    public async Task With_no_run_scoped_server_configured_implement_is_dispatched_without_waiting()
    {
        await using var host = await HostWith(readiness: null, TimeSpan.FromMinutes(1));
        host.Factory.Services.GetService<IRunToolServerReadiness>().Should().BeNull("this host declares no run-scoped server");
        var runId = await host.StartAsync();

        await host.WaitForAsync(runId, _ => host.Runtime.TasksFor("implement").Count == 1, "dispatched implement");

        (await host.Scratch.Store.FindAsync(runId, CancellationToken.None))!.Status.Should().Be(WorkflowStatus.Running);
    }

    /// <summary>Whether <paramref name="signal"/> completes within <see cref="WaitTimeout"/>, as a value to assert on.</summary>
    private static async Task<bool> Completes(TaskCompletionSource signal) =>
        await Task.WhenAny(signal.Task, Task.Delay(WaitTimeout)) == signal.Task;

    private async Task<GateHost> HostWith(IRunToolServerReadiness? readiness, TimeSpan timeout, IWorkflowDispatchGate? first = null)
    {
        var runtime = new RecordingRuntime();
        var scratch = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime,
            settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Thalos:Workflow:RoslynReadyTimeout"] = timeout.ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            },
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
                if (readiness is not null)
                {
                    services.AddSingleton(readiness);
                }

                if (first is not null)
                {
                    // Gates run in registration order, so this one runs before the host's readiness gate.
                    services.Insert(0, ServiceDescriptor.Singleton(first));
                }
            });
        return new GateHost(scratch, runtime);
    }

    private sealed class GateHost(ScratchWorkflowHost scratch, RecordingRuntime runtime) : IAsyncDisposable
    {
        public ScratchWorkflowHost Scratch => scratch;

        public ApiWebApplicationFactory Factory => scratch.Factory;

        public RecordingRuntime Runtime => runtime;

        public IRunWorkspaceProvider Workspaces => scratch.Factory.Services.GetRequiredService<IRunWorkspaceProvider>();

        public async Task<Guid> StartAsync()
        {
            var started = await scratch.Factory.Services.GetRequiredService<IManufactureRunStarter>().StartAsync(
                new ManufactureStartRequest("Tighten a guard.", ScratchWorkflowHost.Repository, new RunPrincipal("u-dev", ["developer"])),
                CancellationToken.None);
            started.IsSuccess.Should().BeTrue(started.IsFailure ? started.Error : null);
            return started.Value;
        }

        /// <summary>Polls the run until <paramref name="until"/> holds or the wait times out, naming where it stopped.</summary>
        public async Task<WorkflowRun> WaitForAsync(Guid runId, Func<WorkflowRun, bool> until, string what)
        {
            var deadline = DateTime.UtcNow + WaitTimeout;
            WorkflowRun? run;
            do
            {
                run = await scratch.Store.FindAsync(runId, CancellationToken.None);
                if (run is not null && until(run))
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(200));
            }
            while (DateTime.UtcNow < deadline);

            run.Should().NotBeNull();
            until(run!).Should().BeTrue(
                $"the run should have {what}, but is {run!.Status} at '{run.CurrentNode}', last error: {run.LastError}");
            return run;
        }

        public async ValueTask DisposeAsync()
        {
            runtime.Dispose();
            await scratch.DisposeAsync();
        }
    }

    /// <summary>Ready only once <see cref="Release"/> is called; <see cref="Waiting"/> completes on the first wait.</summary>
    private sealed class ControllableReadiness : IRunToolServerReadiness
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public async ValueTask<UnitResult<AgentError>> WaitAllReadyAsync(Guid runId, TimeSpan timeout, CancellationToken ct)
        {
            Waiting.TrySetResult();
            await _released.Task.WaitAsync(ct);
            return UnitResult<AgentError>.Success();
        }
    }

    /// <summary>Fails after the timeout, as the real registry does for a server that never reports ready.</summary>
    private sealed class NeverReady : IRunToolServerReadiness
    {
        public async ValueTask<UnitResult<AgentError>> WaitAllReadyAsync(Guid runId, TimeSpan timeout, CancellationToken ct)
        {
            await Task.Delay(timeout, ct);
            return UnitResult<AgentError>.Failure(AgentError.ProviderError($"server 'roslyn' not ready within {timeout:c}"));
        }
    }

    /// <summary>Holds the first dispatch until <see cref="Release"/>; <see cref="Reached"/> completes when it is reached.</summary>
    private sealed class HoldGate : IWorkflowDispatchGate
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _released.TrySetResult();

        public async ValueTask<Result> BeforeTaskNodeAsync(WorkflowRun run, string node, CancellationToken ct)
        {
            Reached.TrySetResult();
            await _released.Task.WaitAsync(ct);
            return Result.Success();
        }
    }

    /// <summary>
    ///     Records the node of every workflow turn it is asked to run, then holds the turn open until the host shuts
    ///     down, so a dispatched run stays <c>Running</c> at that node and no later node is reached.
    /// </summary>
    private sealed class RecordingRuntime : IAgentRuntime, IDisposable
    {
        private readonly ConcurrentQueue<string> _nodes = new();
        private readonly CancellationTokenSource _stopping = new();

        public IReadOnlyList<string> TasksFor(string node) => [.. _nodes.Where(n => string.Equals(n, node, StringComparison.Ordinal))];

        public ValueTask<Result<SessionId, AgentError>> CreateSessionAsync(AgentId agentId, ISecurityContext caller, CancellationToken ct = default) =>
            ValueTask.FromResult(Result<SessionId, AgentError>.Success(new SessionId(Guid.NewGuid())));

        public ValueTask<UnitResult<AgentError>> CloseSessionAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct = default) =>
            ValueTask.FromResult(UnitResult<AgentError>.Success());

        public IAsyncEnumerable<AgentEvent> RunTurnStreamingAsync(AgentTurnRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("The workflow path runs buffered turns only.");

        public async ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
        {
            if (request.Caller is WorkflowCaller caller)
            {
                _nodes.Enqueue(caller.Run.CurrentNode);
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            return Result<AgentTurnResult, AgentError>.Failure(AgentError.Validation("unreachable"));
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _stopping.Dispose();
        }
    }
}
