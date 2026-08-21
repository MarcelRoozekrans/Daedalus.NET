using Daedalus.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;

namespace Daedalus.Agents.Channels;

/// <summary>
///     Registers ZeroAlloc.Outbox's durable-delivery pipeline for <see cref="ChannelMessageQueued"/>: the
///     background poller, the EF Core store bound to <see cref="ApplicationDbContext"/> (so the outbox table
///     lives in the Daedalus database, not a separate store), and the generated
///     <c>IOutboxWriter&lt;ChannelMessageQueued&gt;</c>.
/// </summary>
/// <remarks>
///     No <see cref="IOutboxDispatcher{T}"/> for <see cref="ChannelMessageQueued"/> is registered here — that is
///     the channel-sending business logic, added on top of this durability layer separately. Until it exists,
///     ZeroAlloc.Outbox's fallback <c>DefaultOutboxDispatcher&lt;ChannelMessageQueued&gt;</c> throws at dispatch
///     time, which is correct: nothing writes a <see cref="ChannelMessageQueued"/> yet either, so the worker never
///     has a row to dispatch.
/// </remarks>
public static class ChannelOutboxServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the outbox pipeline described on the type. Configuration is explicit rather than left at the
    ///     library defaults (5 s / 50 / 5) because a default that changes in a future ZeroAlloc.Outbox version
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
    ///     </list>
    /// </summary>
    public static IServiceCollection AddChannelOutbox(this IServiceCollection services)
    {
        services.AddOutbox(o =>
            {
                o.PollingInterval = TimeSpan.FromSeconds(2);
                o.BatchSize = 20;
                o.MaxAttempts = 8;
                o.RetryBaseDelay = TimeSpan.FromSeconds(1);
            })
            .WithEfCore<ApplicationDbContext>()
            .AddChannelMessageQueuedOutbox();

        return services;
    }
}
