using Npgsql;
using NpgsqlTypes;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     The status codes ZeroAlloc.Outbox.Orm's store writes to the channel table's <c>"Status"</c> column. The store's
///     own enum is internal; these are its stored values, the same ones EF Core's <c>OutboxMessageStatus</c> had.
/// </summary>
internal enum ChannelOutboxStatus
{
    /// <summary>Waiting for, or between, delivery attempts.</summary>
    Pending = 0,

    /// <summary>Delivered.</summary>
    Succeeded = 1,

    /// <summary>Abandoned after the last attempt.</summary>
    DeadLetter = 2,
}

/// <summary>
///     One row of the channel outbox table <c>"OutboxMessages"</c>, every column. Mutable, so a test can read a row,
///     change it and write it back with <see cref="ChannelOutboxRows.UpdateAsync"/>.
/// </summary>
internal sealed class ChannelOutboxRow
{
    /// <summary>The message id, the primary key.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>The full name of the message type, the key the generated writers stamp every row with.</summary>
    public string TypeName { get; init; } = "";

    /// <summary>The serialized message.</summary>
    public byte[] Payload { get; init; } = [];

    /// <summary>The delivery status.</summary>
    public ChannelOutboxStatus Status { get; set; }

    /// <summary>How many delivery attempts were made.</summary>
    public int RetryCount { get; set; }

    /// <summary>When the next attempt may run.</summary>
    public DateTimeOffset NextRetryAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the message was enqueued.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the message was delivered or dead-lettered.</summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>Why delivery was abandoned, for a dead letter.</summary>
    public string? DeadLetterError { get; set; }

    /// <summary>The host holding the row's lease.</summary>
    public string? LockedBy { get; set; }

    /// <summary>When the row's lease runs out.</summary>
    public DateTimeOffset? LockedUntil { get; set; }
}

/// <summary>
///     Reads and writes the channel outbox table <c>"OutboxMessages"</c> with plain SQL, for the tests that read or
///     changed its rows through EF Core's <c>OutboxMessageEntity</c> before EF Core left (ruling R129). The production
///     store, ZeroAlloc.Outbox.Orm's, is never bypassed for a write a test asserts on; these helpers only seed a state,
///     such as a dead letter, or read what the store wrote.
/// </summary>
internal static class ChannelOutboxRows
{
    private const string Columns =
        "\"Id\", \"TypeName\", \"Payload\", \"Status\", \"RetryCount\", \"NextRetryAt\", \"CreatedAt\", \"ProcessedAt\", "
        + "\"DeadLetterError\", \"LockedBy\", \"LockedUntil\"";

    /// <summary>Every row whose type name is <typeparamref name="T"/>'s full name, oldest first.</summary>
    public static Task<List<ChannelOutboxRow>> ListAsync<T>(PostgresFixture fixture) =>
        ListAsync(fixture, typeof(T).FullName!);

    /// <summary>Every row whose type name is <paramref name="typeName"/>, or every row when it is null, oldest first.</summary>
    public static async Task<List<ChannelOutboxRow>> ListAsync(PostgresFixture fixture, string? typeName)
    {
        await using var command = fixture.DataSource.CreateCommand(
            $"SELECT {Columns} FROM \"OutboxMessages\" WHERE @typeName::text IS NULL OR \"TypeName\" = @typeName "
            + "ORDER BY \"CreatedAt\", \"Id\"");
        command.Parameters.Add(NullableParameter("typeName", NpgsqlDbType.Text, typeName));
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<ChannelOutboxRow>();
        while (await reader.ReadAsync())
        {
            rows.Add(new ChannelOutboxRow
            {
                Id = await reader.GetFieldValueAsync<Guid>(0),
                TypeName = await reader.GetFieldValueAsync<string>(1),
                Payload = await reader.GetFieldValueAsync<byte[]>(2),
                Status = (ChannelOutboxStatus)await reader.GetFieldValueAsync<int>(3),
                RetryCount = await reader.GetFieldValueAsync<int>(4),
                NextRetryAt = await reader.GetFieldValueAsync<DateTimeOffset>(5),
                CreatedAt = await reader.GetFieldValueAsync<DateTimeOffset>(6),
                ProcessedAt = await reader.GetFieldValueAsync<DateTimeOffset?>(7),
                DeadLetterError = await reader.IsDBNullAsync(8) ? null : await reader.GetFieldValueAsync<string>(8),
                LockedBy = await reader.IsDBNullAsync(9) ? null : await reader.GetFieldValueAsync<string>(9),
                LockedUntil = await reader.GetFieldValueAsync<DateTimeOffset?>(10),
            });
        }

        return rows;
    }

