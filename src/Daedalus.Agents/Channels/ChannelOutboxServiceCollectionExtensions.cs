using Daedalus.Agents.Scheduling;
using Daedalus.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;

namespace Daedalus.Agents.Channels;

/// <summary>
///     Registers ZeroAlloc.Outbox's durable-delivery pipeline for <see cref="ChannelMessageQueued"/> and for the
///     scheduling message types <see cref="ScheduledRunDue"/>, <see cref="RunScoutStep"/>, <see cref="RunWriterStep"/>,
///     and <see cref="DeliverDigest"/>: the background poller, the EF Core store bound to
///     <see cref="ApplicationDbContext"/> (so the outbox table lives in the Daedalus database, not a separate
///     store), and the generated <c>IOutboxWriter&lt;T&gt;</c> for each. All five message types share the single
///     poller registered below — see the remarks on <see cref="AddChannelOutbox"/> for why a second one must never
///     be started.
/// </summary>
/// <remarks>
///     No <see cref="IOutboxDispatcher{T}"/> for <see cref="ChannelMessageQueued"/> is registered here — that is
///     the channel-sending business logic, added on top of this durability layer separately. Calling this method
///     alone leaves ZeroAlloc.Outbox's fallback <c>DefaultOutboxDispatcher&lt;ChannelMessageQueued&gt;</c>
///     registered, which throws at dispatch time. In practice a real dispatcher is always registered on top:
///     <see cref="DaedalusChannelsServiceCollectionExtensions.AddDaedalusChannels"/> replaces it with
///     <see cref="ChannelMessageQueuedDispatcher"/> via <c>Replace</c> so it wins regardless of call order.
/// </remarks>
public static class ChannelOutboxServiceCollectionExtensions
{
    /// <summary>
    ///     How long a claim on a channel or scheduling message lasts. See <see cref="AddChannelOutbox"/> for why
    ///     it must outlast one detached agent turn.
    /// </summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(20);

    /// <summary>
    ///     Registers the outbox pipeline described on the type — the channel message type and the scheduling
    ///     message types together, over the single poller below. Configuration is explicit rather than left at
    ///     the library defaults (5 s / 50 / 5) because a default that changes in a future ZeroAlloc.Outbox version
    ///     would otherwise silently change chat-delivery behaviour:
    ///     <list type="bullet">
    ///         <item><description>
    ///             <see cref="OutboxOptions.PollingInterval"/> = 2 s (default 5 s): a chat reply queued by a crashed
    ///             host should reach the user again quickly once it recovers; halving the default latency window
    ///             is cheap given <see cref="OutboxOptions.BatchSize"/> below.
    ///         </description></item>
    ///         <item><description>
    ///             <see cref="OutboxOptions.BatchSize"/> = 20 (default 50): one Daedalus host serves a modest
    ///             volume of chat replies per poll tick; a smaller batch bounds the worst-case per-tick database
    ///             round trip without needing a shorter interval to keep up.
    ///         </description></item>
    ///         <item><description>
    ///             <see cref="OutboxOptions.MaxAttempts"/> = 8 (default 5): a dropped chat reply is a visible user
    ///             failure and retries are free (dispatch is a channel API call, not something with side effects
    ///             that compound), so this absorbs a longer channel-API outage before giving up.
    ///         </description></item>
    ///         <item><description>
    ///             <see cref="OutboxOptions.RetryBaseDelay"/> = 1 s (default 2 s): the common failure mode is a
    ///             transient network blip, so the first retry comes sooner; combined with <c>MaxAttempts</c> = 8
    ///             the exponential backoff (1 s, 2 s, 4 s, 8 s, 16 s, 32 s, 64 s) still reaches minutes-scale for a
    ///             sustained outage before dead-lettering.
    ///         </description></item>
    ///         <item><description>
    ///             <see cref="OutboxOptions.LeaseDuration"/> = <see cref="LeaseDuration"/>, 20 minutes (default 5):
    ///             <see cref="RunScoutStep"/> and <see cref="RunWriterStep"/> each dispatch a detached agent turn
    ///             bounded by <c>DetachedRuns:DeadlineSeconds</c> (300 s today), and every host that calls
    ///             <c>AddDaedalusAgents</c> — the Api and the Cli — runs this worker against the same table. The
    ///             lease is renewed once per message, right before its dispatch, so it must outlast one whole turn;
    ///             the default equals the deadline, and a turn that ran to it would let the other host claim the
    ///             step and pay for the same turn again. <c>AddDaedalusAgents</c> rejects a deadline that is not
    ///             shorter than this lease.
    ///         </description></item>
    ///     </list>
    ///     <see cref="OutboxOptions.HostId"/> is left at its default, the machine name plus a value random per
    ///     process, which is unique per host as long as one process builds one host.
    /// </summary>
    public static IServiceCollection AddChannelOutbox(this IServiceCollection services)
    {
        // The serializer is left to AddOutbox's default, SystemTextJsonOutboxSerializer, because no
        // ISerializerDispatcher is registered. The rows already in the table were written in that format. A move
        // to ZeroAlloc.Outbox 4.x must call .WithSystemTextJsonSerializer() on this builder, which keeps the stored
        // byte format, so those rows still deserialize.
        services.AddOutbox(o =>
            {
                o.PollingInterval = TimeSpan.FromSeconds(2);
                o.BatchSize = 20;
                o.MaxAttempts = 8;
                o.RetryBaseDelay = TimeSpan.FromSeconds(1);
                o.LeaseDuration = LeaseDuration;
            })
            .WithEfCore<ApplicationDbContext>()
            .AddChannelMessageQueuedOutbox()
            // Scheduling rides the same table and the same poller. The generated IServiceCollection
            // overload of each of these is [Obsolete] under ZAOBOX010; with TreatWarningsAsErrors set
            // solution-wide (Directory.Build.props), calling it fails the build outright. That compile-time
            // failure is why the IOutboxBuilder form below is the one to use, not a runtime race avoided.
            .AddScheduledRunDueOutbox()
            .AddRunScoutStepOutbox()
            .AddRunWriterStepOutbox()
            .AddDeliverDigestOutbox();

        return services;
    }
}
