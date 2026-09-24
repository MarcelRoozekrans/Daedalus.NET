using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute.ExceptionExtensions;
using Thalos;
using Thalos.Skills;
using Thalos.Skills.Charters;
using Thalos.Workflow;

namespace Daedalus.Tests.Unit;

/// <summary>
///     Task B6: <see cref="ContentResyncService"/> re-runs the skill, charter and process-definition syncs on an
///     interval so an edited <c>SKILL.md</c>/<c>roles/*.md</c>/<c>processes/*.yaml</c> reaches a running host
///     without a restart. <see cref="SkillSyncService"/> and <see cref="CharterSyncService"/> are <c>sealed</c>,
///     so every test here drives a real instance of each over a substituted store — there is nothing to override.
/// </summary>
/// <remarks>
///     Falsifiability, per assertion — verified red by making exactly the edit described, then reverted:
///     <list type="bullet">
///     <item>
///     Replacing the per-sync <c>try</c>/<c>catch</c> in <c>ContentResyncService.RunSkillSyncAsync</c> and
///     the other two <c>Run*SyncAsync</c> methods with one shared <c>try</c>/<c>catch</c> around the whole
///     sequential chain in <c>ExecuteAsync</c> makes
///     <see cref="A_throwing_skill_sync_still_lets_the_charter_and_process_syncs_run_on_the_same_tick"/> and
///     <see cref="A_throwing_charter_sync_still_lets_the_skill_and_process_syncs_run_on_the_same_tick"/> fail: a
///     throw from the first call in the chain never lets the later two run at all. Removing only
///     <c>RunProcessSyncAsync</c>'s own <c>try</c>/<c>catch</c> (it runs last in the chain, so a same-tick
///     assertion cannot see it) makes
///     <see cref="A_throwing_process_sync_does_not_stop_the_loop_from_reaching_the_next_tick"/> fail instead: the
///     exception escapes <c>ExecuteAsync</c>'s <c>while</c> loop and no second tick ever runs — see that test's
///     own remarks (fix round 1, Important 2).
///     </item>
///     <item>
///     Deleting the <c>if (processSync is null) return;</c> guard in <c>RunProcessSyncAsync</c> throws a
///     <see cref="NullReferenceException"/> from inside that method's own <c>try</c>, which its
///     <c>ex is not OperationCanceledException</c> filter still catches and logs — so the loop itself would not
///     break, but <see cref="Null_process_sync_is_skipped_without_stopping_the_skill_and_charter_syncs"/> narrows
///     further and goes red on its own assertion that <em>no</em> error was logged for the process step.
///     </item>
///     <item>
///     Deleting the <c>while</c> loop's second iteration (replacing it with a single pass, no re-tick) makes
///     <see cref="A_failing_skill_sync_does_not_stop_the_loop_from_reaching_the_next_tick"/> fail: the second
///     tick's charter run never happens.
///     </item>
///     <item>
///     Removing <c>ExecuteAsync</c>'s outer <c>catch (OperationCanceledException)</c> entirely makes
///     <see cref="Stopping_after_a_tick_lets_the_execute_task_run_to_completion"/> fail: shutdown then ends
///     <see cref="BackgroundService.ExecuteTask"/> <see cref="TaskStatus.Canceled"/> instead of
///     <see cref="TaskStatus.RanToCompletion"/> (fix round 1, Minor 4).
///     </item>
///     </list>
/// </remarks>
public sealed class ContentResyncServiceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task Advancing_the_clock_by_one_interval_syncs_skills_charters_and_processes_once()
    {
        var clock = new FakeTimeProvider();
        var skillRuns = 0;
        var charterRuns = 0;
        var processRuns = 0;

        var skillSync = BuildSkillSync(SucceedingSkillStore(() => Interlocked.Increment(ref skillRuns)), clock);
        var charterSync = BuildCharterSync(SucceedingCharterStore(() => Interlocked.Increment(ref charterRuns)), clock);
        var processSync = BuildProcessSync(SucceedingProcessSource(() => Interlocked.Increment(ref processRuns)));

        var sut = new ContentResyncService(Interval, skillSync, charterSync, processSync, clock, NullLogger<ContentResyncService>.Instance);

        await RunOneTickAsync(sut, clock, () =>
            Volatile.Read(ref skillRuns) >= 1 && Volatile.Read(ref charterRuns) >= 1 && Volatile.Read(ref processRuns) >= 1);

        // ISkillStore.ListAsync and IProcessDefinitionSource.ReadAllAsync are each called exactly once per
        // SkillSyncService.SyncAsync / ProcessDefinitionSync.SyncAsync invocation (Thalos.NET v0.10.0, read from
        // source), so these pin "exactly one sync" rather than merely "at least one".
        Volatile.Read(ref skillRuns).Should().Be(1, "one interval must sync skills exactly once");
        Volatile.Read(ref processRuns).Should().Be(1, "one interval must sync process definitions exactly once");
        // CharterSyncService.SyncAsync calls IRoleCharterStore.ListVersionsAsync twice per successful invocation
        // (once to seed the known-active set, again in PublishAndValidateAsync to republish the full version
        // set) — so this counts underlying store calls, not sync invocations, and only proves "at least one".
        Volatile.Read(ref charterRuns).Should().BeGreaterThanOrEqualTo(1, "one interval must sync charters");
    }

    [Fact]
    public async Task A_throwing_skill_sync_still_lets_the_charter_and_process_syncs_run_on_the_same_tick()
    {
        var clock = new FakeTimeProvider();
        var charterRuns = 0;
        var processRuns = 0;

        var skillSync = BuildSkillSync(ThrowingSkillStore(new InvalidOperationException("simulated skill store failure")), clock);
        var charterSync = BuildCharterSync(SucceedingCharterStore(() => Interlocked.Increment(ref charterRuns)), clock);
        var processSync = BuildProcessSync(SucceedingProcessSource(() => Interlocked.Increment(ref processRuns)));

        var sut = new ContentResyncService(Interval, skillSync, charterSync, processSync, clock, NullLogger<ContentResyncService>.Instance);

        await RunOneTickAsync(sut, clock, () => Volatile.Read(ref charterRuns) >= 1 && Volatile.Read(ref processRuns) >= 1);

        Volatile.Read(ref charterRuns).Should().BeGreaterThanOrEqualTo(1, "a throwing skill sync must not stop the charter sync on the same tick");
        Volatile.Read(ref processRuns).Should().Be(1, "a throwing skill sync must not stop the process sync on the same tick");
    }

    [Fact]
    public async Task A_failing_skill_sync_result_still_lets_the_charter_and_process_syncs_run_on_the_same_tick()
    {
        var clock = new FakeTimeProvider();
        var charterRuns = 0;
        var processRuns = 0;

        var skillSync = BuildSkillSync(FailingSkillStore(AgentError.StoreError("simulated skill store failure")), clock);
        var charterSync = BuildCharterSync(SucceedingCharterStore(() => Interlocked.Increment(ref charterRuns)), clock);
        var processSync = BuildProcessSync(SucceedingProcessSource(() => Interlocked.Increment(ref processRuns)));

        var sut = new ContentResyncService(Interval, skillSync, charterSync, processSync, clock, NullLogger<ContentResyncService>.Instance);

        await RunOneTickAsync(sut, clock, () => Volatile.Read(ref charterRuns) >= 1 && Volatile.Read(ref processRuns) >= 1);

        Volatile.Read(ref charterRuns).Should().BeGreaterThanOrEqualTo(1, "a failed (not thrown) skill sync must not stop the charter sync on the same tick");
        Volatile.Read(ref processRuns).Should().Be(1, "a failed (not thrown) skill sync must not stop the process sync on the same tick");
    }

    [Fact]
    public async Task A_throwing_charter_sync_still_lets_the_skill_and_process_syncs_run_on_the_same_tick()
    {
        var clock = new FakeTimeProvider();
        var skillRuns = 0;
        var processRuns = 0;

        var skillSync = BuildSkillSync(SucceedingSkillStore(() => Interlocked.Increment(ref skillRuns)), clock);
        var charterSync = BuildCharterSync(ThrowingCharterStore(new InvalidOperationException("simulated charter store failure")), clock);
        var processSync = BuildProcessSync(SucceedingProcessSource(() => Interlocked.Increment(ref processRuns)));

        var sut = new ContentResyncService(Interval, skillSync, charterSync, processSync, clock, NullLogger<ContentResyncService>.Instance);

        await RunOneTickAsync(sut, clock, () => Volatile.Read(ref skillRuns) >= 1 && Volatile.Read(ref processRuns) >= 1);

        Volatile.Read(ref skillRuns).Should().Be(1, "a throwing charter sync must not stop the skill sync on the same tick");
        Volatile.Read(ref processRuns).Should().Be(1, "a throwing charter sync must not stop the process sync on the same tick");
    }

    /// <summary>
    ///     The process step runs <em>last</em> in a tick, so a same-tick assertion alone cannot fail here — by the
    ///     time it throws, the skill and charter syncs have already run whether or not they are isolated from it
    ///     (fix round 1, Important 2). What a throwing process sync can only break is the <em>next</em> tick: if
    ///     <c>RunProcessSyncAsync</c>'s own <c>try</c>/<c>catch</c> were removed, the exception would escape
    ///     <c>ExecuteAsync</c>'s <c>while</c> loop entirely and no second tick would ever run.
    /// </summary>
    [Fact]
    public async Task A_throwing_process_sync_does_not_stop_the_loop_from_reaching_the_next_tick()
    {
        var clock = new FakeTimeProvider();
        var skillRuns = 0;
        var charterRuns = 0;

        var skillSync = BuildSkillSync(SucceedingSkillStore(() => Interlocked.Increment(ref skillRuns)), clock);
        var charterSync = BuildCharterSync(SucceedingCharterStore(() => Interlocked.Increment(ref charterRuns)), clock);
        var processSync = BuildProcessSync(ThrowingProcessSource(new InvalidOperationException("simulated process source failure")));

        var sut = new ContentResyncService(Interval, skillSync, charterSync, processSync, clock, NullLogger<ContentResyncService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            clock.Advance(Interval);
            await AdvanceUntilAsync(clock, Interval, () => Volatile.Read(ref skillRuns) >= 1 && Volatile.Read(ref charterRuns) >= 1);

            clock.Advance(Interval);
            await AdvanceUntilAsync(clock, Interval, () => Volatile.Read(ref skillRuns) >= 2);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        Volatile.Read(ref skillRuns).Should().BeGreaterThanOrEqualTo(2,
            "the process sync throwing on the first tick must not stop the second tick's skill sync from running at all");
        Volatile.Read(ref charterRuns).Should().BeGreaterThanOrEqualTo(2,
            "the process sync throwing on the first tick must not stop the second tick's charter sync from running at all");
    }

    /// <summary>
    ///     The ruling: <c>ProcessDefinitionSync</c> is only registered when <c>Thalos:Workflow:Enabled</c> is true;
    ///     when it is not, <see cref="ContentResyncService"/> receives <see langword="null"/> for it and must skip
    ///     that step without failing the other two or logging an error for a step that never ran.
    /// </summary>
    [Fact]
    public async Task Null_process_sync_is_skipped_without_stopping_the_skill_and_charter_syncs()
    {
        var clock = new FakeTimeProvider();
        var skillRuns = 0;
        var charterRuns = 0;
        var logger = new RecordingLogger();

        var skillSync = BuildSkillSync(SucceedingSkillStore(() => Interlocked.Increment(ref skillRuns)), clock);
        var charterSync = BuildCharterSync(SucceedingCharterStore(() => Interlocked.Increment(ref charterRuns)), clock);

        var sut = new ContentResyncService(Interval, skillSync, charterSync, processSync: null, clock, logger);

        await RunOneTickAsync(sut, clock, () => Volatile.Read(ref skillRuns) >= 1 && Volatile.Read(ref charterRuns) >= 1);

        Volatile.Read(ref skillRuns).Should().Be(1, "a null process sync must not stop the skill sync");
        Volatile.Read(ref charterRuns).Should().BeGreaterThanOrEqualTo(1, "a null process sync must not stop the charter sync");
        logger.Levels.Should().NotContain(LogLevel.Error,
            "skipping a step that was never registered is not a failure and must not be logged as one");
    }

    /// <summary>
    ///     The other half of "does not stop the loop": a failure on one tick must not prevent the next tick from
    ///     running at all.
    /// </summary>
    [Fact]
    public async Task A_failing_skill_sync_does_not_stop_the_loop_from_reaching_the_next_tick()
    {
        var clock = new FakeTimeProvider();
        var charterRuns = 0;

        var skillSync = BuildSkillSync(ThrowingSkillStore(new InvalidOperationException("simulated skill store failure")), clock);
        var charterSync = BuildCharterSync(SucceedingCharterStore(() => Interlocked.Increment(ref charterRuns)), clock);
        var processSync = BuildProcessSync(SucceedingProcessSource(() => { }));

        var sut = new ContentResyncService(Interval, skillSync, charterSync, processSync, clock, NullLogger<ContentResyncService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            clock.Advance(Interval);
            await AdvanceUntilAsync(clock, Interval, () => Volatile.Read(ref charterRuns) >= 1);

            clock.Advance(Interval);
            await AdvanceUntilAsync(clock, Interval, () => Volatile.Read(ref charterRuns) >= 2);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        Volatile.Read(ref charterRuns).Should().BeGreaterThanOrEqualTo(2,
            "the skill sync throwing on the first tick must not stop the second tick from running at all");
    }

    /// <summary>Cancelling shutdown before any tick fires must complete cleanly and log nothing — it is not a sync failure.</summary>
    [Fact]
    public async Task Stopping_before_any_tick_completes_cleanly_and_logs_nothing()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();

        var skillSync = BuildSkillSync(SucceedingSkillStore(() => { }), clock);
        var charterSync = BuildCharterSync(SucceedingCharterStore(() => { }), clock);
        var processSync = BuildProcessSync(SucceedingProcessSource(() => { }));

        var sut = new ContentResyncService(Interval, skillSync, charterSync, processSync, clock, logger);

        await sut.StartAsync(CancellationToken.None);
        var stop = async () => await sut.StopAsync(CancellationToken.None);

        await stop.Should().NotThrowAsync();
        logger.Levels.Should().NotContain(LogLevel.Error, "shutdown cancellation is not a sync failure and must not be logged as one");
    }

    /// <summary>
    ///     Fix round 1, Minor 4: exercises <c>ExecuteAsync</c>'s outer <c>catch (OperationCanceledException)</c>
    ///     directly, after at least one real tick has run — not just the "stop before any tick" case above, which
    ///     can reach the outer catch before <c>WaitForNextTickAsync</c> is even awaited once.
    ///     <see cref="BackgroundService.StopAsync"/> does not rethrow a faulted execute task (fix round 1's
    ///     Important 2 finding), so a missing or narrowed catch would not surface as a thrown exception here.
    ///     It also would not necessarily leave <see cref="Task.IsFaulted"/> true: the C# compiler turns an
    ///     uncaught <see cref="OperationCanceledException"/> whose token matches the one that requested
    ///     cancellation into <see cref="TaskStatus.Canceled"/>, not <see cref="TaskStatus.Faulted"/> — the first
    ///     version of this test asserted <c>IsFaulted</c> alone and passed even with the catch removed. Asserting
    ///     the exact status is <see cref="TaskStatus.RanToCompletion"/> catches both.
    /// </summary>
    [Fact]
    public async Task Stopping_after_a_tick_lets_the_execute_task_run_to_completion()
    {
        var clock = new FakeTimeProvider();
        var skillRuns = 0;

        var skillSync = BuildSkillSync(SucceedingSkillStore(() => Interlocked.Increment(ref skillRuns)), clock);
        var charterSync = BuildCharterSync(SucceedingCharterStore(() => { }), clock);
        var processSync = BuildProcessSync(SucceedingProcessSource(() => { }));

        var sut = new ContentResyncService(Interval, skillSync, charterSync, processSync, clock, NullLogger<ContentResyncService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        clock.Advance(Interval);
        await AdvanceUntilAsync(clock, Interval, () => Volatile.Read(ref skillRuns) >= 1);

        await sut.StopAsync(CancellationToken.None);

        sut.ExecuteTask.Should().NotBeNull();
        sut.ExecuteTask!.Status.Should().Be(TaskStatus.RanToCompletion,
            "shutdown cancels the timer's wait, which the outer catch must swallow instead of letting the execute task end faulted or canceled");
    }

    private static async Task RunOneTickAsync(ContentResyncService sut, FakeTimeProvider clock, Func<bool> condition)
    {
        await sut.StartAsync(CancellationToken.None);
        try
        {
            clock.Advance(Interval);
            await AdvanceUntilAsync(clock, Interval, condition);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    ///     Waits for <paramref name="condition"/>, re-advancing the fake clock by <paramref name="interval"/>
    ///     between attempts: the loop registers its next timer asynchronously, so an advance can land while it is
    ///     still between two awaits and never be observed. Mirrors
    ///     <c>ReindexPendingMemoriesHostedServiceTests.AdvanceUntilAsync</c>.
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

        condition().Should().BeTrue("the resync loop should have run within the test's time budget");
    }

    private static SkillSyncService BuildSkillSync(ISkillStore store, TimeProvider clock) =>
        new(store, Substitute.For<ISkillIndex>(), new SkillCatalogue(), Options.Create(new SkillOptions { Roots = [] }), clock);

    private static CharterSyncService BuildCharterSync(IRoleCharterStore store, TimeProvider clock)
    {
        var options = Options.Create(new CharterOptions());
        var catalog = new CharteredAgentCatalog(Options.Create(new ThalosOptions()), options);
        return new CharterSyncService(store, catalog, options, clock);
    }

    private static ProcessDefinitionSync BuildProcessSync(IProcessDefinitionSource source) =>
        new(source, Substitute.For<IProcessDefinitionStore>(), Substitute.For<IWorkflowReferenceResolver>());

    private static ISkillStore SucceedingSkillStore(Action onCalled)
    {
        var store = Substitute.For<ISkillStore>();
        store.ListAsync(Arg.Any<SkillQuery>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            onCalled();
            return Result<IReadOnlyList<SkillDocument>, AgentError>.Success(Array.Empty<SkillDocument>());
        });
        return store;
    }

    private static ISkillStore ThrowingSkillStore(Exception exception)
    {
        var store = Substitute.For<ISkillStore>();
        store.ListAsync(Arg.Any<SkillQuery>(), Arg.Any<CancellationToken>()).Throws(exception);
        return store;
    }

    private static ISkillStore FailingSkillStore(AgentError error)
    {
        var store = Substitute.For<ISkillStore>();
        store.ListAsync(Arg.Any<SkillQuery>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<SkillDocument>, AgentError>.Failure(error));
        return store;
    }

    private static IRoleCharterStore SucceedingCharterStore(Action onCalled)
    {
        var store = Substitute.For<IRoleCharterStore>();
        store.ListVersionsAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            onCalled();
            return Result<IReadOnlyList<RoleCharter>, AgentError>.Success(Array.Empty<RoleCharter>());
        });
        return store;
    }

    private static IRoleCharterStore ThrowingCharterStore(Exception exception)
    {
        var store = Substitute.For<IRoleCharterStore>();
        store.ListVersionsAsync(Arg.Any<CancellationToken>()).Throws(exception);
        return store;
    }

    private static IProcessDefinitionSource SucceedingProcessSource(Action onCalled)
    {
        var source = Substitute.For<IProcessDefinitionSource>();
        source.ReadAllAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            onCalled();
            return (IReadOnlyList<ProcessDocument>)Array.Empty<ProcessDocument>();
        });
        return source;
    }

    private static IProcessDefinitionSource ThrowingProcessSource(Exception exception)
    {
        var source = Substitute.For<IProcessDefinitionSource>();
        source.ReadAllAsync(Arg.Any<CancellationToken>()).Throws(exception);
        return source;
    }

    /// <summary>
    ///     A hand-written <see cref="ILogger{TCategoryName}"/> that records every level logged, in place of
    ///     <c>Substitute.For&lt;ILogger&lt;ContentResyncService&gt;&gt;()</c>: Castle's proxy generator refuses to
    ///     build a proxy over <c>ILogger&lt;ContentResyncService&gt;</c> because <see cref="ContentResyncService"/>
    ///     is <see langword="internal"/> and this assembly carries no <c>InternalsVisibleTo</c> for Castle's
    ///     dynamic proxy assembly — a plain implementation needs no proxy and hits no such restriction.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ContentResyncService>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}

