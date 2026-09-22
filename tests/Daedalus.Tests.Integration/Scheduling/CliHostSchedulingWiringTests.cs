using Daedalus.Agents;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Sessions;
using Daedalus.Agents.Workflow;
using Daedalus.Cli;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        // which is deliberately false there (see that file's own comment: a second poller against the same
        // outbox table as the Api host only buys duplicate paid agent turns). This is the "present by default"
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

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        CliHostServices.ConfigureServices(services, BuildConfiguration(), FakeEnvironment());
        return services.BuildServiceProvider();
    }

    /// <summary>
    ///     The minimal configuration <c>CliHostServices.ConfigureServices</c> needs to complete registration
    ///     without throwing — the same minimal shape <c>DaedalusAgentsRegistrationTests</c> and
    ///     <c>DaedalusChannelsRegistrationTests</c> already use, rather than the real <c>appsettings.json</c>
    ///     (which points <c>Thalos:Skills:Roots</c> at a folder that only exists next to the built host, not next
    ///     to the test assembly).
    /// </summary>
    private IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:daedalus"] = fixture.ConnectionString,
            ["Thalos:Anthropic:DefaultModel"] = "claude-sonnet-5",
            ["Thalos:Channels:DefaultAgent"] = "daedalus-assistant",
            ["DetachedRuns:PrincipalId"] = "schedule:daedalus",
            ["DetachedRuns:Roles:0"] = "reader",
            ["DetachedRuns:MaxTotalTokens"] = "50000",
            ["DetachedRuns:DeadlineSeconds"] = "300",
        }).Build();

    /// <summary>No skills roots are configured above, so <c>ContentRootPath</c> is never actually read.</summary>
    private static IHostEnvironment FakeEnvironment()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        env.EnvironmentName.Returns("Development");
        return env;
    }
}
