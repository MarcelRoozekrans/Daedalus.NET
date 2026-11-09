using System.Collections.Concurrent;
using System.Diagnostics;
using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Application.Abstractions;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using ZeroAlloc.Outbox;
using ZeroAlloc.Results;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Channels;

/// <summary>
///     The switch of the channel pipeline from ZeroAlloc.Outbox.EfCore to ZeroAlloc.Outbox.Orm, against a real
///     database: rows the EF store left in <c>"OutboxMessages"</c>, inserted here with raw SQL in EF's layout, are
///     claimed and delivered by the production pipeline from <see cref="ChannelOutboxServiceCollectionExtensions.AddChannelOutbox"/>,
///     a dead letter is resent through its requeue, and the workflow engine's <c>outboxmessages</c> is never touched.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ChannelOutboxCarryOverTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const int Pending = 0;
    private const int Succeeded = 1;
    private const int DeadLetter = 2;

    private static readonly TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(15);

    private static readonly string _channelTypeName = typeof(ChannelMessageQueued).FullName!;

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Pending_and_leased_rows_are_delivered_once_after_the_switch()
    {
        var recording = new RecordingDispatcher();
        await using var provider = BuildProvider(recording);
        var now = DateTimeOffset.UtcNow;

        var pending = Message("pending before the switch");
        var expired = Message("leased by a host that died");
        var pendingId = await InsertChannelRowAsync(provider, pending, Pending, now.AddMinutes(-3));
        var expiredId = await InsertChannelRowAsync(
            provider, expired, Pending, now.AddMinutes(-2), lockedBy: "old-host", lockedUntil: now.AddMinutes(-1));

        await RunWorkerUntilAsync(provider, async () =>
            await ChannelStatusAsync(pendingId) == Succeeded && await ChannelStatusAsync(expiredId) == Succeeded);

        recording.Messages.Should().BeEquivalentTo(new[] { pending, expired },
            "each row the EF store left pending, or leased by a host whose lease ran out, is delivered exactly once");
    }

    [Fact]
    public async Task Dead_lettered_row_requeued_is_delivered()
    {
        var recording = new RecordingDispatcher();
        await using var provider = BuildProvider(recording);
        var executionId = Guid.NewGuid();
        var message = new ChannelMessageQueued("telegram", "482910337", "Three PRs are waiting on you.", executionId);
        var id = await InsertChannelRowAsync(
            provider, message, DeadLetter, DateTimeOffset.UtcNow.AddMinutes(-5), retryCount: 7,
            deadLetterError: "Telegram returned 429 after 8 attempts");

        Result requeued;
        await using (var scope = provider.CreateAsyncScope())
        {
            requeued = await scope.ServiceProvider.GetRequiredService<IScheduleDeliveryActions>()
                .RequeueAsync(executionId, CancellationToken.None);
        }

        requeued.IsSuccess.Should().BeTrue(requeued.IsFailure ? requeued.Error : null);
        (await ChannelStatusAsync(id)).Should().Be(Pending, "a requeue moves the dead letter back to pending");

        await RunWorkerUntilAsync(provider, async () => await ChannelStatusAsync(id) == Succeeded);

        recording.Messages.Should().ContainSingle().Which.Should().Be(message);
    }

    [Fact]
    public async Task Workflow_outbox_is_untouched()
    {
        var recording = new RecordingDispatcher();
        await using var provider = BuildProvider(recording);
        var now = DateTimeOffset.UtcNow;

        // The workflow table's row carries the channel's own type, so a channel pipeline that claimed from it would
        // deliver it; only the table keeps it out.
        var workflowRow = Message("a row in the workflow engine's table");
        var workflowId = await InsertWorkflowRowAsync(provider, workflowRow, now.AddMinutes(-2));
        var channel = Message("a row in the channel table");
        var channelId = await InsertChannelRowAsync(provider, channel, Pending, now.AddMinutes(-1));

        // Stops as soon as either table moves, so a pipeline on the wrong table fails the assertions below rather
        // than the wait.
        await RunWorkerUntilAsync(provider, async () =>
            await ChannelStatusAsync(channelId) == Succeeded || await WorkflowRowAsync(workflowId) != (Pending, 0, null));

        recording.Messages.Should().ContainSingle().Which.Should().Be(channel);
        var workflow = await WorkflowRowAsync(workflowId);
        workflow.Should().Be((Pending, 0, (string?)null),
            "the channel pipeline neither claims, retries nor leases a row in outboxmessages");
    }

    private static ChannelMessageQueued Message(string text) => new("telegram", "482910337", text);

    private ServiceProvider BuildProvider(IOutboxDispatcher<ChannelMessageQueued> dispatcher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddChannelOutboxOver(fixture, o => o.PollingInterval = _pollingInterval);

        // After the chain: the generated DefaultOutboxDispatcher was only TryAdd-ed, so this registration wins.
        services.AddSingleton(dispatcher);
        services.Configure<ScheduleDiagnosticsOptions>(o => o.DeadLetterLookback = TimeSpan.FromDays(1));
        services.AddScoped<IScheduleDeliveryActions, ScheduleDeliveryActions>();
        return services.BuildServiceProvider();
    }

    /// <summary>Inserts a row into the channel table with every column named as EF Core created it.</summary>
    private async Task<Guid> InsertChannelRowAsync(
        ServiceProvider provider,
        ChannelMessageQueued message,
        int status,
        DateTimeOffset createdAt,
        int retryCount = 0,
        string? deadLetterError = null,
        string? lockedBy = null,
        DateTimeOffset? lockedUntil = null)
    {
        var id = Guid.NewGuid();
        await using var command = fixture.DataSource.CreateCommand("""
            INSERT INTO "OutboxMessages"
                ("Id", "TypeName", "Payload", "Status", "RetryCount", "NextRetryAt", "CreatedAt", "ProcessedAt",
                 "DeadLetterError", "LockedBy", "LockedUntil")
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = _channelTypeName });
        command.Parameters.Add(new NpgsqlParameter { Value = Serialize(provider, message) });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        command.Parameters.Add(new NpgsqlParameter { Value = retryCount });
        command.Parameters.Add(new NpgsqlParameter { Value = createdAt });
        command.Parameters.Add(new NpgsqlParameter { Value = createdAt });
        command.Parameters.Add(new NpgsqlParameter { Value = status == DeadLetter ? createdAt : DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)deadLetterError ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)lockedBy ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)lockedUntil ?? DBNull.Value });
        await command.ExecuteNonQueryAsync();
        return id;
    }

    /// <summary>Inserts a pending row into the workflow engine's table, whose columns are unquoted.</summary>
    private async Task<Guid> InsertWorkflowRowAsync(ServiceProvider provider, ChannelMessageQueued message, DateTimeOffset createdAt)
    {
        var id = Guid.NewGuid();
        await using var command = fixture.DataSource.CreateCommand("""
            INSERT INTO outboxmessages (id, typename, payload, status, retrycount, nextretryat, createdat)
            VALUES ($1, $2, $3, 0, 0, $4, $4)
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = _channelTypeName });
        command.Parameters.Add(new NpgsqlParameter { Value = Serialize(provider, message) });
        command.Parameters.Add(new NpgsqlParameter { Value = createdAt });
        await command.ExecuteNonQueryAsync();
        return id;
    }

    private static byte[] Serialize(ServiceProvider provider, ChannelMessageQueued message) =>
        provider.GetRequiredService<IOutboxSerializer>().Serialize(message).ToArray();

    private async Task<int> ChannelStatusAsync(Guid id)
    {
        await using var command = fixture.DataSource.CreateCommand("""SELECT "Status" FROM "OutboxMessages" WHERE "Id" = $1""");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return (int)(await command.ExecuteScalarAsync())!;
    }

    private async Task<(int Status, int RetryCount, string? LockedBy)> WorkflowRowAsync(Guid id)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "SELECT status, retrycount, lockedby FROM outboxmessages WHERE id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue("the workflow row is still there");
        return (reader.GetInt32(0), reader.GetInt32(1), await reader.IsDBNullAsync(2) ? null : reader.GetString(2));
    }

    /// <summary>
    ///     Runs the channel pipeline's worker until <paramref name="done"/> holds, then a few more polls, so a second
    ///     delivery of a row already succeeded, or a claim of a row it must leave, would still show.
    /// </summary>
    private static async Task RunWorkerUntilAsync(ServiceProvider provider, Func<Task<bool>> done)
    {
        var worker = provider.ChannelWorker();
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            while (!await done())
            {
                if (stopwatch.Elapsed > _waitTimeout)
                {
                    throw new TimeoutException($"The channel worker did not get there within {_waitTimeout}.");
                }

                await Task.Delay(25);
            }

            await Task.Delay(_pollingInterval * 5);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>Records every message it is asked to deliver; simulates a channel adapter that always succeeds.</summary>
    private sealed class RecordingDispatcher : IOutboxDispatcher<ChannelMessageQueued>
    {
        public ConcurrentQueue<ChannelMessageQueued> Messages { get; } = new();

        public ValueTask DispatchAsync(ChannelMessageQueued message, CancellationToken ct)
        {
            Messages.Enqueue(message);
            return ValueTask.CompletedTask;
        }
    }
}