/// <summary>
///     Registration: <see cref="ContentResyncService"/> must be absent when <c>Thalos:Content:ResyncInterval</c>
///     is unset (the shipped default) and present when it is set, and — when present — must resolve the exact
///     same <see cref="SkillSyncService"/>/<see cref="CharterSyncService"/> instance the host's
///     <see cref="IHostedService"/> pipeline runs, not a second, independently-constructed one.
/// </summary>
/// <remarks>
///     Falsifiability: removing the <c>if (options.Content.ResyncInterval is { } resyncInterval)</c> guard in
///     <c>DaedalusAgentsServiceCollectionExtensions.AddDaedalusAgents</c> (always registering
///     <see cref="ContentResyncService"/>) makes
///     <see cref="Content_resync_is_absent_when_no_interval_is_configured"/> go red. Replacing
///     <c>ForwardHostedServiceToConcreteSingleton&lt;SkillSyncService&gt;(services)</c> with a bare
///     <c>services.TryAddSingleton&lt;SkillSyncService&gt;()</c> (no forwarding) makes
///     <see cref="Skill_sync_service_is_the_same_instance_the_hosted_pipeline_runs_and_content_resync_resolves"/>
///     go red: the hosted-service collection and the direct resolution then construct two different instances.
///     Changing the registration factory's <c>sp.GetService&lt;ProcessDefinitionSync&gt;()</c> to
///     <c>GetRequiredService</c> makes
///     <see cref="Content_resync_constructs_with_a_null_process_sync_when_the_workflow_engine_is_disabled"/> go
///     red: resolving <see cref="IHostedService"/> then throws instead of constructing
///     <see cref="ContentResyncService"/> with a null process sync (fix round 1, Important 1). Deleting
///     <c>ValidateContentConfig</c>'s non-positive-interval check makes
///     <see cref="A_non_positive_resync_interval_fails_host_start"/> go red (fix round 1, Minor 3). All were
///     verified red by making exactly those edits and reverted.
/// </remarks>
public sealed class ContentResyncServiceRegistrationTests
{
    private const string ApiAppSettingsFileName = "Daedalus.Api.appsettings.json";

