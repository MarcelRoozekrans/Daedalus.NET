using Daedalus.Agents.Workflow;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using Thalos.Workflow;
using Thalos.Workspaces;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Task B10: <see cref="RunWorkspaceSweepService"/> hosts the real <see cref="RunWorkspaceSweeper"/> on a
///     one-minute <see cref="PeriodicTimer"/>, sweeping once immediately at start and then on every tick. This
///     tests only when sweeps happen and that the loop survives a failed sweep — which workspaces the sweeper
///     itself removes, given a run's status, is Thalos.NET's own <c>RunWorkspaceSweeperTests</c>.
/// </summary>
/// <remarks>
///     Falsifiability, per assertion — verified red by making exactly the edit described, watching the named test
///     fail on its own assertion (not an exception), then reverting:
///     <list type="bullet">
///     <item>
///     Replacing <c>ExecuteAsync</c>'s <c>do</c>/<c>while</c> with a plain
///     <c>while (await timer.WaitForNextTickAsync(...))</c> makes
///     <see cref="A_sweep_happens_before_the_first_tick_with_no_clock_advance"/> fail: the workspace is still
///     unremoved after the wait budget, because the first sweep then waits for the first tick instead of running
///     immediately.
///     </item>
///     <item>
///     Removing <c>SweepOnceAsync</c>'s <c>try</c>/<c>catch</c> entirely makes
///     <see cref="A_throwing_provider_is_logged_and_does_not_propagate"/> fail: the exception escapes
///     <c>SweepOnceAsync</c> instead of being caught and logged, so <c>ThrowAsync</c>-shaped assertions on it turn
///     red — and, at the loop level, the same edit would end every future sweep in production.
///     </item>
///     <item>
///     Narrowing the <c>catch</c> filter from <c>ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested</c>
///     to just <c>ex is not OperationCanceledException</c> makes
///     <see cref="A_timeout_inside_the_sweep_is_logged_and_does_not_propagate"/> fail: an
///     <see cref="OperationCanceledException"/> the provider raised on its own, with the stopping token still
///     live, is then swallowed by the narrower filter's negation and rethrown as if it were a real stop, instead
///     of being logged as a failed sweep.
///     </item>
///     <item>
///     Dropping the <c>|| !stoppingToken.IsCancellationRequested</c> half the other way — i.e. widening the guard
///     so it never treats a cancelled stopping token as a stop — makes
///     <see cref="A_stop_during_a_sweep_propagates"/> fail: the genuine stop is then caught and logged instead of
///     propagating, so the host could never shut the loop down.
///     </item>
///     <item>
///     Advancing the clock by one minute must reach a second sweep:
///     <see cref="Advancing_one_minute_sweeps_again"/> fails if the <c>PeriodicTimer</c> is constructed without
///     the injected <see cref="TimeProvider"/> (falls back to wall-clock time the test's <see cref="FakeTimeProvider"/>
///     never advances), or if the loop does not reach its second iteration at all.
///     </item>
///     <item>
///     <see cref="Cancellation_ends_the_loop_quietly"/> pins the observable shutdown contract directly, rather
///     than a single line inside it: stopping the host never throws out of <c>StopAsync</c> and never logs an
///     error for an ordinary shutdown.
///     </item>
///     </list>
/// </remarks>
public sealed class RunWorkspaceSweepServiceTests
{
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    private readonly IRunWorkspaceProvider _workspaces = Substitute.For<IRunWorkspaceProvider>();
    private readonly IWorkflowStore _store = Substitute.For<IWorkflowStore>();

