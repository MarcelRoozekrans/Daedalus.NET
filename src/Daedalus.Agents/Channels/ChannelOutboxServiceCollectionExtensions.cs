using System.Data.Async;
using System.Data.Async.Adapters;
using Daedalus.Agents.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;

namespace Daedalus.Agents.Channels;

/// <summary>
///     Registers ZeroAlloc.Outbox's durable-delivery pipeline for <see cref="ChannelMessageQueued"/> and for the
///     scheduling message types <see cref="ScheduledRunDue"/>, <see cref="RunScoutStep"/>, <see cref="RunWriterStep"/>,
///     and <see cref="DeliverDigest"/>: the named pipeline <see cref="PipelineName"/> with its own poller, the
///     ZeroAlloc.Outbox.Orm store on the Daedalus database's <c>"OutboxMessages"</c> table, and the generated
///     <c>IOutboxWriter&lt;T&gt;</c> for each, keyed by <see cref="PipelineName"/>. All five message types share
///     the one poller registered below.
/// </summary>
/// <remarks>
///     <para>
///     <b>The table EF Core created, read in place.</b> <c>"OutboxMessages"</c> is the quoted, mixed-case table
///     ZeroAlloc.Outbox.EfCore's model created; the baseline migration owns it now. The store is pointed at it with
///     <see cref="OrmOutboxOptions.TableName"/> spelled with its quotes, and <see cref="OrmOutboxOptions.QuoteColumns"/>
///     on, because EF created its columns quoted and mixed case too. The columns, the lease ones included, are the
///     same, so pending and leased rows written by the EF store are claimed by this one as they are. The workflow
///     engine's table is the unquoted <c>OutboxMessages</c>, which PostgreSQL folds to <c>outboxmessages</c>: a
///     different table, polled by <c>WorkflowOutboxDispatchService</c>.
///     </para>
///     <para>
///     <b>A named pipeline, on a connection of its own.</b> The writers and the store are keyed by
///     <see cref="PipelineName"/>, so a consumer injects <c>[FromKeyedServices(PipelineName)] IOutboxWriter&lt;T&gt;</c>.
///     The store's connection is the keyed, scoped <see cref="IAsyncDbConnection"/> registered under
///     <see cref="ConnectionKey"/>: a connection from the host's one <see cref="NpgsqlDataSource"/>, opened by each
///     statement and returned to the pool after it, and disposed with the scope. The container has no unkeyed
///     <see cref="IAsyncDbConnection"/>, and this pipeline does not add one. A writer handed a transaction, as the
///     scheduling stores hand theirs, inserts on that transaction's own connection instead, so the row commits or
///     rolls back with the caller's other writes.
///     </para>
///     <para>
///     No <see cref="IOutboxDispatcher{T}"/> for <see cref="ChannelMessageQueued"/> is registered here — that is
///     the channel-sending business logic, added on top of this durability layer separately. Calling this method
///     alone leaves ZeroAlloc.Outbox's fallback <c>DefaultOutboxDispatcher&lt;ChannelMessageQueued&gt;</c>
///     registered, which throws at dispatch time. In practice a real dispatcher is always registered on top:
///     <see cref="DaedalusChannelsServiceCollectionExtensions.AddDaedalusChannels"/> replaces it with
///     <see cref="ChannelMessageQueuedDispatcher"/> via <c>Replace</c> so it wins regardless of call order.
///     Dispatchers are shared by every pipeline in the container, which is why the workflow engine's dispatcher is
///     never registered as an <see cref="IOutboxTypeDispatcher"/>.
///     </para>
/// </remarks>
public static class ChannelOutboxServiceCollectionExtensions
{
    /// <summary>
    ///     The name of the channel pipeline: the key of its writers, its store and its <see cref="IOutboxRequeue"/>,
    ///     and the name of its <see cref="OutboxOptions"/>.
    /// </summary>
    public const string PipelineName = "channel";

    /// <summary>The key of the scoped <see cref="IAsyncDbConnection"/> the pipeline's store runs on.</summary>
    public const string ConnectionKey = "channel-outbox";

    /// <summary>
    ///     The channel table, quoted as EF Core created it. Spliced verbatim into every statement the store issues,
    ///     so it is a constant and never configuration.
    /// </summary>
    public const string TableName = "\"OutboxMessages\"";

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
    ///             bounded by <c>DetachedRuns:DeadlineSeconds</c> (600 s today), and every host that calls
    ///             <c>AddDaedalusAgents</c> — the Api and the Cli — runs this worker against the same table. The
    ///             lease is renewed once per message, right before its dispatch, so it must outlast one whole turn;
    ///             the 5-minute default is shorter than the deadline, and a turn that ran to it would let the other host claim the
    ///             step and pay for the same turn again. <c>AddDaedalusAgents</c> rejects a deadline that is not
    ///             shorter than this lease.
    ///             The lease is shared by every message type on this pipeline, so it has a cost for chat: a host
    ///             that crashes while holding a batch leaves those replies unclaimable for up to 20 minutes, not 5,
    ///             which outweighs the 2 s <see cref="OutboxOptions.PollingInterval"/> above for that case. A
    ///             graceful stop releases unfinished leases at once; only a crash pays this.
    ///         </description></item>
    ///     </list>
    ///     <see cref="OutboxOptions.HostId"/> is left at its default, the machine name plus a value random per
    ///     process, which is unique per host as long as one process builds one host.
    /// </summary>
    public static IServiceCollection AddChannelOutbox(this IServiceCollection services)
    {
        // A connection per scope, from the host's one data source (ruling R4), disposed with the scope. Each
        // statement opens it and returns it to the pool, so it is never held across a poll interval.
        services.AddKeyedScoped<IAsyncDbConnection>(
            ConnectionKey, static (sp, _) => sp.GetRequiredService<NpgsqlDataSource>().CreateConnection().AsAsync());

        // ZeroAlloc.Outbox 4.x has no default serializer, and without one the worker fails the host start. The
        // generated AddSerializerDispatcher registers an ISerializerDispatcher that the outbox picks up; it routes
        // each [ZeroAllocSerializable] payload through OutboxJsonContext, so no reflection runs and Native AOT stays
        // safe. The context uses default options, which are what WithSystemTextJsonSerializer used, so rows already
        // in the table still deserialize and new rows carry the same bytes. It applies to every pipeline.
        services.AddSerializerDispatcher();
        services.AddOutbox(PipelineName, o =>
            {
                o.PollingInterval = TimeSpan.FromSeconds(2);
                o.BatchSize = 20;
                o.MaxAttempts = 8;
                o.RetryBaseDelay = TimeSpan.FromSeconds(1);
                o.LeaseDuration = LeaseDuration;
            })
            .WithOrm(OutboxOrmDialect.Postgres, ConnectionKey, o =>
            {
                o.TableName = TableName;
                o.QuoteColumns = true;
            })
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
