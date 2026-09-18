using Daedalus.Agents.Scheduling;
using Daedalus.Application.Abstractions;
using Daedalus.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;

namespace Daedalus.Agents;

/// <summary>Composition root for scheduled-run execution on the Daedalus host.</summary>
public static class DaedalusSchedulingServiceCollectionExtensions
{
    /// <summary>
    ///     Registers the trigger for scheduled runs (<see cref="ScheduleSweeperService"/>), the two scoped stores
    ///     that hold all of the sweep and step logic (<see cref="ScheduledRunStore"/>,
    ///     <see cref="ScheduledRunExecutionStore"/>), the single seam onto a subagent
    ///     (<see cref="ISubagentRunExecutor"/>) with the <c>DetachedRuns</c> options it reads its budget from, the
    ///     read-only diagnostics seam <see cref="ScheduleDiagnostics"/> with its own options section, the
    ///     write-only delivery seam <see cref="ScheduleDeliveryActions"/> kept deliberately separate from it, and
    ///     the four step dispatchers in place of ZeroAlloc.Outbox's throwing defaults.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Host configuration; the <c>DetachedRuns</c> and <c>ScheduleDiagnostics</c> sections are read.</param>
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
    ///     <para>
    ///     <b>The extensibility cost this design accepted.</b> A saga library would have let a new workflow be one
    ///     <c>[Saga]</c> class. Without one, adding a second workflow next to <c>RepoDigest</c> means: a new step
    ///     record per stage (like <see cref="Scheduling.RunScoutStep"/>, <see cref="Scheduling.RunWriterStep"/>,
    ///     <see cref="Scheduling.DeliverDigest"/>), a dispatcher per record registered here with its own
    ///     <see cref="ServiceCollectionDescriptorExtensions.Replace"/> call, and branching in
    ///     <see cref="Scheduling.ScheduledRunExecutionStore.TryBeginAsync"/> to choose which chain a schedule
    ///     starts. <b>Adding the new workflow's name to <see cref="Scheduling.ScheduleReconciler.KnownTriggers"/> is
    ///     not, by itself, enough</b> — <c>KnownTriggers</c> is validated at boot but never read at run time:
    ///     <c>TryBeginAsync</c> does not look at <c>ScheduledRun.Trigger</c> at all today and
    ///     unconditionally starts the <c>RepoDigest</c> chain, so a schedule whose <c>Trigger</c> names a second,
    ///     validated workflow would still silently run <c>RepoDigest</c> until that branching is added. This was
    ///     accepted because the alternative — <c>ZeroAlloc.Saga</c> — was found undriveable in this phase (its
    ///     generated <c>Publish</c> dispatches to a closed list of handler types fixed at the saga's own compile
    ///     time, and never enumerates handlers registered later via DI): a library that promises one class but
    ///     cannot actually be driven is not cheaper than four.
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

        // Scoped, not singleton — same rule as the two stores above: ScheduleDiagnostics takes ApplicationDbContext
        // directly, and EfCoreOutboxStore.EnqueueAsync needs connection identity with whatever else is in the scope.
        services.AddScoped<IScheduleDiagnostics, ScheduleDiagnostics>();
        services.Configure<ScheduleDiagnosticsOptions>(configuration.GetSection(ScheduleDiagnosticsOptions.SectionName));

        // TryAdd: EfCoreOutboxStore<ApplicationDbContext> implements both IOutboxStore and IOutboxDashboardStore,
        // but AddChannelOutbox's WithEfCore<ApplicationDbContext>() call only registers the former. This fills
        // the dashboard seam without risking a second, conflicting registration if a future ZeroAlloc.Outbox
        // version starts registering it itself.
        services.TryAddScoped<IOutboxDashboardStore, EfCoreOutboxStore<ApplicationDbContext>>();

        // Scoped, same rule as ScheduleDiagnostics: ScheduleDeliveryActions also takes ApplicationDbContext
        // directly. Deliberately its own registration rather than added to IScheduleDiagnostics — see
        // IScheduleDeliveryActions's remarks for why the read-only seam handed to agent tools must stay read-only.
        services.AddScoped<IScheduleDeliveryActions, ScheduleDeliveryActions>();

        // Replace, not Add — see remarks above.
        services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<ScheduledRunDue>, ScheduledRunDueDispatcher>());
        services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<RunScoutStep>, RunScoutStepDispatcher>());
        services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<RunWriterStep>, RunWriterStepDispatcher>());
        services.Replace(ServiceDescriptor.Transient<IOutboxDispatcher<DeliverDigest>, DeliverDigestDispatcher>());

        services.AddHostedService<ScheduleSweeperService>();

        return services;
    }
}
