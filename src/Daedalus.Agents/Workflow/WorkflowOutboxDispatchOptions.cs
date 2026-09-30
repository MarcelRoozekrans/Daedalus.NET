using Microsoft.Extensions.Configuration;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Configures <see cref="WorkflowOutboxDispatchService"/>'s poll loop. Deliberately its own type rather than
///     <c>ZeroAlloc.Outbox.OutboxOptions</c>: that type is a single, un-keyed options instance in this host's
///     container (<c>AddChannelOutbox</c> already binds one for the ApplicationDbContext-backed pipeline), so
///     configuring it a second time here would also rewrite the scheduling/channel outbox's polling interval,
///     batch size, retry budget and lease out from under it. <see cref="LeaseDuration"/> and <see cref="HostId"/>
///     live here for the same reason.
/// </summary>
/// <remarks>
///     <b>Bound from <c>Thalos:Workflow:Dispatch</c></b> by <see cref="Bind"/>, so a host whose turn deadline or
///     readiness wait outgrows the default lease can raise <see cref="LeaseDuration"/> with them. Every value is still
///     validated at registration, against the turn deadline and the gate wait, by
///     <see cref="WorkflowDispatchTiming.Validate"/>, which also keeps <see cref="BatchSize"/> at 1 whatever the
///     configuration says; the reasoning is on that property. <see cref="HostId"/> is never configured: it identifies
///     one process's leases, and a value in shared configuration would give every replica the same one.
/// </remarks>
internal sealed class WorkflowOutboxDispatchOptions
{
    /// <summary>The configuration section <see cref="Bind"/> reads.</summary>
    internal const string SectionName = "Thalos:Workflow:Dispatch";

    /// <summary>The width of the ORM outbox table's <c>LockedBy</c> column, which stores <see cref="HostId"/>.</summary>
    internal const int MaxHostIdLength = 128;

    /// <summary>
    ///     The longest lease accepted, matching <c>ZeroAlloc.Outbox</c>'s own <c>OutboxOptions</c> validation: a
    ///     lease this long already keeps a crashed host's message unclaimable for a day.
    /// </summary>
    internal static readonly TimeSpan MaxLeaseDuration = TimeSpan.FromDays(1);

    private static readonly string DefaultHostId = $"{Environment.MachineName}:{Guid.NewGuid():N}";

    /// <summary>How often <see cref="WorkflowOutboxDispatchService"/> polls the workflow outbox table.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     The maximum number of pending rows claimed per poll. <b>Fixed at 1, not the 20
    ///     <c>docs/workflow.md</c>'s own example batches</b>, for two reasons. <see cref="WorkflowOutboxDispatchService.ProcessBatchAsync"/>
    ///     dispatches its batch serially, and each entry here is a full agent turn costing minutes, not a cheap
    ///     message worth amortising a round trip over: batching 20 of these freezes the twentieth run's
    ///     <c>updated_at</c> for every turn ahead of it, which <see cref="WorkflowStrandedRunSweepService"/> would
    ///     read as stranded. And every claimed row is leased at once but renewed only right before its own
    ///     dispatch, so a host holding several multi-minute turns lets the later leases run out while other
    ///     replicas sit idle, and a replica that then claims one of them dispatches it a second time.
    /// </summary>
    public int BatchSize { get; set; } = 1;

    /// <summary>
    ///     Attempts (including the first) before a message is dead-lettered. Matches <c>docs/workflow.md</c>'s own
    ///     worked example. Its backoff is one term of <see cref="WorkflowDispatchTiming.StrandedAfter"/>, so a
    ///     change here moves the stranded-run threshold with it.
    /// </summary>
    public int MaxAttempts { get; set; } = 8;

    /// <summary>The base of the exponential backoff between retries: attempt <c>n</c> waits <c>RetryBaseDelay * 2^(n-1)</c>.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     How long a claim on a workflow dispatch lasts, and how far the renewal right before its dispatch
    ///     extends it. Once it runs out another replica may claim the same message and run the same agent turn
    ///     again, so it must outlast one whole dispatch: the longest dispatch-gate wait plus the turn deadline
    ///     (<c>DetachedRuns:DeadlineSeconds</c>, 300 s today). <see cref="WorkflowDispatchTiming.Validate"/>
    ///     enforces that at registration, and caps it at <see cref="MaxLeaseDuration"/>. Twenty minutes covers a
    ///     five-minute turn behind a ten-minute readiness wait with room to spare. It is also one term of the
    ///     stranded-run threshold: a host that crashes mid-dispatch leaves the message leased, and the run's
    ///     <c>updated_at</c> frozen, until the lease runs out.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    ///     Identifies this process as the holder of the leases it claims, stored in the <c>LockedBy</c> column.
    ///     Unique per process and fixed for its lifetime: the machine name plus a random value. Two processes
    ///     sharing one value would treat each other's leases as their own. At most
    ///     <see cref="MaxHostIdLength"/> characters.
    /// </summary>
    public string HostId { get; set; } = DefaultHostId;

    /// <summary>
    ///     The total time a message spends waiting between attempts before it is dead-lettered: the sum of
    ///     <c>RetryBaseDelay * 2^(n-1)</c> over the <c>MaxAttempts - 1</c> retries. 254 s with today's defaults.
    /// </summary>
    public TimeSpan RetryBackoffWindow
    {
        get
        {
            var total = TimeSpan.Zero;
            for (var retry = 1; retry < MaxAttempts; retry++)
            {
                total += TimeSpan.FromMilliseconds(RetryBaseDelay.TotalMilliseconds * Math.Pow(2, retry - 1));
            }

            return total;
        }
    }

    /// <summary>
    ///     The options <paramref name="section"/> configures, over the defaults on each property. The caller validates
    ///     them with <see cref="WorkflowDispatchTiming.Validate"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The section sets <see cref="HostId"/>, which is per process.</exception>
    internal static WorkflowOutboxDispatchOptions Bind(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);
        if (section[nameof(HostId)] is not null)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(HostId)} must not be configured: it identifies one process's leases, and a value in " +
                "shared configuration would let every replica treat the others' leases as its own.");
        }

        var options = new WorkflowOutboxDispatchOptions();
        section.Bind(options);
        return options;
    }
}