    /// <param name="resyncInterval">Overrides <c>Thalos:Content:ResyncInterval</c>; <see langword="null"/> leaves it unset.</param>
    /// <param name="workflowEnabled">
    ///     Overrides <c>Thalos:Workflow:Enabled</c>; <see langword="true"/> (the shipped API default) leaves it
    ///     unset. <see langword="false"/> is what a host with the workflow engine off looks like — nothing
    ///     registers <see cref="ProcessDefinitionSync"/> in that configuration.
    /// </param>
    private static ServiceProvider BuildContainer(TimeSpan? resyncInterval, bool workflowEnabled = true)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.ContentRootPath.Returns(AppContext.BaseDirectory);
        environment.EnvironmentName.Returns("Development");

        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile(ApiAppSettingsFileName, optional: false);
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (resyncInterval is { } interval)
        {
            overrides["Thalos:Content:ResyncInterval"] = interval.ToString();
        }

        if (!workflowEnabled)
        {
            overrides["Thalos:Workflow:Enabled"] = "false";
        }

        if (overrides.Count > 0)
        {
            builder.AddInMemoryCollection(overrides);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IDbContextFactory<ApplicationDbContext>>());
        services.AddDaedalusAgents(builder.Build(), environment);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Content_resync_is_absent_when_no_interval_is_configured()
    {
        using var sp = BuildContainer(resyncInterval: null);

        sp.GetServices<IHostedService>().Should().NotContain(s => s is ContentResyncService,
            "shipped configuration sets no Thalos:Content:ResyncInterval, so no host pays for this loop by default");
    }