    [Fact]
    public async Task A_sweep_happens_before_the_first_tick_with_no_clock_advance()
    {
        var clock = new FakeTimeProvider(Now);
        var removed = 0;
        _workspaces.ListAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<IReadOnlyList<RunWorkspace>>([Workspace(RunId, Now)]));
        _workspaces.RemoveAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref removed);
                return new ValueTask<UnitResult<AgentError>>(UnitResult<AgentError>.Success());
            });
        _store.FindAsync(RunId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<WorkflowRun?>(Run(WorkflowStatus.Succeeded)));
        var sut = Service(clock);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            // No clock advance anywhere in this test: only a sweep that runs before the loop's first
            // WaitForNextTickAsync can ever remove this workspace.
            await WaitUntilAsync(() => Volatile.Read(ref removed) >= 1);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        Volatile.Read(ref removed).Should().Be(1, "the run is Succeeded, so its workspace must be removed on the very first sweep");
    }

    [Fact]
    public async Task Advancing_one_minute_sweeps_again()
    {
        var clock = new FakeTimeProvider(Now);
        var listCalls = 0;
        _workspaces.ListAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref listCalls);
                return new ValueTask<IReadOnlyList<RunWorkspace>>(Array.Empty<RunWorkspace>());
            });
        var sut = Service(clock);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Volatile.Read(ref listCalls) >= 1);
            await AdvanceUntilAsync(clock, TimeSpan.FromMinutes(1), () => Volatile.Read(ref listCalls) >= 2);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        Volatile.Read(ref listCalls).Should().BeGreaterThanOrEqualTo(2, "advancing the clock by one minute must trigger a second sweep");
    }

    [Fact]
    public async Task Cancellation_ends_the_loop_quietly()
    {
        var clock = new FakeTimeProvider(Now);
        _workspaces.ListAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<RunWorkspace>>(Array.Empty<RunWorkspace>()));
        var logger = new RecordingLogger();
        var sut = Service(clock, logger);

        await sut.StartAsync(CancellationToken.None);
        var stop = async () => await sut.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync("shutdown cancellation is not a sweep failure and must not surface as a thrown exception");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error, "an ordinary shutdown must not be logged as a failed sweep");
    }

    /// <summary>
    ///     An observer's own timeout while listing the workspaces, not the stopping token: a failed sweep, logged,
    ///     and the loop must reach its next tick — the same rule <c>WorkflowOutboxDispatchService.IsStopping</c> and
    ///     <c>WorkflowStrandedRunSweepService.SweepOnceAsync</c> apply.
    /// </summary>
    [Fact]
    public async Task A_timeout_inside_the_sweep_is_logged_and_does_not_propagate()
    {
        _workspaces.ListAsync(Arg.Any<CancellationToken>())
            .Returns<ValueTask<IReadOnlyList<RunWorkspace>>>(_ => throw new OperationCanceledException("an observer's own timeout"));
        var logger = new RecordingLogger();
        var sut = Service(new FakeTimeProvider(Now), logger);

        var thrown = await Record.ExceptionAsync(() => sut.SweepOnceAsync(CancellationToken.None));

        thrown.Should().BeNull("the stopping token was never cancelled, so this is a failed sweep, not a stop, and the loop must survive");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && e.Exception is OperationCanceledException);
    }

    /// <summary>A genuine stop: the sweep observes the cancelled stopping token, and the loop must be allowed to end.</summary>
    [Fact]
    public async Task A_stop_during_a_sweep_propagates()
    {
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();
        _workspaces.ListAsync(Arg.Any<CancellationToken>())
            .Returns<ValueTask<IReadOnlyList<RunWorkspace>>>(_ => throw new OperationCanceledException(stopping.Token));
        var sut = Service(new FakeTimeProvider(Now));

        var sweep = async () => await sut.SweepOnceAsync(stopping.Token);

        await sweep.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    ///     A plain, non-cancellation failure while listing the workspaces (brief: "A throwing provider is logged,
    ///     and the loop continues").
    /// </summary>
    [Fact]
    public async Task A_throwing_provider_is_logged_and_does_not_propagate()
    {
        var boom = new IOException("listing the workspaces failed");
        _workspaces.ListAsync(Arg.Any<CancellationToken>())
            .Returns<ValueTask<IReadOnlyList<RunWorkspace>>>(_ => throw boom);
        var logger = new RecordingLogger();
        var sut = Service(new FakeTimeProvider(Now), logger);

        var thrown = await Record.ExceptionAsync(() => sut.SweepOnceAsync(CancellationToken.None));

        thrown.Should().BeNull("a failed sweep must not stop the loop from reaching its next tick");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error && ReferenceEquals(e.Exception, boom));
    }

    private RunWorkspaceSweepService Service(TimeProvider clock, ILogger<RunWorkspaceSweepService>? logger = null) =>
        new(
            new RunWorkspaceSweeper(_workspaces, _store, clock, NullLogger<RunWorkspaceSweeper>.Instance),
            clock,
            logger ?? NullLogger<RunWorkspaceSweepService>.Instance);

    private static RunWorkspace Workspace(Guid runId, DateTimeOffset createdAt) =>
        new(runId, "sandbox", "https://example.invalid/sandbox.git", "main", $"run/{runId}", $"/runs/{runId}", SolutionPath: null)
        {
            CreatedAt = createdAt,
        };

    private static WorkflowRun Run(WorkflowStatus status) => new()
    {
        Id = RunId,
        Process = "manufacture",
        ProcessVersion = 1,
        CurrentNode = "somewhere",
        CurrentSeq = 1,
        Status = status,
        Visits = new Dictionary<string, int>(StringComparer.Ordinal),
        StartedBy = new RunPrincipal("u-starter", ["developer"]),
    };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var elapsed = 0;
        while (!condition() && elapsed < timeoutMs)
        {
            await Task.Delay(10);
            elapsed += 10;
        }

        condition().Should().BeTrue("the sweep should have run within the test's time budget");
    }

    /// <summary>
    ///     Re-advances the fake clock between polling attempts: the loop registers its next timer wait
    ///     asynchronously, so a single advance can land while it is still between two awaits and never be
    ///     observed. Mirrors <c>ContentResyncServiceTests.AdvanceUntilAsync</c>.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider clock, TimeSpan interval, Func<bool> condition)
    {
        for (var attempt = 0; attempt < 20 && !condition(); attempt++)
        {
            if (attempt > 0)
            {
                clock.Advance(interval);
            }

            for (var i = 0; i < 20 && !condition(); i++)
            {
                await Task.Delay(10);
            }
        }

        condition().Should().BeTrue("the sweep loop should have reached its next tick within the test's time budget");
    }

    /// <summary>
    ///     A hand-written <see cref="ILogger{TCategoryName}"/> that records every entry logged, in place of
    ///     <c>Substitute.For&lt;ILogger&lt;RunWorkspaceSweepService&gt;&gt;()</c>: Castle's proxy generator refuses
    ///     to build a proxy over <c>ILogger&lt;RunWorkspaceSweepService&gt;</c> because
    ///     <see cref="RunWorkspaceSweepService"/> is <see langword="internal"/> and this assembly carries no
    ///     <c>InternalsVisibleTo</c> for Castle's dynamic proxy assembly — a plain implementation needs no proxy
    ///     and hits no such restriction. Mirrors <c>ContentResyncServiceTests.RecordingLogger</c>.
    /// </summary>
    private sealed class RecordingLogger : ILogger<RunWorkspaceSweepService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
