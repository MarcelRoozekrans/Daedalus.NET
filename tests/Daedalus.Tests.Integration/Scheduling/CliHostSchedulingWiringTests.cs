using System.Globalization;
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

    [Theory]
    [InlineData(300, 34, 14)]
    [InlineData(600, 39, 14)]
    public void The_stranded_run_sweep_threshold_is_derived_from_the_configured_turn_deadline(
        int deadlineSeconds, int minutes, int seconds)
    {
        using var provider = BuildProvider(new(StringComparer.Ordinal) { ["DetachedRuns:DeadlineSeconds"] = deadlineSeconds.ToString(CultureInfo.InvariantCulture) });

        var sweep = provider.GetServices<IHostedService>().OfType<WorkflowStrandedRunSweepService>().Single();

        // 20 min lease + the turn deadline + 254 s retry backoff + 5 min margin; no dispatch gate yet.
        sweep.StrandedAfter.Should().Be(new TimeSpan(0, minutes, seconds));
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