    [Fact]
    public void Content_resync_is_present_when_an_interval_is_configured()
    {
        using var sp = BuildContainer(TimeSpan.FromMinutes(5));

        sp.GetServices<IHostedService>().Should().ContainSingle(s => s is ContentResyncService);
    }

    [Fact]
    public void Skill_sync_service_is_the_same_instance_the_hosted_pipeline_runs_and_content_resync_resolves()
    {
        using var sp = BuildContainer(TimeSpan.FromMinutes(5));

        var hosted = sp.GetServices<IHostedService>().OfType<SkillSyncService>().Single();

        hosted.Should().BeSameAs(sp.GetRequiredService<SkillSyncService>(),
            "two live instances would mean two independent syncs racing the same skill store");
    }

    [Fact]
    public void Charter_sync_service_is_the_same_instance_the_hosted_pipeline_runs_and_content_resync_resolves()
    {
        using var sp = BuildContainer(TimeSpan.FromMinutes(5));

        var hosted = sp.GetServices<IHostedService>().OfType<CharterSyncService>().Single();

        hosted.Should().BeSameAs(sp.GetRequiredService<CharterSyncService>(),
            "two live instances would mean two independent syncs racing the same charter store");
    }

    /// <summary>
    ///     The forwarding must not change what runs at boot: the API host's real, shipped configuration leaves
    ///     <c>Thalos:Workflow:Enabled</c> true, so <see cref="ProcessDefinitionSync"/> must still resolve on its
    ///     own — forwarding <see cref="SkillSyncService"/>/<see cref="CharterSyncService"/> must not have disturbed it.
    /// </summary>
    [Fact]
    public void Process_definition_sync_still_resolves_when_content_resync_is_registered()
    {
        using var sp = BuildContainer(TimeSpan.FromMinutes(5));

        sp.GetServices<IHostedService>().Should().Contain(s => s is ProcessDefinitionSyncHostedService);
        sp.GetService<ProcessDefinitionSync>().Should().NotBeNull();
    }

