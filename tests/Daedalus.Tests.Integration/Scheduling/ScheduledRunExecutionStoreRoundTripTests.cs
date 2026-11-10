using AwesomeAssertions.Equivalency;
using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRunExecutions;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Phase 3.5, Task 20: <see cref="ScheduledRunExecutionStore"/> on ZeroAlloc.ORM stores and loads every column of
///     an execution, loads a row whatever <see cref="ScheduledRunExecution.Create"/> would say of it today, and keeps
///     each write in the transaction of its outbox enqueue: a claim whose commit fails leaves neither row, and an
///     advance or a failure that loses the <c>xmin</c> race rolls back the message it already enqueued.
/// </summary>
/// <remarks>
///     Every race holds the winner's update open in a transaction of its own until the loser's guarded update waits
///     on the row lock, then commits it; so the loser has read the row, and enqueued its message, before the winner's
///     change lands, and it is the loser's later write that fails.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunExecutionStoreRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _occurrence = new(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 17, 7, 0, 5, TimeSpan.Zero));
    private readonly List<IAsyncDisposable> _disposables = [];
    private Guid _scheduleId;

    public async Task InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        var schedule = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337", "schedule:daily-digest",
            ["reader", "digest"], ScheduleOrigin.Config, _occurrence, "owner/repo").Value;
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        await using var tx = await connection.BeginTransactionAsync(CancellationToken.None);
        await new ScheduledRunSql(connection).InsertAsync(schedule, tx, CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);
        _scheduleId = schedule.Id;
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            await disposable.DisposeAsync();
        }
    }

    /// <summary>
    ///     An execution claimed, advanced twice and failed through the store, each write a minute after the last, comes
    ///     back equal in every property with UTC times; the roles are stored comma-joined and the steps by name, as EF
    ///     Core stored them. Red, shown once: read <c>"Digest"</c> into <c>Findings</c> in <c>FindRowQuery</c>, and the
    ///     comparison names <c>Findings</c>; bind <c>0</c> for the attempts <c>FailAsync</c> writes, and it names
    ///     <c>Attempts</c>.
    /// </summary>
    [Fact]
    public async Task Stored_execution_loads_back_whole()
    {
        var store = Store();
        var created = _time.GetUtcNow().UtcDateTime;
        (await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, _occurrence), default)).Should().BeTrue();
        var id = (await ExecutionIdsAsync()).Single();
        _time.Advance(TimeSpan.FromMinutes(1));
        (await store.TryCompleteScoutAsync(id, "three open PRs", default)).Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(1));
        (await store.TryCompleteWriterAsync(id, "Here is your digest.", default)).Should().BeTrue();
        _time.Advance(TimeSpan.FromMinutes(1));
        await store.FailAsync(id, "channel adapter unreachable", default);

        var loaded = await store.FindAsync(id, default);

        var expected = ScheduledRunExecution.Rehydrate(
            id, _scheduleId, _occurrence, RunStep.Failed, RunStep.Deliver, "three open PRs", "Here is your digest.",
            "telegram", "482910337", "schedule:daily-digest", ["reader", "digest"], attempts: 1,
            lastError: "channel adapter unreachable", createdAt: created, updatedAt: created.AddMinutes(3));
        loaded.Should().BeEquivalentTo(expected, Members);
        loaded!.OccurrenceAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
        (await ScalarAsync<string>("""SELECT "Roles" FROM "ScheduledRunExecutions" """)).Should().Be("reader,digest");
        (await ScalarAsync<string>("""SELECT "Step" FROM "ScheduledRunExecutions" """)).Should().Be("Failed");
        (await ScalarAsync<string>("""SELECT "FailedAtStep" FROM "ScheduledRunExecutions" """)).Should().Be("Deliver");
    }

    /// <summary>
    ///     Review focus 1: a row <see cref="ScheduledRunExecution.Create"/> refuses, a blank principal and no roles,
    ///     loads as written, with its nulls. Red: split the stored roles without removing empty entries, and the roles
    ///     load as one empty string.
    /// </summary>
    [Fact]
    public async Task A_row_Create_rejects_still_loads()
    {
        ScheduledRunExecution.Create(_scheduleId, _occurrence, "telegram", "1", " ", [], _occurrence)
            .IsFailure.Should().BeTrue("a blank principal and no roles are what Create refuses");
        var id = Guid.CreateVersion7();
        await ExecuteAsync(
            """
            INSERT INTO "ScheduledRunExecutions"
                ("Id", "ScheduleId", "OccurrenceAt", "Step", "FailedAtStep", "Findings", "Digest", "ChannelId",
                 "ConversationId", "PrincipalId", "Roles", "Attempts", "LastError", "CreatedAt", "UpdatedAt")
            VALUES (@id, @scheduleId, @at, 'Writer', NULL, 'f', NULL, 'telegram', '1', ' ', '', 0, NULL, @at, @at)
            """,
            ("id", id), ("scheduleId", _scheduleId), ("at", _occurrence));

        var loaded = await Store().FindAsync(id, default);

        loaded.Should().NotBeNull();
        loaded!.Id.Should().Be(id);
        loaded.Step.Should().Be(RunStep.Writer);
        loaded.FailedAtStep.Should().BeNull();
        loaded.Findings.Should().Be("f");
        loaded.Digest.Should().BeNull();
        loaded.PrincipalId.Should().Be(" ");
        loaded.Roles.Should().BeEmpty();
        loaded.LastError.Should().BeNull();
        loaded.OccurrenceAt.Should().Be(_occurrence);
    }

    /// <summary>
    ///     The claim's insert and its scout step commit together: when the commit fails, after both were written, neither
    ///     the execution nor the step is left. A deferred trigger on the execution table fails the commit. Red: hand the
    ///     scout writer <see langword="null"/> instead of the claim's transaction; the step commits on its own and stays.
    /// </summary>
    [Fact]
    public async Task Begin_rolls_back_its_enqueue_when_the_commit_fails()
    {
        await ExecuteAsync(
            """
            CREATE FUNCTION fail_execution_commit() RETURNS trigger LANGUAGE plpgsql AS
            $$ BEGIN RAISE EXCEPTION 'commit refused' USING ERRCODE = 'P0001'; END $$;
            CREATE CONSTRAINT TRIGGER fail_execution_commit AFTER INSERT ON "ScheduledRunExecutions"
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION fail_execution_commit();
            """);
        try
        {
            var act = async () => await Store().TryBeginAsync(new ScheduledRunDue(_scheduleId, _occurrence), default);

            (await act.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0001");
            (await ExecutionIdsAsync()).Should().BeEmpty();
            (await OutboxRowCountAsync<RunScoutStep>()).Should().Be(
                0, "the scout step enqueued before the failed commit rolls back with the execution");
        }
        finally
        {
            await ExecuteAsync(
                """
                DROP TRIGGER fail_execution_commit ON "ScheduledRunExecutions";
                DROP FUNCTION fail_execution_commit();
                """);
        }
    }

    /// <summary>
    ///     A delivery whose guarded advance loses the race to a concurrent write of its row returns
    ///     <see langword="false"/>, rolls back the channel message it already enqueued, and leaves the winner's values.
    ///     Red: drop <c>Sql.Xmin(e) == version</c> from <c>AdvanceQuery</c>; the delivery wins, the row ends
    ///     <see cref="RunStep.Done"/> and the message stays.
    /// </summary>
    [Fact]
    public async Task Advance_loses_the_race_and_rolls_back_its_enqueue()
    {
        var store = Store();
        await store.TryBeginAsync(new ScheduledRunDue(_scheduleId, _occurrence), default);
        var id = (await ExecutionIdsAsync()).Single();
        await store.TryCompleteScoutAsync(id, "three open PRs", default);
        await store.TryCompleteWriterAsync(id, "Here is your digest.", default);
        await using var winner = await HoldUpdateAsync(
            """UPDATE "ScheduledRunExecutions" SET "Attempts" = 7 WHERE "Id" = @id""", id);

        var delivery = Store().TryCompleteDeliveryAsync(id, default).AsTask();
        await WaitUntilAnExecutionUpdateWaitsOnALockAsync();
        await winner.CommitAsync();
        var delivered = await delivery;

        delivered.Should().BeFalse("the loser no-ops; it does not throw");
        (await OutboxRowCountAsync<ChannelMessageQueued>()).Should().Be(
            0, "the message enqueued before the lost advance rolls back with it");
        var stored = (await Store().FindAsync(id, default))!;
        stored.Step.Should().Be(RunStep.Deliver);
        stored.Attempts.Should().Be(7);
    }

    /// <summary>
    ///     A failure whose guarded write loses the race returns silently, rolls back the operator notice it already
    ///     enqueued, and leaves the winner's values. Red: drop <c>Sql.Xmin(e) == version</c> from <c>FailQuery</c>; the
    ///     row ends <see cref="RunStep.Failed"/> and the notice stays.
    /// </summary>
    [Fact]
    public async Task Fail_loses_the_race_and_rolls_back_its_notice()
    {
        await Store().TryBeginAsync(new ScheduledRunDue(_scheduleId, _occurrence), default);
        var id = (await ExecutionIdsAsync()).Single();
        await using var winner = await HoldUpdateAsync(
            """UPDATE "ScheduledRunExecutions" SET "Attempts" = 7 WHERE "Id" = @id""", id);

        var fail = Store().FailAsync(id, "provider returned 529", default).AsTask();
        await WaitUntilAnExecutionUpdateWaitsOnALockAsync();
        await winner.CommitAsync();
        var act = async () => await fail;

        await act.Should().NotThrowAsync("the loser returns silently, as before");
        (await OutboxRowCountAsync<ChannelMessageQueued>()).Should().Be(
            0, "the notice enqueued before the lost failure rolls back with it");
        var stored = (await Store().FindAsync(id, default))!;
        stored.Step.Should().Be(RunStep.Scout);
        stored.Attempts.Should().Be(7);
        stored.LastError.Should().BeNull();
    }

    /// <summary>
    ///     The scout's window starts at the latest <see cref="RunStep.Done"/> occurrence of the same schedule strictly
    ///     before the current one: not an earlier one, not another schedule's, not the current occurrence itself. Red:
    ///     <c>Min</c> for <c>Max</c> in <c>LatestDoneOccurrenceBeforeQuery</c> gives -49h; dropping the schedule test
    ///     gives -1h; <c>&lt;=</c> for <c>&lt;</c> gives the current occurrence.
    /// </summary>
    [Fact]
    public async Task The_window_starts_at_the_latest_done_occurrence_of_its_schedule_before_the_current_one()
    {
        var current = _occurrence.AddDays(2);
        await InsertDoneExecutionAsync(_scheduleId, current.AddHours(-49));
        await InsertDoneExecutionAsync(_scheduleId, current.AddHours(-25));
        await InsertDoneExecutionAsync(_scheduleId, current);
        await InsertDoneExecutionAsync(Guid.CreateVersion7(), current.AddHours(-1));

        var since = await Store().ResolveWindowStartAsync(_scheduleId, current, default);

        since.Should().Be(current.AddHours(-25));
        since.Kind.Should().Be(DateTimeKind.Utc);
    }

    private static Func<EquivalencyOptions<ScheduledRunExecution>, EquivalencyOptions<ScheduledRunExecution>> Members =>
        o => o.ComparingByMembers<ScheduledRunExecution>().WithStrictOrdering();

    private ScheduledRunExecutionStore Store()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddChannelOutboxOver(fixture);
        services.AddScoped<ScheduledRunExecutionStore>();
        var provider = services.BuildServiceProvider();
        _disposables.Add(provider);
        var scope = provider.CreateAsyncScope();
        _disposables.Add(scope);
        return scope.ServiceProvider.GetRequiredService<ScheduledRunExecutionStore>();
    }

    private Task InsertDoneExecutionAsync(Guid scheduleId, DateTime occurrenceAt) =>
        ExecuteAsync(
            """
            INSERT INTO "ScheduledRunExecutions"
                ("Id", "ScheduleId", "OccurrenceAt", "Step", "FailedAtStep", "Findings", "Digest", "ChannelId",
                 "ConversationId", "PrincipalId", "Roles", "Attempts", "LastError", "CreatedAt", "UpdatedAt")
            VALUES (@id, @scheduleId, @at, 'Done', NULL, 'f', 'd', 'telegram', '1', 'p', 'reader', 0, NULL, @at, @at)
            """,
            ("id", Guid.CreateVersion7()), ("scheduleId", scheduleId), ("at", occurrenceAt));

    private async Task<List<Guid>> ExecutionIdsAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""SELECT "Id" FROM "ScheduledRunExecutions" """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Runs <paramref name="sql"/> in a transaction it leaves open, holding the row lock until the commit.</summary>
    private async Task<HeldUpdate> HoldUpdateAsync(string sql, Guid id)
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var tx = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand(sql, connection, tx))
        {
            command.Parameters.AddWithValue("id", id);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        return new HeldUpdate(connection, tx);
    }

    private async Task WaitUntilAnExecutionUpdateWaitsOnALockAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND query LIKE 'UPDATE "ScheduledRunExecutions"%'
                """,
                connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("No guarded update of an execution ever waited on the row lock.");
    }

    private async Task<long> OutboxRowCountAsync<T>() =>
        await ScalarAsync<long>($"""SELECT count(*) FROM "OutboxMessages" WHERE "TypeName" = '{typeof(T).FullName}'""");

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
