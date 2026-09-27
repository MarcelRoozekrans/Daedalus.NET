using System.Collections.Concurrent;
using System.Data.Async.Adapters;
using AwesomeAssertions.Execution;
using Daedalus.Agents.Workflow;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Thalos.Workflow;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.Orm;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     The lease half of <see cref="WorkflowOutboxDispatchService"/>, against a real Postgres outbox table: the
///     claim is exclusive across replicas, the outcome of a dispatch that ran is recorded even when the host
///     stops during it, a dispatch's own cancellation is a failed attempt rather than a stop, a stop before the
///     dispatch finished hands the lease back, and a mark that finds the message taken over is not a failure.
/// </summary>
/// <remarks>
///     The dispatcher is a scripted <see cref="IOutboxTypeDispatcher"/> for
///     <see cref="WorkflowDispatch.TypeName"/>; the store, the claim SQL and the table are real. Each test gets its
///     own database, migrated with <see cref="OutboxOrmMigrations.Postgres"/> exactly as
///     <c>Daedalus.Migrations</c> does, so the lease columns come from the migration and not from the test.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class WorkflowOutboxDispatchLeaseTests(PostgresFixture fixture)
{
    // OrmOutboxMessageStatus is internal to ZeroAlloc.Outbox.Orm; these are its stored values.
    private const int Pending = 0;
    private const int Succeeded = 1;

    [Fact]
    public async Task Two_dispatch_services_on_one_database_never_both_dispatch_the_same_message()
    {
        await WithDatabaseAsync(async dataSource =>
        {
            var marker = await EnqueueAsync(dataSource);

            var dispatches = new ConcurrentBag<string>();
            var replicaB = CreateService(dataSource, new ScriptedDispatcher((payload, _) =>
            {
                dispatches.Add("replica-b:" + new Guid(payload.Span));
                return ValueTask.CompletedTask;
            }), new WorkflowOutboxDispatchOptions { HostId = "replica-b" });

            // Replica A is mid-turn when replica B polls: the claim and the renewal already happened, the mark has
            // not. The pause makes a lease shorter than this dispatch run out before B claims.
            var replicaA = CreateService(dataSource, new ScriptedDispatcher(async (payload, _) =>
            {
                dispatches.Add("replica-a:" + new Guid(payload.Span));
                await Task.Delay(TimeSpan.FromMilliseconds(200));
                await replicaB.ProcessBatchAsync(CancellationToken.None);
            }), new WorkflowOutboxDispatchOptions { HostId = "replica-a" });

            await replicaA.ProcessBatchAsync(CancellationToken.None);

            using var scope = new AssertionScope();
            dispatches.Should().Equal(["replica-a:" + marker], "the message is dispatched once, by the replica holding its lease");
            (await ReadRowAsync(dataSource, marker)).Status.Should().Be(Succeeded, "replica A records the outcome of its dispatch");
        });
    }

    [Fact]
    public async Task A_claim_passes_over_a_row_another_replica_holds_locked()
    {
        await WithDatabaseAsync(async dataSource =>
        {
            var locked = await EnqueueAsync(dataSource);
            var free = await EnqueueAsync(dataSource);

            // Another replica is mid-claim on the older row: its row lock is held in an open transaction.
            await using var otherReplica = await dataSource.OpenConnectionAsync();
            await using var transaction = await otherReplica.BeginTransactionAsync();
            await using (var lockCommand = new NpgsqlCommand("SELECT id FROM outboxmessages WHERE payload = @payload FOR UPDATE", otherReplica, transaction))
            {
                lockCommand.Parameters.AddWithValue("payload", locked.ToByteArray());
                await lockCommand.ExecuteNonQueryAsync();
            }

            var dispatched = new ConcurrentBag<Guid>();
            var service = CreateService(dataSource, new ScriptedDispatcher((payload, _) =>
            {
                dispatched.Add(new Guid(payload.Span));
                return ValueTask.CompletedTask;
            }));

            var batch = service.ProcessBatchAsync(CancellationToken.None);
            var finished = await Task.WhenAny(batch, Task.Delay(TimeSpan.FromSeconds(10))) == batch;

            // Unblock a claim that queued on the lock, so a failing run still ends cleanly.
            await transaction.RollbackAsync();
            await batch;

            using var scope = new AssertionScope();
            finished.Should().BeTrue("the claim skips the locked row instead of waiting for the other replica");
            dispatched.Should().Equal([free], "the claim passes over the locked row to the next due one");
        });
    }

    [Fact]
    public async Task The_outcome_of_a_dispatch_that_ran_is_recorded_even_when_the_host_stops_during_it()
    {
        await WithDatabaseAsync(async dataSource =>
        {
            var id = await EnqueueAsync(dataSource);

            using var stopping = new CancellationTokenSource();
            var service = CreateService(dataSource, new ScriptedDispatcher(async (_, _) =>
            {
                // The host is asked to stop after the turn has already run to completion.
                await stopping.CancelAsync();
            }));

            var thrown = await Record.ExceptionAsync(() => service.ProcessBatchAsync(stopping.Token));

            var row = await ReadRowAsync(dataSource, id);
            using var scope = new AssertionScope();
            row.Status.Should().Be(Succeeded, "the completed dispatch is marked, so no replica runs it again");
            row.LockedBy.Should().BeNull("marking releases the lease");
            thrown.Should().BeNull("the dispatch ran and its outcome was recorded; nothing was left to stop");
        });
    }

    [Fact]
    public async Task A_dispatch_that_cancels_on_its_own_is_a_failed_attempt_not_a_stop()
    {
        await WithDatabaseAsync(async dataSource =>
        {
            var id = await EnqueueAsync(dataSource);

            // A turn deadline, for example: the dispatch raises its own cancellation while the host keeps running.
            var service = CreateService(dataSource, new ScriptedDispatcher((_, _) => throw new OperationCanceledException("turn deadline")));

            var thrown = await Record.ExceptionAsync(() => service.ProcessBatchAsync(CancellationToken.None));

            var row = await ReadRowAsync(dataSource, id);
            using var scope = new AssertionScope();
            thrown.Should().BeNull("only a cancellation of the stopping token stops the loop");
            row.Status.Should().Be(Pending);
            row.RetryCount.Should().Be(1, "the cancelled dispatch counts as one failed attempt, retried with backoff");
            row.LockedBy.Should().BeNull("recording the failure releases the lease, so the retry can be claimed");
        });
    }

    [Fact]
    public async Task A_stop_during_a_dispatch_hands_the_lease_back()
    {
        await WithDatabaseAsync(async dataSource =>
        {
            var id = await EnqueueAsync(dataSource);

            using var stopping = new CancellationTokenSource();
            var service = CreateService(dataSource, new ScriptedDispatcher(async (_, ct) =>
            {
                // The host stops while the turn is still running, and the turn observes it.
                await stopping.CancelAsync();
                ct.ThrowIfCancellationRequested();
            }));

            var thrown = await Record.ExceptionAsync(() => service.ProcessBatchAsync(stopping.Token));

            var row = await ReadRowAsync(dataSource, id);
            using var scope = new AssertionScope();
            thrown.Should().BeAssignableTo<OperationCanceledException>("a stop propagates so the host can shut down");
            row.LockedBy.Should().BeNull("the unfinished message is released, so another replica can claim it at once");
            row.Status.Should().Be(Pending);
            row.RetryCount.Should().Be(0, "a stop is not a failed attempt");
        });
    }

    [Fact]
    public async Task A_mark_that_finds_the_message_taken_over_is_not_a_failure()
    {
        await WithDatabaseAsync(async dataSource =>
        {
            var id = await EnqueueAsync(dataSource);

            var service = CreateService(dataSource, new ScriptedDispatcher(async (_, _) =>
            {
                // This host's lease ran out mid-dispatch and another replica claimed the message.
                await using var command = dataSource.CreateCommand("UPDATE outboxmessages SET lockedby = 'replica-b' WHERE payload = @payload");
                command.Parameters.AddWithValue("payload", id.ToByteArray());
                await command.ExecuteNonQueryAsync();
            }));

            var thrown = await Record.ExceptionAsync(() => service.ProcessBatchAsync(CancellationToken.None));

            // Only the service's own reaction is asserted. That the row keeps the new holder's lease is the store's
            // conditional mark, which no change to this service can break, so asserting it here could not fail.
            thrown.Should().BeNull("the other replica now owns the message; that is not an error in this batch");
        });
    }

    private static WorkflowOutboxDispatchService CreateService(
        NpgsqlDataSource dataSource, IOutboxTypeDispatcher dispatcher, WorkflowOutboxDispatchOptions? options = null) =>
        new(dataSource, dispatcher, options ?? new WorkflowOutboxDispatchOptions(), NullLogger<WorkflowOutboxDispatchService>.Instance);

    /// <summary>
    ///     Enqueues a workflow dispatch whose payload is a fresh marker, and returns that marker. The store
    ///     generates the row id; the tests identify a row by its payload instead.
    /// </summary>
    private static async Task<Guid> EnqueueAsync(NpgsqlDataSource dataSource)
    {
        var marker = Guid.NewGuid();
        await using var connection = await dataSource.OpenConnectionAsync();
        var store = new OrmOutboxStore(connection.AsAsync(), OutboxOrmDialect.Postgres);
        await store.EnqueueAsync(WorkflowDispatch.TypeName, marker.ToByteArray(), transaction: null, CancellationToken.None);
        return marker;
    }

    private static async Task<(int Status, int RetryCount, string? LockedBy)> ReadRowAsync(NpgsqlDataSource dataSource, Guid marker)
    {
        await using var command = dataSource.CreateCommand("SELECT status, retrycount, lockedby FROM outboxmessages WHERE payload = @payload");
        command.Parameters.AddWithValue("payload", marker.ToByteArray());
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"the outbox row for marker {marker} exists");
        return (reader.GetInt32(0), reader.GetInt32(1), await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
    }

    private async Task WithDatabaseAsync(Func<NpgsqlDataSource, Task> test)
    {
        var dbName = $"workflow_lease_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;

        try
        {
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await new MigrationRunner(connection.AsAsync(), OutboxOrmMigrations.Postgres, new PostgresMigrationDialect()).RunAsync();
            }

            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            await test(dataSource);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        }
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ScriptedDispatcher(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> dispatch) : IOutboxTypeDispatcher
    {
        public string TypeName => WorkflowDispatch.TypeName;

        public ValueTask DispatchAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => dispatch(payload, ct);
    }
}