    /// <summary>
    ///     Fix round 1, Important 1: the ruling requires the workflow-disabled case to work at the DI level, not
    ///     only when <see cref="ContentResyncService"/> is constructed by hand in
    ///     <see cref="ContentResyncServiceTests.Null_process_sync_is_skipped_without_stopping_the_skill_and_charter_syncs"/>.
    ///     With <c>Thalos:Workflow:Enabled</c> false, nothing registers <see cref="ProcessDefinitionSync"/> at
    ///     all, so <c>AddDaedalusAgents</c>'s registration factory must resolve it with
    ///     <c>sp.GetService&lt;ProcessDefinitionSync&gt;()</c> (nullable), not <c>GetRequiredService</c> — this
    ///     forces the factory to actually run by resolving <see cref="IHostedService"/>, which
    ///     <c>Content_resync_is_present_when_an_interval_is_configured</c> alone never does under this
    ///     configuration.
    /// </summary>
    [Fact]
    public void Content_resync_constructs_with_a_null_process_sync_when_the_workflow_engine_is_disabled()
    {
        using var sp = BuildContainer(TimeSpan.FromMinutes(5), workflowEnabled: false);

        sp.GetServices<IHostedService>().Should().ContainSingle(s => s is ContentResyncService,
            "the resync loop must still construct with the workflow engine off — it just skips the process step");
        sp.GetService<ProcessDefinitionSync>().Should().BeNull(
            "Thalos:Workflow:Enabled is false, so nothing registers ProcessDefinitionSync on this host");
    }

    /// <summary>Fix round 1, Minor 3: <c>ValidateContentConfig</c>'s non-positive-interval guard.</summary>
    [Fact]
    public void A_non_positive_resync_interval_fails_host_start()
    {
        var act = () => BuildContainer(TimeSpan.Zero);

        act.Should().Throw<InvalidOperationException>().WithMessage("*Thalos:Content:ResyncInterval*");
    }
}
