using System.Data.Common;
using System.Diagnostics;
using Daedalus.Agents.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Integration tests for <see cref="ScheduleSweeperService"/> against a real PostgreSQL database. Placed here
///     rather than in <c>Daedalus.Tests.Unit</c> because <see cref="ScheduledRunStore.ClaimAndEnqueueDueAsync"/> —
///     the only thing this service calls — opens a real transaction and relies on Postgres's <c>xmin</c> system
///     column, neither of which the EF Core InMemory provider supports; there is no way to exercise even the
///     "nothing is due" path without a relational database. What these tests cover is deliberately narrow: that
///     the service delegates to the store, logs only when something actually fired, and — the one behaviour that
///     is this class's own, not the store's — that a tick which throws is caught and logged rather than being
///     allowed to end every future sweep. <c>ScheduledRunStoreTests</c> already covers the store's own claim
///     logic exhaustively; these tests do not repeat that.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduleSweeperServiceTests(PostgresFixture fixture) : IAsyncLifetime
{
    // Real time, deliberately short: the service's own internal-only constructor (see its remarks) accepts this
    // instead of production's one minute, so a test waits milliseconds instead of a real minute for a tick.
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(10);

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
    public async Task A_due_run_is_claimed_and_the_fired_count_is_logged()
    {
        await SeedDueRunAsync("daily-digest");
        var logger = new RecordingLogger();
        var provider = BuildProvider();
        var service = new ScheduleSweeperService(provider.GetRequiredService<IServiceScopeFactory>(), logger, _sweepInterval);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(async () => (await OutboxRowsAsync()).Count == 1);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        (await LoadAsync("daily-digest")).NextRunAt.Should().NotBe(default, "the store advances NextRunAt on the same claim that wrote the outbox row");
        logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains('1'),
            "exactly one run fired, and the service must log that count");
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Ticks_with_nothing_due_log_nothing()
    {
        // No seeded ScheduledRun at all.
        var logger = new RecordingLogger();
        var provider = BuildProvider();
        var service = new ScheduleSweeperService(provider.GetRequiredService<IServiceScopeFactory>(), logger, _sweepInterval);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // No positive condition to wait for (nothing ever happens), so instead wait long enough for several
            // ticks to have definitely occurred at this interval before asserting the negative.
            await Task.Delay(_sweepInterval * 10);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        logger.Entries.Should().BeEmpty("a sweep that claims nothing must not log anything — only a fired count is worth a line");
    }

    [Fact]
    public async Task A_tick_that_throws_is_caught_logged_and_does_not_stop_future_sweeps()
    {
        // Every claim finds the same due row again (its transaction rolls back on the throw below, so NextRunAt
        // never advances), which is exactly what proves the service keeps ticking after a failure: a second
        // error only appears if the loop survived the first one.
        await SeedDueRunAsync("daily-digest");
        var logger = new RecordingLogger();
        var provider = BuildProvider(wrapWriter: _ => new AlwaysThrowingOutboxWriter());
        var service = new ScheduleSweeperService(provider.GetRequiredService<IServiceScopeFactory>(), logger, _sweepInterval);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => Task.FromResult(logger.Entries.Where(e => e.Level == LogLevel.Error).Skip(1).Any()));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Information,
            "every attempt failed before the store could ever report a fired count");
        (await OutboxRowsAsync()).Should().BeEmpty("the failing write never committed, so no trigger row should exist");
    }

    private ServiceProvider BuildProvider(Func<IOutboxWriter<ScheduledRunDue>, IOutboxWriter<ScheduledRunDue>>? wrapWriter = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddDbContextPool<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddOutbox(o => { })
            .WithEfCore<ApplicationDbContext>()
            .AddScheduledRunDueOutbox();
        services.AddScoped<ScheduledRunStore>();

        if (wrapWriter is not null)
        {
            // Registered after AddScheduledRunDueOutbox's own AddTransient: DI resolves the last registration
            // for a service type, so this wins — the same pattern ScheduledRunStoreTests uses.
            services.AddTransient<IOutboxWriter<ScheduledRunDue>>(sp =>
                wrapWriter(new ScheduledRunDueOutboxWriter(sp.GetRequiredService<IOutboxStore>(), sp.GetRequiredService<IOutboxSerializer>())));
        }

        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        return provider;
    }

    private async Task SeedDueRunAsync(string name)
    {
        var run = ScheduledRun.Create(
            name, "0 7 * * *", "RepoDigestSaga", "telegram", "482910337",
            $"schedule:{name}", ["reader", "digest"], ScheduleOrigin.Config, DateTime.UtcNow.AddMinutes(-5)).Value;

        await using var db = fixture.CreateDbContext();
        db.ScheduledRuns.Add(run);
        await db.SaveChangesAsync();
    }

    private async Task<ScheduledRun> LoadAsync(string name)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ScheduledRuns.AsNoTracking().SingleAsync(r => r.Name == name);
    }

    private async Task<List<OutboxMessageEntity>> OutboxRowsAsync()
    {
        var typeName = typeof(ScheduledRunDue).FullName;
        await using var db = fixture.CreateDbContext();
        return await db.OutboxMessages.AsNoTracking().Where(m => m.TypeName == typeName).ToListAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < _waitTimeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Condition was not met within {_waitTimeout}.");
    }

    /// <summary>Fails every write, simulating a step whose underlying database call is permanently broken.</summary>
    private sealed class AlwaysThrowingOutboxWriter : IOutboxWriter<ScheduledRunDue>
    {
        public ValueTask WriteAsync(ScheduledRunDue message, DbTransaction? transaction = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated permanent outbox write failure.");
    }

    /// <summary>
    ///     A minimal <see cref="ILogger{TCategoryName}"/> that records every entry's level and formatted message,
    ///     precise enough to assert exactly which <c>[LoggerMessage]</c> line fired without depending on the
    ///     source generator's private state-holder type.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ScheduleSweeperService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
