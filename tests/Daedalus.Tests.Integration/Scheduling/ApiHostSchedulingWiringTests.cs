using Daedalus.Agents;
using Daedalus.Agents.Scheduling;
using Daedalus.Agents.Sessions;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using ZeroAlloc.Outbox;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Pins the composed <c>Daedalus.Api</c> host's scheduling wiring: exactly one of each background worker —
///     including the shared <see cref="OutboxWorkerService"/> poller and the single
///     <see cref="ScheduleReconcilerHostedService"/> that both <c>AddDaedalusAgents</c> registers and
///     <see cref="DaedalusSchedulingServiceCollectionExtensions.AddDaedalusScheduling"/> must not duplicate — and
///     every one of the four scheduling step dispatchers resolving as its real implementation rather than
///     ZeroAlloc.Outbox's throwing default.
/// </summary>
/// <remarks>
///     Deliberately boots the real <c>Daedalus.Api</c> <c>Program</c> via <see cref="ApiWebApplicationFactory"/>,
///     the same reasoning as <c>ApiHostChannelWiringTests</c>: the coupling under test — that
///     <c>AddDaedalusAgents</c>, <c>AddDaedalusChannels</c> and <c>AddDaedalusScheduling</c> compose correctly on
///     top of each other — is a property of how <c>Program.cs</c> wires them together, not of any one method in
///     isolation.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ApiHostSchedulingWiringTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly IAgentRuntime _runtime = Substitute.For<IAgentRuntime>();
    private ApiWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _factory = new ApiWebApplicationFactory(fixture.ConnectionString, _runtime);
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void The_api_host_registers_exactly_one_of_each_background_worker()
    {
        var hosted = _factory.Services.GetServices<IHostedService>().ToList();

        hosted.Count(h => h is OutboxWorkerService).Should().Be(1,
            "one AddOutbox call, one OutboxMessages table, one poller — channel and scheduling messages share it");
        hosted.Count(h => h is ScheduleSweeperService).Should().Be(1);
        hosted.Count(h => h is ScheduleReconcilerHostedService).Should().Be(1,
            "AddDaedalusAgents already registers it; AddDaedalusScheduling must not register a second one");
        hosted.Count(h => h is AgentSessionCrashRecovery).Should().Be(1);
    }

    [Fact]
    public void The_api_host_resolves_every_scheduling_dispatcher_as_the_real_implementation()
    {
        // Every one of these dispatchers depends on the scoped ScheduledRunExecutionStore (directly, or via
        // ISubagentRunExecutor), so — like IOutboxWriter<T> in ApiHostChannelWiringTests — none can resolve from
        // the host's root provider; a scope is required.
        using var scope = _factory.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<IOutboxDispatcher<ScheduledRunDue>>().Should().BeOfType<ScheduledRunDueDispatcher>();
        services.GetRequiredService<IOutboxDispatcher<RunScoutStep>>().Should().BeOfType<RunScoutStepDispatcher>();
        services.GetRequiredService<IOutboxDispatcher<RunWriterStep>>().Should().BeOfType<RunWriterStepDispatcher>();
        services.GetRequiredService<IOutboxDispatcher<DeliverDigest>>().Should().BeOfType<DeliverDigestDispatcher>(
            "leaving DefaultOutboxDispatcher in place would dead-letter every step instead of running it");
    }
}
