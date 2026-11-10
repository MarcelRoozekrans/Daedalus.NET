using AwesomeAssertions.Equivalency;
using Daedalus.Agents.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using ZeroAlloc.ORM;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Phase 3.5, Task 19: <see cref="ScheduledRunStore"/> and <see cref="ScheduleReconciler"/> on ZeroAlloc.ORM store
///     and load every column of a schedule, load a row whatever <see cref="ScheduledRun.Create"/> would say of it
///     today, and keep EF Core's <c>xmin</c> guard on every write: a sweep that loses a race rolls back its outbox
///     rows and claims nothing, and a reconcile that loses one throws and rolls back everything it wrote.
/// </summary>
/// <remarks>
///     Every race holds the winner's update open in a transaction of its own until the loser's guarded update waits
///     on the row lock, then commits it; so the loser has read the row, and done every earlier write, before the
///     winner's change lands, and it is the loser's later write that fails.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunStoreRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _seven = new(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTimeOffset _afterSeven = new(2026, 9, 16, 7, 0, 5, TimeSpan.Zero);

    private readonly List<IAsyncDisposable> _disposables = [];

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>
    ///     A schedule stored through the SQL class, advanced once, with two roles and a repository, comes back equal in
    ///     every property with UTC times, through the reconciler's read and the sweep's; the roles are stored
    ///     comma-joined and the origin by name, as EF Core stored them. Red, shown once: read <c>"ConversationId"</c>
    ///     into <c>ChannelId</c> in <c>ListConfigQuery</c>, and the comparison names <c>ChannelId</c>; split the roles
    ///     in reverse, and the strictly ordered comparison names <c>Roles</c>.
    /// </summary>
    [Fact]
    public async Task Stored_schedule_loads_back_whole()
    {
        var run = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337", "schedule:daily-digest",
            ["reader", "digest"], ScheduleOrigin.Config, _seven, "owner/repo").Value;
        run.AdvanceTo(_seven.AddDays(1), _seven, missed: 2);

        await using (var connection = await fixture.Db.OpenAsync(CancellationToken.None))
        {
            await using var tx = await connection.BeginTransactionAsync(CancellationToken.None);
            await new ScheduledRunSql(connection).InsertAsync(run, tx, CancellationToken.None);
            await tx.CommitAsync(CancellationToken.None);
        }

        var configured = await ListConfigAsync();
        var due = await ListDueAsync(_seven.AddDays(1));

        configured.Should().ContainSingle().Which.Run.Should().BeEquivalentTo(run, Members);
        due.Should().ContainSingle().Which.Run.Should().BeEquivalentTo(run, Members);
        configured[0].Version.Should().NotBe(0u);
        configured[0].Run.NextRunAt.Kind.Should().Be(DateTimeKind.Utc);
        configured[0].Run.LastRunAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
        (await ScalarAsync<string>("""SELECT "Roles" FROM "ScheduledRuns" """)).Should().Be("reader,digest");
        (await ScalarAsync<string>("""SELECT "Origin" FROM "ScheduledRuns" """)).Should().Be("Config");
    }

    /// <summary>
    ///     Review focus 1: a row <see cref="ScheduledRun.Create"/> refuses, a blank cron and no roles, with every other
    ///     column set, still loads as written. Red: split the stored roles without removing empty entries, and the
    ///     roles load as one empty string.
    /// </summary>
    [Fact]
    public async Task A_row_Create_rejects_still_loads()
    {
        ScheduledRun.Create("legacy", " ", "RepoDigest", "telegram", "1", "p", [], ScheduleOrigin.Config, _seven)
            .IsFailure.Should().BeTrue("a blank cron and no roles are what Create refuses");
        var id = Guid.NewGuid();
        await ExecuteAsync(
            """
            INSERT INTO "ScheduledRuns"
                ("Id", "Name", "Cron", "Trigger", "ChannelId", "ConversationId", "PrincipalId", "Roles", "Origin",
                 "NextRunAt", "LastRunAt", "Enabled", "MissedOccurrences", "Repository")
            VALUES (@id, 'legacy', ' ', 'RepoDigest', 'telegram', '1', 'p', '', 'Config', @next, NULL, TRUE, 4, NULL)
            """,
            ("id", id), ("next", _seven));

        var loaded = (await ListConfigAsync()).Should().ContainSingle().Which.Run;

        loaded.Id.Should().Be(id);
        loaded.Name.Should().Be("legacy");
        loaded.Cron.Should().Be(" ");
        loaded.Roles.Should().BeEmpty();
        loaded.Repository.Should().BeNull();
        loaded.LastRunAt.Should().BeNull();
        loaded.Enabled.Should().BeTrue();
        loaded.MissedOccurrences.Should().Be(4);
        loaded.NextRunAt.Should().Be(_seven);
    }

    /// <summary>
    ///     A sweep's guarded advance that loses the race to a concurrent write of its row rolls back the trigger it
    ///     already enqueued and claims nothing; the winner's values stand. Red: drop <c>Sql.Xmin(r) == version</c>
    ///     from <c>AdvanceQuery</c>; the sweep claims the row, overwrites the winner and leaves a trigger behind.
    /// </summary>
    [Fact]
    public async Task Enqueue_rolls_back_with_a_lost_race()
    {
        await InsertAsync(Schedule("daily-digest", "0 7 * * *"));
        await using var winner = await HoldUpdateAsync(
            """UPDATE "ScheduledRuns" SET "NextRunAt" = @at, "MissedOccurrences" = 9 WHERE "Name" = 'daily-digest'""",
            _seven.AddDays(5));

        var sweep = Store().ClaimAndEnqueueDueAsync(CancellationToken.None).AsTask();
        await WaitUntilAScheduleUpdateWaitsOnALockAsync();
        await winner.CommitAsync();
        var claimed = await sweep;

        claimed.Should().Be(0, "the loser yields this tick; it does not throw");
        (await OutboxRowCountAsync()).Should().Be(0, "the trigger enqueued before the lost update rolls back with it");
        var stored = (await ListConfigAsync()).Single().Run;
        stored.NextRunAt.Should().Be(_seven.AddDays(5));
        stored.MissedOccurrences.Should().Be(9);
    }

    /// <summary>
    ///     A sweep's guarded disable of a cron with no future occurrence that loses the race rolls back the whole sweep:
    ///     a second due row with a valid cron is not claimed and leaves no trigger, and the winner's values stand, the
    ///     row still enabled. Red: drop <c>Sql.Xmin(r) == version</c> from <c>DisableQuery</c>; the sweep claims the
    ///     valid row and the impossible one ends disabled.
    /// </summary>
    [Fact]
    public async Task Disable_loses_the_race_and_the_sweep_claims_nothing()
    {
        await InsertAsync(Schedule("impossible-cron", "0 0 30 2 *"));
        await InsertAsync(Schedule("daily-digest", "0 7 * * *"));
        await using var winner = await HoldUpdateAsync(
            """UPDATE "ScheduledRuns" SET "NextRunAt" = @at WHERE "Name" = 'impossible-cron'""", _seven.AddMinutes(1));

        var sweep = Store().ClaimAndEnqueueDueAsync(CancellationToken.None).AsTask();
        await WaitUntilAScheduleUpdateWaitsOnALockAsync();
        await winner.CommitAsync();
        var claimed = await sweep;

        claimed.Should().Be(0, "the lost disable rolls back the valid row's claim with the rest of the sweep");
        (await OutboxRowCountAsync()).Should().Be(0);
        var stored = (await ListConfigAsync()).Single(r => string.Equals(r.Run.Name, "impossible-cron", StringComparison.Ordinal)).Run;
        stored.Enabled.Should().BeTrue("the disable lost the race, so it wrote nothing");
        stored.NextRunAt.Should().Be(_seven.AddMinutes(1));
        (await ListConfigAsync()).Single(r => string.Equals(r.Run.Name, "daily-digest", StringComparison.Ordinal)).Run.NextRunAt.Should().Be(_seven);
    }

    /// <summary>
    ///     Owner, 2026-11-05: the reconciler keeps the guard EF Core gave it, so a config update that loses the race
    ///     throws out of <see cref="ScheduleReconciler.ReconcileAsync"/>, and the schedule it inserted first rolls back
    ///     with it; the winner's values stand. Red: drop <c>Sql.Xmin(r) == version</c> from <c>UpdateConfigQuery</c>;
    ///     the reconcile succeeds and overwrites the conversation.
    /// </summary>
    [Fact]
    public async Task Reconcile_update_loses_the_race_and_rolls_back_the_whole_reconcile()
    {
        await Reconciler([Entry("daily-digest", "482910337")]).ReconcileAsync(CancellationToken.None);
        await using var winner = await HoldUpdateAsync(
            """UPDATE "ScheduledRuns" SET "NextRunAt" = @at, "MissedOccurrences" = 9 WHERE "Name" = 'daily-digest'""",
            _seven.AddDays(5));

        var reconcile = Reconciler([Entry("weekly", "1"), Entry("daily-digest", "999999999")])
            .ReconcileAsync(CancellationToken.None).AsTask();
        await WaitUntilAScheduleUpdateWaitsOnALockAsync();
        await winner.CommitAsync();
        var act = async () => await reconcile;

        await act.Should().ThrowAsync<ZeroAllocOrmConcurrencyException>();
        var stored = (await ListConfigAsync()).Should().ContainSingle("the schedule inserted first rolled back").Which.Run;
        stored.Name.Should().Be("daily-digest");
        stored.ConversationId.Should().Be("482910337");
        stored.MissedOccurrences.Should().Be(9);
    }

    /// <summary>
    ///     A reconcile whose disable of a schedule gone from configuration loses the race throws, and the schedule it
    ///     inserted first rolls back with it; the row stays enabled. Red: drop <c>Sql.Xmin(r) == version</c> from
    ///     <c>DisableQuery</c>; the reconcile succeeds and the row ends disabled.
    /// </summary>
    [Fact]
    public async Task Reconcile_disable_loses_the_race_and_rolls_back_the_whole_reconcile()
    {
        await Reconciler([Entry("daily-digest", "482910337")]).ReconcileAsync(CancellationToken.None);
        await using var winner = await HoldUpdateAsync(
            """UPDATE "ScheduledRuns" SET "NextRunAt" = @at WHERE "Name" = 'daily-digest'""", _seven.AddDays(5));

        var reconcile = Reconciler([Entry("weekly", "1")]).ReconcileAsync(CancellationToken.None).AsTask();
        await WaitUntilAScheduleUpdateWaitsOnALockAsync();
        await winner.CommitAsync();
        var act = async () => await reconcile;

        await act.Should().ThrowAsync<ZeroAllocOrmConcurrencyException>();
        var stored = (await ListConfigAsync()).Should().ContainSingle("the schedule inserted first rolled back").Which.Run;
        stored.Name.Should().Be("daily-digest");
        stored.Enabled.Should().BeTrue();
        stored.NextRunAt.Should().Be(_seven.AddDays(5));
    }

    /// <summary>
    ///     Ruling R116: a reconcile that changes nothing in a schedule writes nothing, as EF Core saved no unchanged
    ///     entity, so its <c>xmin</c> stays and a sweep that read the row is not made to lose a race. Red: write every
    ///     matched schedule unconditionally; the version changes.
    /// </summary>
    [Fact]
    public async Task Reconcile_writes_nothing_for_an_unchanged_schedule()
    {
        await Reconciler([Entry("daily-digest", "482910337")]).ReconcileAsync(CancellationToken.None);
        var before = (await ListConfigAsync()).Single().Version;

        await Reconciler([Entry("daily-digest", "482910337")]).ReconcileAsync(CancellationToken.None);

        (await ListConfigAsync()).Single().Version.Should().Be(before);
    }

    private static Func<EquivalencyOptions<ScheduledRun>, EquivalencyOptions<ScheduledRun>> Members =>
        o => o.ComparingByMembers<ScheduledRun>().WithStrictOrdering();

    private static ScheduledRun Schedule(string name, string cron) =>
        ScheduledRun.Create(
            name, cron, "RepoDigest", "telegram", "482910337", $"schedule:{name}", ["reader"], ScheduleOrigin.Config,
            _seven).Value;

    private static (string Name, string ConversationId) Entry(string name, string conversationId) => (name, conversationId);

    private ScheduleReconciler Reconciler(IReadOnlyList<(string Name, string ConversationId)> entries)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DetachedRuns:PrincipalId"] = "schedule:daedalus",
            ["DetachedRuns:Roles:0"] = "reader",
        };
        for (var i = 0; i < entries.Count; i++)
        {
            settings[$"ScheduledRuns:{i}:Name"] = entries[i].Name;
            settings[$"ScheduledRuns:{i}:Cron"] = "0 7 * * *";
            settings[$"ScheduledRuns:{i}:Trigger"] = "RepoDigest";
            settings[$"ScheduledRuns:{i}:ChannelId"] = "telegram";
            settings[$"ScheduledRuns:{i}:ConversationId"] = entries[i].ConversationId;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new ScheduleReconciler(
            fixture.Db, configuration, new FakeTimeProvider(_afterSeven), NullLogger<ScheduleReconciler>.Instance);
    }

    private ScheduledRunStore Store()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(_afterSeven));
        services.AddChannelOutboxOver(fixture);
        services.AddScoped<ScheduledRunStore>();
        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        var scope = provider.CreateAsyncScope();
        _disposables.Add(scope);
        return scope.ServiceProvider.GetRequiredService<ScheduledRunStore>();
    }

    private async Task InsertAsync(ScheduledRun run)
    {
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        await using var tx = await connection.BeginTransactionAsync(CancellationToken.None);
        await new ScheduledRunSql(connection).InsertAsync(run, tx, CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
    }

    private async Task<IReadOnlyList<VersionedScheduledRun>> ListConfigAsync()
    {
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        await using var tx = await connection.BeginTransactionAsync(CancellationToken.None);
        return await new ScheduledRunSql(connection).ListConfigAsync(tx, CancellationToken.None);
    }

    private async Task<IReadOnlyList<VersionedScheduledRun>> ListDueAsync(DateTime now)
    {
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        await using var tx = await connection.BeginTransactionAsync(CancellationToken.None);
        return await new ScheduledRunSql(connection).ListDueAsync(now, tx, CancellationToken.None);
    }

    /// <summary>Runs <paramref name="sql"/> in a transaction it leaves open, holding the row lock until the commit.</summary>
    private async Task<HeldUpdate> HoldUpdateAsync(string sql, DateTime at)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var tx = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(sql, connection, tx))
        {
            command.Parameters.AddWithValue("at", at);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        return new HeldUpdate(connection, tx);
    }

    private async Task WaitUntilAScheduleUpdateWaitsOnALockAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND query LIKE 'UPDATE "ScheduledRuns"%'
                """,
                connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("No guarded update of a schedule ever waited on the row lock.");
    }

    private async Task<long> OutboxRowCountAsync() =>
        await ScalarAsync<long>(
            $"""SELECT count(*) FROM "OutboxMessages" WHERE "TypeName" = '{typeof(ScheduledRunDue).FullName}'""");

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The winner's open transaction and its connection; disposing rolls back what was not committed.</summary>
    private sealed class HeldUpdate(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        public Task CommitAsync() => transaction.CommitAsync();

        public async ValueTask DisposeAsync()
        {
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
