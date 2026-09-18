using Daedalus.Agents.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;

namespace Daedalus.Agents;

/// <summary>Composition root for scheduled-run execution on the Daedalus host.</summary>
public static class DaedalusSchedulingServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the trigger for scheduled runs (<see cref="ScheduleSweeperService"/>), the two scoped stores
    ///     that hold all of the sweep and step logic (<see cref="ScheduledRunStore"/>,
    ///     <see cref="ScheduledRunExecutionStore"/>), the single seam onto a subagent
    ///     (<see cref="ISubagentRunExecutor"/>) with the <c>DetachedRuns</c> options it reads its budget from, and
    ///     the four step dispatchers in place of ZeroAlloc.Outbox's throwing defaults.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration; the <c>DetachedRuns</c> section is read.</param>
    /// <remarks>
    ///     <para>
    ///     <b>No scheduling package.</b> This method originally planned to lean on
    ///     <c>ZeroAlloc.Scheduling</c>/<c>ZeroAlloc.Scheduling.EfCore</c> for the once-a-minute trigger; that was
    ///     dropped for this phase and <see cref="ScheduleSweeperService"/> is a plain <see cref="BackgroundService"/>
    ///     instead. See that type's remarks for the full reasoning — in short, the package's EF Core store needs
    ///     its own <c>SchedulingDbContext</c> with its own migration, which nothing here needs: the sweep is
    ///     idempotent and <c>ScheduledRuns.NextRunAt</c> is the only state that matters. This is reversible and
    ///     recorded as a phase 1.5 roadmap change for that reason.
    ///     </para>
    ///     <para>
    ///     <b>Do not call <c>AddChannelOutbox</c> or any <c>AddOutbox</c> here.</b> The outbox — including the
    ///     four scheduling step writers — is wired by <c>AddDaedalusAgents</c>; a second call would register a
    ///     second <c>OutboxWorkerService</c> racing the same table. See
    ///     <see cref="Channels.DaedalusChannelsServiceCollectionExtensions.AddDaedalusChannels"/> for the same
    ///     hazard on the channels side.
    ///     </para>
    ///     <para>
    ///     <b>Do not register a second <c>ScheduleReconcilerHostedService</c>.</b> <c>AddDaedalusAgents</c> already
    ///     registers it, and it also runs the agent-name validation this host depends on at boot.
    ///     </para>
    ///     <para>
    ///     <b>The four <see cref="ServiceCollectionDescriptorExtensions.Replace"/> calls below are load-bearing,
    ///     not a stylistic choice over <c>Add</c>.</b> The outbox source generator's <c>AddXOutbox()</c> — called
    ///     for each of these four message types by <c>AddDaedalusAgents</c>, via <c>AddChannelOutbox</c> —
    ///     registers ZeroAlloc.Outbox's throwing <c>DefaultOutboxDispatcher&lt;T&gt;</c> with <c>TryAddTransient</c>.
    ///     A plain <c>Add</c> here would leave registration order deciding which dispatcher wins; <c>Replace</c>
    ///     wins unconditionally regardless of call order — the same approach
    ///     <see cref="Channels.DaedalusChannelsServiceCollectionExtensions.AddDaedalusChannels"/> already uses for
    ///     <c>ChannelMessageQueuedDispatcher</c>. Leaving the throwing default in place would dead-letter every
    ///     step instead of running it.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddDaedalusScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<DetachedRunOptions>(configuration.GetSection("DetachedRuns"));

        // TryAdd, not Add: ScheduledRunStore and ScheduledRunExecutionStore both take TimeProvider directly.
        // AddThalos (called by AddDaedalusAgents, which every caller of this method already calls) registers one
        // today — verified empirically — but nothing in this file should quietly depend on that staying true
        // forever. TryAddSingleton is a no-op wherever a host already has one and never overrides it.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped, not singleton: both stores share ApplicationDbContext (also scoped) with the outbox writers they
        // call — see ScheduledRunStore's and ScheduledRunExecutionStore's own remarks for why. ISubagentRunExecutor
        // follows the same rule so it is safe to depend on regardless of what lifetime Thalos gives ISubagentRunner.
        services.AddScoped<ScheduledRunStore>();
        services.AddScoped<ScheduledRunExecutionStore>();
        services.AddScoped<ISubagentRunExecutor, SubagentRunExecutor>();

        // Replace, not Add — see remarks above.
        services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<ScheduledRunDue>, ScheduledRunDueDispatcher>());
        services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<RunScoutStep>, RunScoutStepDispatcher>());
        services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<RunWriterStep>, RunWriterStepDispatcher>());
        services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<DeliverDigest>, DeliverDigestDispatcher>());

        services.AddHostedService<ScheduleSweeperService>();

        return services;
    }
}
