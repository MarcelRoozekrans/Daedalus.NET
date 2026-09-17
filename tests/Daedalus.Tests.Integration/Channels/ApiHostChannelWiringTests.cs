using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos;
using ZeroAlloc.Outbox;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Channels;

/// <summary>
///     Pins the coupling documented on <see cref="DaedalusChannelsServiceCollectionExtensions.AddDaedalusChannels"/>:
///     the composed <c>Daedalus.Api</c> host must resolve BOTH <c>OutboxWorkerService</c> — the durability-layer
///     poller wired by <c>AddDaedalusAgents</c>'s call to <c>AddChannelOutbox</c> — AND
///     <see cref="ChannelMessageQueuedDispatcher"/> as <c>IOutboxDispatcher&lt;ChannelMessageQueued&gt;</c> — the
///     dispatcher <see cref="DaedalusChannelsServiceCollectionExtensions.AddDaedalusChannels"/> installs in place
///     of ZeroAlloc.Outbox's throwing default. A host that registered only the first would run a poller with
///     nothing plugged into it; only the second would leave a dispatcher over a queue nothing polls. Either half
///     missing is silently inert today (nothing writes a <see cref="ChannelMessageQueued"/> yet), which is exactly
///     what would make a regression here invisible until phase 1.5 adds the writer and messages quietly stop
///     being delivered.
/// </summary>
/// <remarks>
///     Deliberately boots the real <c>Daedalus.Api</c> <c>Program</c> via <see cref="ApiWebApplicationFactory"/>
///     rather than a hand-built <c>ServiceCollection</c>: the coupling under test is a property of how the API
///     host composes <c>AddDaedalusAgents</c> and <c>AddDaedalusChannels</c> together (see the "does not itself
///     call AddChannelOutbox" remark on <see cref="DaedalusChannelsServiceCollectionExtensions.AddDaedalusChannels"/>),
///     not of either method in isolation — <c>DaedalusChannelsRegistrationTests</c> already covers
///     <see cref="DaedalusChannelsServiceCollectionExtensions.AddDaedalusChannels"/> alone, order-independently,
///     against a hand-built collection. Only a real composed host proves the two are wired on top of each other
///     the way <c>Program.cs</c> actually does it.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ApiHostChannelWiringTests(PostgresFixture fixture) : IAsyncLifetime
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
    public void The_API_host_resolves_the_outbox_worker_and_the_channel_dispatcher_together()
    {
        var services = _factory.Services;

        services.GetServices<IHostedService>().Should().ContainSingle(s => s is OutboxWorkerService,
            "AddDaedalusAgents wires the outbox durability layer (AddChannelOutbox) this host depends on; " +
            "without it, AddDaedalusChannels' dispatcher below has nothing that ever calls it");

        services.GetRequiredService<IOutboxDispatcher<ChannelMessageQueued>>().Should().BeOfType<ChannelMessageQueuedDispatcher>(
            "AddDaedalusChannels must replace ZeroAlloc.Outbox's throwing default dispatcher, or the worker " +
            "above would dead-letter every queued message instead of delivering it through a real IChannelAdapter");
    }

    [Fact]
    public void The_API_host_runs_exactly_one_outbox_poller_for_every_message_type()
    {
        var services = _factory.Services;

        services.GetServices<IHostedService>().Count(s => s is OutboxWorkerService).Should().Be(1,
            "every [OutboxMessage] type rides one AddOutbox call and one OutboxMessages table, so exactly one " +
            "OutboxWorkerService should drain it; calling AddOutbox() a second time (the generated " +
            "IServiceCollection overloads, ZAOBOX010, do this) registers OutboxWorkerService a second time too, " +
            "but AddHostedService's TryAddEnumerable dedupes identical (service, implementation) pairs, so that " +
            "specific mistake is caught at compile time instead, by the ZAOBOX010 obsolete diagnostic under " +
            "TreatWarningsAsErrors. This assertion is defence in depth against a registration shape dedupe would " +
            "not catch — a factory-lambda or wrapper-type registration of a second worker over the same table");

        // IOutboxWriter<T> is scoped (it shares the ambient IOutboxStore/DbContext — see ScheduledRunStore's
        // remarks), so it cannot resolve from the host's root provider; a scope is required, same as
        // ChannelOutboxTests' WriteAsync helper.
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IOutboxWriter<ScheduledRunDue>>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IOutboxWriter<RunScoutStep>>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IOutboxWriter<RunWriterStep>>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IOutboxWriter<DeliverDigest>>().Should().NotBeNull();
    }
}
