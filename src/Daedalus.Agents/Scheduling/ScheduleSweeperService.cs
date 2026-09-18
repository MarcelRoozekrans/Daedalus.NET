using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Daedalus.Agents.Scheduling;

/// <summary>
///     Fires the sweep once a minute: claims and enqueues every occurrence that has come due. All the work is in
///     <see cref="ScheduledRunStore"/> — this class owns only the tick and the scope.
/// </summary>
/// <remarks>
///     <para>
///     <b>A plain timer, not <c>ZeroAlloc.Scheduling</c>.</b> The package was evaluated for this trigger and
///     dropped: its EF Core store needs its own <c>SchedulingDbContext</c>, which ships no migrations, and
///     <c>Database.EnsureCreated()</c> is not a substitute for one — it no-ops once the <c>daedalus</c> database
///     already exists (created by <see cref="Daedalus.Infrastructure.Persistence.ApplicationDbContext"/>'s
///     migrations), silently leaving the package's job table missing. None of what the package sells — durable
///     jobs, retries, dead-lettering, a dashboard — is needed here either:
///     <see cref="ScheduledRunStore.ClaimAndEnqueueDueAsync"/> is idempotent and <c>ScheduledRuns.NextRunAt</c> is
///     the sweep's only source of truth, so a missed tick is simply picked up by the next one. There is no
///     "run the sweeper" row that would ever need to survive a crash. This is reversible — the package slots back
///     in later alongside the migration work it needs — and is recorded in the roadmap as a phase 1.5 scope
///     change for that reason.
///     </para>
///     <para>
///     <b>A fresh <see cref="IServiceScope"/> per tick is a correctness requirement, not a style choice.</b>
///     <see cref="ScheduledRunStore"/> is scoped and shares its
///     <see cref="Daedalus.Infrastructure.Persistence.ApplicationDbContext"/> with the outbox writer (see that
///     type's own remarks for why); reusing one scope across ticks would carry EF Core's change-tracker state
///     between sweeps.
///     </para>
///     <para>
///     <b>A tick must never let an exception escape.</b> Doing so would stop <see cref="BackgroundService"/>'s
///     loop and end every future sweep, not just the failing one. Since the sweep is idempotent and re-derives
///     everything from <c>NextRunAt</c>, catching, logging, and continuing to the next tick costs nothing beyond
///     the one missed minute — respecting the stopping token so shutdown still terminates promptly.
///     </para>
/// </remarks>
public sealed partial class ScheduleSweeperService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduleSweeperService> _logger;
    private readonly TimeSpan _sweepInterval;

    /// <summary>Production constructor, resolved by DI: the sweep runs once a minute.</summary>
    public ScheduleSweeperService(IServiceScopeFactory scopeFactory, ILogger<ScheduleSweeperService> logger)
        : this(scopeFactory, logger, TimeSpan.FromMinutes(1))
    {
    }

    /// <summary>
    ///     Test-only seam, reached via <c>InternalsVisibleTo</c> from <c>Daedalus.Tests.Integration</c> and
    ///     <c>Daedalus.Tests.Unit</c>: lets a test drive several sweeps in milliseconds instead of waiting on a real one-minute
    ///     <see cref="PeriodicTimer"/> tick. Production always uses the one-minute constructor above — this is
    ///     not an operator-tunable setting, because <c>NextRunAt</c>, not the sweep cadence, is the only thing
    ///     that matters for correctness (see the class remarks).
    /// </summary>
    internal ScheduleSweeperService(IServiceScopeFactory scopeFactory, ILogger<ScheduleSweeperService> logger, TimeSpan sweepInterval)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _sweepInterval = sweepInterval;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_sweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await SweepAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<ScheduledRunStore>();

            var fired = await store.ClaimAndEnqueueDueAsync(ct).ConfigureAwait(false);
            if (fired > 0) { LogFired(_logger, fired); }
        }
        // A normal host shutdown cancels ct mid-sweep; that is not a sweep failure and must propagate so the
        // BackgroundService loop above stops promptly instead of logging a spurious error on the way out.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSweepFailed(_logger, ex);
        }
    }

    [LoggerMessage(EventId = 456, Level = LogLevel.Information,
        Message = "Sweep claimed and enqueued {Count} due scheduled run(s).")]
    private static partial void LogFired(ILogger logger, int count);

    [LoggerMessage(EventId = 457, Level = LogLevel.Error,
        Message = "Scheduled run sweep failed; the next tick will retry — NextRunAt is the source of truth, so nothing is lost.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);
}
