using System.Data.Common;
using System.Text.Json;
using Daedalus.Agents.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Integration tests for <see cref="ScheduledRunStore.ClaimAndEnqueueDueAsync"/> against a real PostgreSQL
///     database: the atomic claim that advances a due row's <c>NextRunAt</c> and enqueues its
///     <see cref="ScheduledRunDue"/> trigger in the same transaction. Every test resolves the store the way
///     production now must — from its own <see cref="IServiceScope"/>, with the real ZeroAlloc.Outbox-generated
///     writer sharing that scope's <see cref="ApplicationDbContext"/> — because <see cref="ScheduledRunStore"/> is
///     scoped, not a singleton over <c>IDbContextFactory</c>; see the remarks on the store itself for why. The last
///     three tests are the reason this store exists in this exact form: one-transaction-per-sweep with genuinely
///     more than one row at risk, the impossible-cron escape hatch, and the <c>xmin</c> concurrency race.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunStoreTests(PostgresFixture fixture) : IAsyncLifetime
{
    // Every seeded schedule uses the same "07:00 UTC daily" cron; only the fake clock's current instant changes
    // between tests to move a row in and out of "due".
    private static readonly DateTime _seedDay = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_due_row_advances_and_enqueues_exactly_one_trigger()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));

        var fired = await StoreWith(time).ClaimAndEnqueueDueAsync(default);

        fired.Should().Be(1);
        (await LoadAsync("daily-digest")).NextRunAt.Should().Be(At(7, 0).AddDays(1));
        var rows = await OutboxRowsAsync<ScheduledRunDue>();
        rows.Should().ContainSingle();
        DeserializePayload(rows[0]).OccurrenceAtUtc.Should().Be(At(7, 0));
    }

    [Fact]
    public async Task A_row_that_is_not_due_is_left_alone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 6, 59, 0, TimeSpan.Zero));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));

        var fired = await StoreWith(time).ClaimAndEnqueueDueAsync(default);

        fired.Should().Be(0);
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_disabled_row_never_fires()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "off", cron: "0 7 * * *", nextRunAt: At(7, 0), enabled: false);

        (await StoreWith(time).ClaimAndEnqueueDueAsync(default)).Should().Be(0);

        (await OutboxRowsAsync<ScheduledRunDue>()).Should().BeEmpty();
        (await LoadAsync("off")).NextRunAt.Should().Be(At(7, 0), "a disabled row is never touched, not even its stale NextRunAt");
    }

    [Fact]
    public async Task Three_missed_days_fire_once_and_record_the_skipped_count()
    {
        // spec D8: yesterday's digest has no value today, and catch-up-all would bill three
        // subagent runs to deliver two documents nobody wants
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 19, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));

        var fired = await StoreWith(time).ClaimAndEnqueueDueAsync(default);

        fired.Should().Be(1);
        var rows = await OutboxRowsAsync<ScheduledRunDue>();
        rows.Should().ContainSingle();
        // The most recent due occurrence (09-19) fires, not the oldest stale one (09-16): firing the oldest while
        // discarding today's would invert D8's own "yesterday's digest has no value today" reasoning.
        DeserializePayload(rows[0]).OccurrenceAtUtc.Should().Be(new DateTime(2026, 9, 19, 7, 0, 0, DateTimeKind.Utc));
        var row = await LoadAsync("daily-digest");
        row.MissedOccurrences.Should().Be(3);
        row.NextRunAt.Should().Be(new DateTime(2026, 9, 20, 7, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task A_failure_writing_the_outbox_leaves_NextRunAt_untouched()
    {
        // the atomicity claim: advance and trigger commit together or not at all
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));
        var store = StoreWithFailingOutbox(time);

        var act = async () => await store.ClaimAndEnqueueDueAsync(default);

        await act.Should().ThrowAsync<Exception>();
        (await LoadAsync("daily-digest")).NextRunAt.Should().Be(At(7, 0),
            "a crash between advancing and enqueueing must not consume the occurrence");
        // An implementation that committed the outbox write on a separate, unshared connection while NextRunAt
        // rolled back would pass the assertion above but leave a spurious trigger behind - this closes that gap.
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_schedule_with_no_future_occurrence_is_disabled_and_does_not_block_other_due_rows()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 7, 0, 5, TimeSpan.Zero));
        // Syntactically valid, semantically impossible: February never has a 30th, so Cronos's GetNextOccurrence
        // returns null forever rather than throwing. DateTime.MaxValue (Kind=Unspecified) cannot be persisted into
        // a timestamptz column, so this row must be disabled rather than "advanced" to a sentinel.
        await SeedAsync(name: "impossible-cron", cron: "0 0 30 2 *", nextRunAt: At(7, 0));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));

        var fired = await StoreWith(time).ClaimAndEnqueueDueAsync(default);

        // The critical clause: one impossible row does not prevent the other due row in the same sweep from firing.
        fired.Should().Be(1);
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().ContainSingle();
        (await LoadAsync("impossible-cron")).Enabled.Should().BeFalse();
        (await LoadAsync("daily-digest")).NextRunAt.Should().Be(At(7, 0).AddDays(1));
    }

    [Fact]
    public async Task A_failure_on_one_row_rolls_back_every_row_in_the_sweep()
    {
        // The brief's own justification for one-transaction-per-sweep, verbatim: "if the outbox write throws on
        // row three, rows one and two must roll back too." A single-row sweep can never distinguish this
        // implementation from a per-row-transaction one - only a multi-row sweep can.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "row-one", cron: "0 7 * * *", nextRunAt: At(7, 0));
        await SeedAsync(name: "row-two", cron: "0 7 * * *", nextRunAt: At(7, 0));
        await SeedAsync(name: "row-three", cron: "0 7 * * *", nextRunAt: At(7, 0));
        var store = StoreWithFailingOutboxOnNthWrite(time, failOnCallNumber: 3);

        var act = async () => await store.ClaimAndEnqueueDueAsync(default);

        await act.Should().ThrowAsync<Exception>();
        (await LoadAsync("row-one")).NextRunAt.Should().Be(At(7, 0));
        (await LoadAsync("row-two")).NextRunAt.Should().Be(At(7, 0));
        (await LoadAsync("row-three")).NextRunAt.Should().Be(At(7, 0));
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().BeEmpty();
    }

    [Fact]
    public async Task Two_concurrent_sweeps_of_the_same_due_row_have_exactly_one_winner()
    {
        // ScheduledRun carries an xmin concurrency token (ScheduledRunConfiguration), so two sweepers racing the
        // same due row are already mutually exclusive without any explicit row lock. A rendezvous forces both
        // sweeps to have already read the row and be about to write before either is allowed to proceed, so the
        // two really race for the same row's xmin instead of running one after the other.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 16, 7, 0, 5, TimeSpan.Zero));
        await SeedAsync(name: "daily-digest", cron: "0 7 * * *", nextRunAt: At(7, 0));

        var aReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storeA = StoreThatRendezvousesBeforeWriting(time, aReady, bReady.Task);
        var storeB = StoreThatRendezvousesBeforeWriting(time, bReady, aReady.Task);

        var results = await Task.WhenAll(
            storeA.ClaimAndEnqueueDueAsync(default).AsTask(),
            storeB.ClaimAndEnqueueDueAsync(default).AsTask());

        results.Order().Should().Equal([0, 1], "exactly one sweeper claims the row; the loser yields, it does not throw");
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().ContainSingle();
        (await LoadAsync("daily-digest")).NextRunAt.Should().Be(At(7, 0).AddDays(1));
    }

    /// <summary>The seed day (2026-09-16, UTC) at the given time of day.</summary>
    private static DateTime At(int hour, int minute) => _seedDay.AddHours(hour).AddMinutes(minute);

    private async Task SeedAsync(string name, string cron, DateTime nextRunAt, bool enabled = true)
    {
        var run = ScheduledRun.Create(
            name, cron, "RepoDigestSaga", "telegram", "482910337",
            $"schedule:{name}", ["reader", "digest"], ScheduleOrigin.Config, nextRunAt).Value;

        if (!enabled)
        {
            run.Disable();
        }

        await using var db = fixture.CreateDbContext();
        db.ScheduledRuns.Add(run);
        await db.SaveChangesAsync();
    }

    private async Task<ScheduledRun> LoadAsync(string name)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ScheduledRuns.AsNoTracking().SingleAsync(r => r.Name == name);
    }

    /// <summary>
    ///     Rows in the shared <c>OutboxMessages</c> table (see <see cref="ApplicationDbContext.OutboxMessages"/>)
    ///     whose type-name column matches <typeparamref name="T"/>'s full name — the same key ZeroAlloc.Outbox's
    ///     generated writer stamps every row with.
    /// </summary>
    private async Task<List<OutboxMessageEntity>> OutboxRowsAsync<T>()
    {
        var typeName = typeof(T).FullName;
        await using var db = fixture.CreateDbContext();
        return await db.OutboxMessages.AsNoTracking().Where(m => m.TypeName == typeName).ToListAsync();
    }

    /// <summary>
    ///     Case-insensitive: the outbox serializer camelCases its JSON, and matching only PascalCase would
    ///     silently leave every property at its default (e.g. OccurrenceAtUtc == default(DateTime)) rather than
    ///     failing loudly.
    /// </summary>
    private static readonly JsonSerializerOptions _payloadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Deserializes an outbox row's payload back into the message it carries, for asserting on its fields.</summary>
    private static ScheduledRunDue DeserializePayload(OutboxMessageEntity row) =>
        JsonSerializer.Deserialize<ScheduledRunDue>(row.Payload, _payloadOptions)!;

    /// <summary>
    ///     A store resolved from its own scope, wired exactly like production: the real generated
    ///     <c>IOutboxWriter&lt;ScheduledRunDue&gt;</c> shares the scope's single <see cref="ApplicationDbContext"/>
    ///     with the store itself, so <c>ScheduledRunStore</c>'s transaction actually enlists the outbox write.
    /// </summary>
    private ScheduledRunStore StoreWith(FakeTimeProvider time) => ResolveStore(time);

    /// <summary>
    ///     A store built the same real way as <see cref="StoreWith"/>, except the outbox write is wrapped so it
    ///     throws immediately after the real writer has already enlisted its insert in the ambient transaction and
    ///     flushed it — see <see cref="ThrowAfterRealWriteOutboxWriter"/>. This is deliberately not a fake that
    ///     skips the real write path: the point of this test is that the real write, having genuinely joined the
    ///     transaction, still rolls back when the sweep never reaches <c>CommitAsync</c>.
    /// </summary>
    private ScheduledRunStore StoreWithFailingOutbox(FakeTimeProvider time) =>
        ResolveStore(time, (real, _) => new ThrowAfterRealWriteOutboxWriter(real));

    /// <summary>
    ///     Like <see cref="StoreWithFailingOutbox"/>, except every write up to and including <paramref
    ///     name="failOnCallNumber"/> genuinely happens (real insert, real flush) and only the call numbered
    ///     <paramref name="failOnCallNumber"/> throws afterward — for proving a multi-row sweep rolls back rows
    ///     that had already been durably staged earlier in the same sweep.
    /// </summary>
    private ScheduledRunStore StoreWithFailingOutboxOnNthWrite(FakeTimeProvider time, int failOnCallNumber) =>
        ResolveStore(time, (real, _) => new ThrowOnNthWriteOutboxWriter(real, failOnCallNumber));

    /// <summary>
    ///     A store whose outbox write signals <paramref name="mySignal"/> and awaits <paramref name="otherSignal"/>
    ///     <em>before</em> performing the real write — see <see cref="RendezvousOutboxWriter"/> for why it must be
    ///     before, not after. Used in pairs so two concurrent sweeps both reach the point just after reading the
    ///     due row and are both about to write before either is allowed to proceed, forcing them to genuinely race
    ///     for the same row's <c>xmin</c> instead of merely running one after the other.
    /// </summary>
    private ScheduledRunStore StoreThatRendezvousesBeforeWriting(FakeTimeProvider time, TaskCompletionSource mySignal, Task otherSignal) =>
        ResolveStore(time, (real, _) => new RendezvousOutboxWriter(real, mySignal, otherSignal));

    private ScheduledRunStore ResolveStore(
        FakeTimeProvider time,
        Func<IOutboxWriter<ScheduledRunDue>, IServiceProvider, IOutboxWriter<ScheduledRunDue>>? wrapWriter = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddDbContextPool<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddOutbox(o => { })
            .WithEfCore<ApplicationDbContext>()
            .AddScheduledRunDueOutbox();
        services.AddScoped<ScheduledRunStore>();

        if (wrapWriter is not null)
        {
            // Registered after AddScheduledRunDueOutbox's own AddTransient: DI resolves the last registration for
            // a service type, so this wins - the same pattern ChannelOutboxTests uses to override the default
            // dispatcher.
            services.AddTransient<IOutboxWriter<ScheduledRunDue>>(sp =>
                wrapWriter(
                    new ScheduledRunDueOutboxWriter(sp.GetRequiredService<IOutboxStore>(), sp.GetRequiredService<IOutboxSerializer>()),
                    sp));
        }

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        var scope = provider.CreateAsyncScope();
        _disposables.Add(scope);
        return scope.ServiceProvider.GetRequiredService<ScheduledRunStore>();
    }

    /// <summary>
    ///     Delegates to the real, DI-resolved <see cref="ScheduledRunDueOutboxWriter"/> (accessible here via
    ///     <c>InternalsVisibleTo</c>) so the underlying insert genuinely enlists in the ambient transaction and is
    ///     flushed to Postgres, then throws - simulating a crash that happens after the write but before the
    ///     sweep's own commit. This proves the rollback undoes a write that really happened, not one that never
    ///     touched the database.
    /// </summary>
    private sealed class ThrowAfterRealWriteOutboxWriter(IOutboxWriter<ScheduledRunDue> inner) : IOutboxWriter<ScheduledRunDue>
    {
        public async ValueTask WriteAsync(ScheduledRunDue message, DbTransaction? transaction = null, CancellationToken ct = default)
        {
            await inner.WriteAsync(message, transaction, ct);
            throw new InvalidOperationException(
                "Simulated failure immediately after the real outbox write enlisted in the ambient transaction.");
        }
    }

    /// <summary>Like <see cref="ThrowAfterRealWriteOutboxWriter"/>, but only the Nth call throws; earlier calls genuinely succeed.</summary>
    private sealed class ThrowOnNthWriteOutboxWriter(IOutboxWriter<ScheduledRunDue> inner, int failOnCallNumber) : IOutboxWriter<ScheduledRunDue>
    {
        private int _calls;

        public async ValueTask WriteAsync(ScheduledRunDue message, DbTransaction? transaction = null, CancellationToken ct = default)
        {
            var callNumber = Interlocked.Increment(ref _calls);
            await inner.WriteAsync(message, transaction, ct);
            if (callNumber == failOnCallNumber)
            {
                throw new InvalidOperationException(
                    $"Simulated failure after the real outbox write for call #{callNumber} enlisted in the ambient transaction.");
            }
        }
    }

    /// <summary>
    ///     Signals readiness and waits for the paired writer's own signal BEFORE performing the real write - see
    ///     the remarks inside <see cref="WriteAsync"/> for why the ordering matters.
    /// </summary>
    private sealed class RendezvousOutboxWriter(IOutboxWriter<ScheduledRunDue> inner, TaskCompletionSource mySignal, Task otherSignal)
        : IOutboxWriter<ScheduledRunDue>
    {
        public async ValueTask WriteAsync(ScheduledRunDue message, DbTransaction? transaction = null, CancellationToken ct = default)
        {
            // Signal readiness and wait for the other side BEFORE the real write, not after: EfCoreOutboxStore
            // shares this sweep's own DbContext (see ScheduledRunStore's remarks), so its internal SaveChangesAsync
            // flushes the already-mutated ScheduledRun row too - the row lock is acquired inside inner.WriteAsync,
            // not at the sweep's later explicit SaveChangesAsync. Rendezvousing after the write would have the
            // loser block on Postgres's row lock INSIDE inner.WriteAsync, unable to ever reach the signal the
            // winner is waiting on - a deadlock. Rendezvousing first guarantees both sides have already read the
            // row and are about to write before either takes the lock, so they race for real.
            mySignal.TrySetResult();
            await otherSignal;
            await inner.WriteAsync(message, transaction, ct);
        }
    }
}
