using System.Data.Common;
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
///     test (<see cref="A_failure_writing_the_outbox_leaves_NextRunAt_untouched"/>) is the reason this store exists
///     in this form.
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
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().ContainSingle();
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
        (await OutboxRowsAsync<ScheduledRunDue>()).Should().ContainSingle();
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
    ///     A store resolved from its own scope, wired exactly like production: the real generated
    ///     <c>IOutboxWriter&lt;ScheduledRunDue&gt;</c> shares the scope's single <see cref="ApplicationDbContext"/>
    ///     with the store itself, so <c>ScheduledRunStore</c>'s transaction actually enlists the outbox write.
    /// </summary>
    private ScheduledRunStore StoreWith(FakeTimeProvider time) => ResolveStore(time, wrapWriterToFailAfterRealWrite: false);

    /// <summary>
    ///     A store built the same real way as <see cref="StoreWith"/>, except the outbox write is wrapped so it
    ///     throws immediately after the real writer has already enlisted its insert in the ambient transaction and
    ///     flushed it — see <see cref="ThrowAfterRealWriteOutboxWriter"/>. This is deliberately not a fake that
    ///     skips the real write path: the point of this test is that the real write, having genuinely joined the
    ///     transaction, still rolls back when the sweep never reaches <c>CommitAsync</c>.
    /// </summary>
    private ScheduledRunStore StoreWithFailingOutbox(FakeTimeProvider time) => ResolveStore(time, wrapWriterToFailAfterRealWrite: true);

    private ScheduledRunStore ResolveStore(FakeTimeProvider time, bool wrapWriterToFailAfterRealWrite)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddDbContextPool<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddOutbox(o => { })
            .WithEfCore<ApplicationDbContext>()
            .AddScheduledRunDueOutbox();
        services.AddScoped<ScheduledRunStore>();

        if (wrapWriterToFailAfterRealWrite)
        {
            // Registered after AddScheduledRunDueOutbox's own AddTransient: DI resolves the last registration for
            // a service type, so this wins - the same pattern ChannelOutboxTests uses to override the default
            // dispatcher.
            services.AddTransient<IOutboxWriter<ScheduledRunDue>>(sp =>
                new ThrowAfterRealWriteOutboxWriter(
                    new ScheduledRunDueOutboxWriter(sp.GetRequiredService<IOutboxStore>(), sp.GetRequiredService<IOutboxSerializer>())));
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
}
