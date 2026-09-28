using System.Globalization;
using System.Reflection;
using Daedalus.Agents;
using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Sessions;
using Daedalus.Agents.Workflow;
using Daedalus.Cli;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Thalos.Workflow;
using ZeroAlloc.Outbox;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Pins the composed <c>Daedalus.Cli</c> host's scheduling wiring — the same properties
///     <c>ApiHostSchedulingWiringTests</c> pins for the API host: exactly one of each background worker, and
///     every scheduling dispatcher resolving as its real implementation rather than ZeroAlloc.Outbox's throwing
///     default.
/// </summary>
/// <remarks>
///     <c>Daedalus.Cli</c>'s entry point is top-level statements with no <c>WebApplicationFactory</c>-style
///     harness, so this calls <see cref="CliHostServices.ConfigureServices"/> — the method
///     <c>Program.cs</c>'s <c>ConfigureServices</c> lambda was extracted into for exactly this reason — directly
///     against a real <see cref="ServiceCollection"/>, with a configuration and <see cref="IHostEnvironment"/>
///     this test controls. That exercises the exact same registration code Program.cs runs; the one line left
///     untested is the lambda that calls it, which cannot silently drift from what is tested here the way a
///     duplicated or hand-rolled registration list could.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class CliHostSchedulingWiringTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void The_cli_host_registers_exactly_one_of_each_background_worker()
    {
        using var provider = BuildProvider();

        var hosted = provider.GetServices<IHostedService>().ToList();

        hosted.Count(h => h is OutboxWorkerService).Should().Be(1,
            "one AddOutbox call, one OutboxMessages table, one poller — channel and scheduling messages share it");
        hosted.Count(h => h is ScheduleSweeperService).Should().Be(1);
        hosted.Count(h => h is ScheduleReconcilerHostedService).Should().Be(1,
            "AddDaedalusAgents already registers it; AddDaedalusScheduling must not register a second one");
        hosted.Count(h => h is AgentSessionCrashRecovery).Should().Be(1);
    }

    [Fact]
    public void The_workflow_engine_is_wired_when_Thalos_Workflow_Enabled_is_not_overridden()
    {
        // BuildConfiguration below deliberately does not set Thalos:Workflow:Enabled, so this exercises
        // WorkflowConfig.Enabled's own default (true) — not src/Daedalus.Cli/appsettings.json's real value,
        // which is deliberately false there (see that file's own comment: this host has no migrations ordering,
        // and whether it may run the engine alongside the Api host is an open owner decision). This is the "present by default"
        // half of the guard ApiHostSchedulingWiringTests' NotContain assertions are the other half of: if this
        // default silently flipped to false, a real host that never overrides it (as Daedalus.Api does not, for
        // configuration reasons — the only override in this solution is Daedalus.Cli/appsettings.json's explicit
        // opt-out) would silently stop running the workflow engine at all.
        using var provider = BuildProvider();

        var hosted = provider.GetServices<IHostedService>().ToList();

        hosted.Count(h => h is WorkflowOutboxDispatchService).Should().Be(1);
        hosted.Count(h => h is WorkflowStrandedRunSweepService).Should().Be(1);
        hosted.Count(h => h is ProcessDefinitionSyncHostedService).Should().Be(1);
        hosted.Count(h => h is RunWorkspaceSweepService).Should().Be(1,
            "task B10: this is the only thing that removes a published run's worktree (rulings R14/R19)");
    }

    /// <summary>
    ///     Task B10 fix round 1: <see cref="RunWorkspaceSweeper"/> must see the same <see cref="IWorkflowStore"/>
    ///     <see cref="Daedalus.Agents.Workflow.WorkflowRunGateway"/> and <c>WorkflowRunReconciler</c> do — never a
    ///     decorated copy such as <c>ReviewHandoffWorkflowStore</c>/<c>WorkflowRunModeStore</c>, which only
    ///     <c>WorkflowNodeDispatcherFactory</c>'s dispatcher-facing copy gets (see that factory's own remarks). A
    ///     human reading a run must see everything it holds, which a decorator could hide or rewrite.
    /// </summary>
    /// <remarks>
    ///     Red: wrap the registration's <c>sp.GetRequiredService&lt;IWorkflowStore&gt;()</c> in one of those
    ///     decorators before handing it to <see cref="RunWorkspaceSweeper"/>'s constructor. The sweeper then holds a
    ///     different instance than <see cref="IWorkflowStore"/> itself resolves to, and this test's
    ///     <c>BeSameAs</c> assertion goes red — verified by making exactly that edit and reverting it.
    /// </remarks>
    [Fact]
    public void The_run_workspace_sweeper_resolves_over_the_undecorated_workflow_store()
    {
        using var provider = BuildProvider();

        var sweeper = provider.GetRequiredService<RunWorkspaceSweeper>();
        var store = provider.GetRequiredService<IWorkflowStore>();

        var field = typeof(RunWorkspaceSweeper).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance);
        field.Should().NotBeNull("RunWorkspaceSweeper is expected to keep its dependency in a private '_store' field");
        field!.GetValue(sweeper).Should().BeSameAs(store,
            "the sweeper must resolve the exact IWorkflowStore instance the container hands out elsewhere, not a decorated copy");
    }

    [Fact]
    public void The_cli_host_resolves_every_scheduling_dispatcher_as_the_real_implementation()
    {
        using var provider = BuildProvider();

        // Every one of these dispatchers depends on the scoped ScheduledRunExecutionStore, directly or via
        // ISubagentRunExecutor, so — like on the API host — none can resolve from the root provider.
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<IOutboxDispatcher<ScheduledRunDue>>().Should().BeOfType<ScheduledRunDueDispatcher>();
        services.GetRequiredService<IOutboxDispatcher<RunScoutStep>>().Should().BeOfType<RunScoutStepDispatcher>();
        services.GetRequiredService<IOutboxDispatcher<RunWriterStep>>().Should().BeOfType<RunWriterStepDispatcher>();
        services.GetRequiredService<IOutboxDispatcher<DeliverDigest>>().Should().BeOfType<DeliverDigestDispatcher>(
            "leaving DefaultOutboxDispatcher in place would dead-letter every step instead of running it");
    }

    /// <summary>
    ///     20 min lease + the dispatch gate's wait, <c>Thalos:Workflow:RoslynReadyTimeout</c> (10 min by default) + the
    ///     turn deadline + 254 s retry backoff + 5 min margin. Red: pass <see cref="TimeSpan.Zero"/> as the gate wait
    ///     again; the default row is then 34:14, and a run healthily waiting on the gate would be swept as stranded.
    /// </summary>
    [Theory]
    [InlineData(300, null, 44, 14)]
    [InlineData(300, "00:05:00", 39, 14)]
    [InlineData(420, "00:01:00", 37, 14)]
    public void The_stranded_run_sweep_threshold_is_derived_from_the_turn_deadline_and_the_gate_wait(
        int deadlineSeconds, string? roslynReadyTimeout, int minutes, int seconds)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DetachedRuns:DeadlineSeconds"] = deadlineSeconds.ToString(CultureInfo.InvariantCulture),
        };
        if (roslynReadyTimeout is not null)
        {
            overrides["Thalos:Workflow:RoslynReadyTimeout"] = roslynReadyTimeout;
        }

        using var provider = BuildProvider(overrides);

        var sweep = provider.GetServices<IHostedService>().OfType<WorkflowStrandedRunSweepService>().Single();

        sweep.StrandedAfter.Should().Be(new TimeSpan(0, minutes, seconds));
    }

    /// <summary>
    ///     A 10-minute turn behind the default 10-minute gate wait fills the whole 20-minute lease, so another replica
    ///     could claim the message mid-turn. Red: pass <see cref="TimeSpan.Zero"/> as the gate wait; registration then
    ///     passes.
    /// </summary>
    [Fact]
    public void A_turn_deadline_the_lease_cannot_hold_behind_the_gate_wait_fails_registration()
    {
        var act = () => BuildProvider(new(StringComparer.Ordinal) { ["DetachedRuns:DeadlineSeconds"] = "600" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*LeaseDuration*must exceed the longest dispatch*");
    }

    [Fact]
    public void A_turn_deadline_the_workflow_lease_cannot_hold_fails_registration()
    {
        var act = () => BuildProvider(new(StringComparer.Ordinal) { ["DetachedRuns:DeadlineSeconds"] = "1200" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*LeaseDuration*must exceed the longest dispatch*");
    }

    [Fact]
    public void A_turn_deadline_the_channel_outbox_lease_cannot_hold_fails_registration_with_the_engine_off()
    {
        var act = () => BuildProvider(new(StringComparer.Ordinal)
        {
            ["Thalos:Workflow:Enabled"] = "false",
            ["DetachedRuns:DeadlineSeconds"] = "1200",
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*channel and scheduling outbox's lease*");
    }

    [Fact]
    public void The_channel_outbox_worker_claims_with_the_lease_its_deadline_check_is_made_against()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IOptions<OutboxOptions>>().Value.LeaseDuration
            .Should().Be(ChannelOutboxServiceCollectionExtensions.LeaseDuration)
            .And.Be(TimeSpan.FromMinutes(20));
    }

    private ServiceProvider BuildProvider(Dictionary<string, string?>? overrides = null)
    {
        var services = new ServiceCollection();
        CliHostServices.ConfigureServices(services, BuildConfiguration(overrides), FakeEnvironment());
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     The minimal configuration <c>CliHostServices.ConfigureServices</c> needs to complete registration
    ///     without throwing — the same minimal shape <c>DaedalusAgentsRegistrationTests</c> and
    ///     <c>DaedalusChannelsRegistrationTests</c> already use, rather than the real <c>appsettings.json</c>
    ///     (which points <c>Thalos:Skills:Roots</c> at a folder that only exists next to the built host, not next
    ///     to the test assembly).
    /// </summary>
    private IConfiguration BuildConfiguration(Dictionary<string, string?>? overrides) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:daedalus"] = fixture.ConnectionString,
            ["Thalos:Anthropic:DefaultModel"] = "claude-sonnet-5",
            ["Thalos:Channels:DefaultAgent"] = "daedalus-assistant",
            // Required since AddDaedalusAgents started rejecting a blank Thalos:Squad:FallbackAgentName at
            // startup: a blank name throws ArgumentException out of Thalos' agent resolution rather than
            // answering "no such agent", so it is never a valid configuration in either squad mode. This
            // configuration declares no Thalos:Agents at all, which the check does not look at - it is the
            // shipped appsettings.json that has to name a real roster entry, and SquadConfigurationDriftTests
            // is what holds that.
            ["Thalos:Squad:FallbackAgentName"] = "daedalus-assistant",
            ["DetachedRuns:PrincipalId"] = "schedule:daedalus",
            ["DetachedRuns:Roles:0"] = "reader",
            ["DetachedRuns:MaxTotalTokens"] = "50000",
            ["DetachedRuns:DeadlineSeconds"] = "300",
        }).AddInMemoryCollection(overrides ?? []).Build();

    /// <summary>No skills roots are configured above, so <c>ContentRootPath</c> is never actually read.</summary>
    private static IHostEnvironment FakeEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        env.EnvironmentName.Returns("Development");
        return env;
    }
}