    /// <summary>The one row whose type name is <typeparamref name="T"/>'s full name; throws unless there is exactly one.</summary>
    public static async Task<ChannelOutboxRow> SingleAsync<T>(PostgresFixture fixture) =>
        (await ListAsync<T>(fixture)).Single();

    /// <summary>How many rows have <typeparamref name="T"/>'s full name as their type name.</summary>
    public static async Task<int> CountAsync<T>(PostgresFixture fixture) => (await ListAsync<T>(fixture)).Count;

    /// <summary>How many rows the table holds, of any type.</summary>
    public static async Task<int> CountAsync(PostgresFixture fixture) => (await ListAsync(fixture, typeName: null)).Count;

    /// <summary>Inserts <paramref name="row"/>, every column.</summary>
    public static async Task InsertAsync(PostgresFixture fixture, ChannelOutboxRow row)
    {
        await using var command = fixture.DataSource.CreateCommand(
            $"INSERT INTO \"OutboxMessages\" ({Columns}) VALUES (@id, @typeName, @payload, @status, @retryCount, "
            + "@nextRetryAt, @createdAt, @processedAt, @deadLetterError, @lockedBy, @lockedUntil)");
        Bind(command, row);
        command.Parameters.Add(new NpgsqlParameter<string>("typeName", row.TypeName));
        command.Parameters.Add(new NpgsqlParameter<byte[]>("payload", row.Payload));
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    /// <summary>Writes every mutable column of <paramref name="row"/> back to its stored row.</summary>
    public static async Task UpdateAsync(PostgresFixture fixture, ChannelOutboxRow row)
    {
        await using var command = fixture.DataSource.CreateCommand(
            "UPDATE \"OutboxMessages\" SET \"Status\" = @status, \"RetryCount\" = @retryCount, "
            + "\"NextRetryAt\" = @nextRetryAt, \"CreatedAt\" = @createdAt, \"ProcessedAt\" = @processedAt, "
            + "\"DeadLetterError\" = @deadLetterError, \"LockedBy\" = @lockedBy, \"LockedUntil\" = @lockedUntil "
            + "WHERE \"Id\" = @id");
        Bind(command, row);
        (await command.ExecuteNonQueryAsync()).Should().Be(1, "the row to update must exist");
    }

    private static void Bind(NpgsqlCommand command, ChannelOutboxRow row)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", row.Id));
        command.Parameters.Add(new NpgsqlParameter<int>("status", (int)row.Status));
        command.Parameters.Add(new NpgsqlParameter<int>("retryCount", row.RetryCount));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("nextRetryAt", row.NextRetryAt));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("createdAt", row.CreatedAt));
        command.Parameters.Add(NullableParameter("processedAt", NpgsqlDbType.TimestampTz, row.ProcessedAt));
        command.Parameters.Add(NullableParameter("deadLetterError", NpgsqlDbType.Text, row.DeadLetterError));
        command.Parameters.Add(NullableParameter("lockedBy", NpgsqlDbType.Varchar, row.LockedBy));
        command.Parameters.Add(NullableParameter("lockedUntil", NpgsqlDbType.TimestampTz, row.LockedUntil));
    }

    private static NpgsqlParameter NullableParameter(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}
