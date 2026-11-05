using Daedalus.Agents.Memory;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.AgentMemories;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Thalos;
using Thalos.Memory;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Phase 3.5: <see cref="PostgresMemoryStore"/> on ZeroAlloc.ORM stores every column of a memory, loads a row whatever
///     <c>AgentMemory.Create</c> would say of it today, writes only the fields an update supplies, and keeps an update's
///     columns in one transaction.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresMemoryStoreRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset _at = new(2026, 11, 5, 9, 15, 30, TimeSpan.Zero);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static MemoryRecord Record(string text = "remember this", string[]? tags = null) => new()
    {
        Id = MemoryId.New(),
        OwnerId = "alice",
        AgentId = AgentId.New(),
        Kind = MemoryKind.Learning,
        Text = text,
        Tags = tags ?? ["ef", "ops"],
        Source = "tool:memory__remember",
        Importance = 0.75,
        CreatedAt = _at,
        UpdatedAt = _at.AddMinutes(5),
        LastRecalledAt = _at.AddMinutes(10),
        RecallCount = 3,
        IsArchived = true,
        IndexPending = true,
    };

    /// <summary>
    ///     Every field of a memory record: stored through the store and read back identical, through the get, the list
    ///     and the stream, each of which selects the columns itself. Red: leave any column out of the insert or of a read.
    /// </summary>
    [Fact]
    public async Task Stored_memory_loads_back_whole()
    {
        var store = new PostgresMemoryStore(fixture.Db, TimeProvider.System);
        var record = Record();

        (await store.CreateAsync(record, CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await store.GetAsync(record.Id, CancellationToken.None)).Value.Should().BeEquivalentTo(record);
        var query = new MemoryQuery { IncludeArchived = true };
        (await store.ListAsync(query, CancellationToken.None)).Value.Items.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(record, "the raw list statement must select every column in order");
        var streamed = new List<MemoryRecord>();
        await foreach (var r in store.StreamAsync(query, CancellationToken.None))
        {
            streamed.Add(r);
        }

        streamed.Should().ContainSingle().Which.Should().BeEquivalentTo(record, "the raw stream statement too");
    }

    /// <summary>
    ///     The aggregate itself: a stored memory comes back equal in every property, with UTC times. Red: read any
    ///     column wrongly, and the comparison names it.
    /// </summary>
    [Fact]
    public async Task Stored_aggregate_loads_back_whole()
    {
        var memory = AgentMemory.Create(
            Guid.NewGuid(), "alice", Guid.NewGuid(), "learning", "Text.", ["ef", "ops"], "src", 0.75, _at.UtcDateTime,
            true, _at.UtcDateTime.AddMinutes(5), true, 3, _at.UtcDateTime.AddMinutes(10)).Value;
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new AgentMemorySql(connection);

        await sql.InsertAsync(memory, CancellationToken.None);
        var loaded = await sql.FindAsync(memory.Id, CancellationToken.None);

        loaded.Should().BeEquivalentTo(memory, options => options.ComparingByMembers<AgentMemory>());
        loaded!.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.LastRecalledAt!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>
    ///     Review focus 1: a row <c>Create</c> refuses, an uppercase kind and tags, empty text and source, an
    ///     importance above one and an update that precedes the creation, still loads, as written. Red: build the
    ///     loaded row through <c>Create</c>, and the load fails.
    /// </summary>
    [Fact]
    public async Task A_row_Create_rejects_still_loads()
    {
        var id = Guid.NewGuid();
        AgentMemory.Create(id, "alice", null, "Legacy", "", ["A"], "", 5, _at.UtcDateTime, false, _at.UtcDateTime.AddHours(-1))
            .IsFailure.Should().BeTrue("the row below is one Create refuses");
        await ExecuteAsync(
            """
            INSERT INTO "AgentMemories"
                ("Id", "OwnerId", "AgentId", "Kind", "Text", "Tags", "Source", "Importance", "CreatedAt", "UpdatedAt",
                 "LastRecalledAt", "RecallCount", "IsArchived", "IndexPending")
            VALUES (@id, 'alice', NULL, 'Legacy', '', '{A,B}', '', 5, @at, @before, NULL, -1, false, true)
            """,
            ("id", id), ("at", _at.UtcDateTime), ("before", _at.UtcDateTime.AddHours(-1)));
        var store = new PostgresMemoryStore(fixture.Db, TimeProvider.System);

        var loaded = (await store.GetAsync(new MemoryId(id), CancellationToken.None)).Value;

        loaded.Kind.Value.Should().Be("Legacy");
        loaded.Text.Should().BeEmpty();
        loaded.Tags.Should().Equal("A", "B");
        loaded.Source.Should().BeEmpty();
        loaded.Importance.Should().Be(5);
        loaded.CreatedAt.Should().Be(_at);
        loaded.UpdatedAt.Should().Be(_at.AddHours(-1));
        loaded.RecallCount.Should().Be(-1);
        loaded.IndexPending.Should().BeTrue();
    }

    /// <summary>
    ///     A recall counted between the update's read and its write stands. The clock plays the other writer: the
    ///     store asks it for the time after reading the row and before writing, and there the hook marks the memory
    ///     recalled. The update then sets the text; the count and the last recall time are the recall's. Red: write
    ///     the whole row, RecallCount and LastRecalledAt included, and the count goes back to what the read saw.
    /// </summary>
    [Fact]
    public async Task Update_does_not_undo_a_concurrent_recall()
    {
        var clock = new HookClock();
        var store = new PostgresMemoryStore(fixture.Db, clock);
        var record = Record() with { RecallCount = 0, LastRecalledAt = null, IsArchived = false };
        (await store.CreateAsync(record, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var recalledAt = _at.AddHours(2);
        clock.OnNextRead = () => new PostgresMemoryStore(fixture.Db, TimeProvider.System)
            .MarkRecalledAsync([record.Id], recalledAt, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var updated = await store.UpdateAsync(record.Id, new MemoryUpdate { Text = "edited" }, CancellationToken.None);

        updated.IsSuccess.Should().BeTrue();
        clock.Fired.Should().BeTrue("the update must have read the clock between its read and its write");
        var stored = (await store.GetAsync(record.Id, CancellationToken.None)).Value;
        stored.Text.Should().Be("edited");
        stored.RecallCount.Should().Be(1, "the recall landed after the read and before the write");
        stored.LastRecalledAt.Should().Be(recalledAt);
    }

    /// <summary>
    ///     An update writes only what it supplies: an archive flag flipped between the read and the write stands when
    ///     the update sets the index flag. Red: write every column and the archive flag goes back.
    /// </summary>
    [Fact]
    public async Task Update_writes_only_the_supplied_fields()
    {
        var clock = new HookClock();
        var store = new PostgresMemoryStore(fixture.Db, clock);
        var record = Record() with { IsArchived = false, IndexPending = true };
        (await store.CreateAsync(record, CancellationToken.None)).IsSuccess.Should().BeTrue();
        clock.OnNextRead = () => new PostgresMemoryStore(fixture.Db, TimeProvider.System)
            .UpdateAsync(record.Id, new MemoryUpdate { IsArchived = true }, CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        (await store.UpdateAsync(record.Id, new MemoryUpdate { IndexPending = false }, CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        var stored = (await store.GetAsync(record.Id, CancellationToken.None)).Value;
        stored.IndexPending.Should().BeFalse();
        stored.IsArchived.Should().BeTrue("the other writer's archive flag was not part of this update");
    }

    /// <summary>
    ///     The index flag alone is bookkeeping and does not stamp <c>UpdatedAt</c>; a content field does, in the same
    ///     statement. Red: stamp <c>UpdatedAt</c> on the index-flag write, or leave it out of a content write.
    /// </summary>
    [Fact]
    public async Task Only_a_content_field_stamps_UpdatedAt()
    {
        var clock = new HookClock { Now = _at.AddDays(1) };
        var store = new PostgresMemoryStore(fixture.Db, clock);
        var record = Record();
        (await store.CreateAsync(record, CancellationToken.None)).IsSuccess.Should().BeTrue();

        await store.UpdateAsync(record.Id, new MemoryUpdate { IndexPending = false }, CancellationToken.None);
        (await store.GetAsync(record.Id, CancellationToken.None)).Value.UpdatedAt.Should().Be(record.UpdatedAt);

        await store.UpdateAsync(record.Id, new MemoryUpdate { Importance = 0.1 }, CancellationToken.None);
        (await store.GetAsync(record.Id, CancellationToken.None)).Value.UpdatedAt.Should().Be(clock.Now);
    }

    /// <summary>
    ///     A memory deleted between the update's read and its write is not found, as EF's update of a missing row threw
    ///     and the store answered. Red: drop ThrowOnNoRows from the update statements and the update answers success
    ///     for a row that is gone.
    /// </summary>
    [Fact]
    public async Task Update_of_a_memory_deleted_after_the_read_is_not_found()
    {
        var clock = new HookClock();
        var store = new PostgresMemoryStore(fixture.Db, clock);
        var record = Record();
        (await store.CreateAsync(record, CancellationToken.None)).IsSuccess.Should().BeTrue();
        clock.OnNextRead = () => new PostgresMemoryStore(fixture.Db, TimeProvider.System)
            .DeleteAsync(record.Id, CancellationToken.None).AsTask().GetAwaiter().GetResult();

        var result = await store.UpdateAsync(record.Id, new MemoryUpdate { Text = "too late" }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.MemoryNotFound);
    }

    /// <summary>
    ///     An update's columns commit together or not at all, as EF's single SaveChanges did. The text and the importance
    ///     are two statements; a trigger fails any write that changes the importance, the second one. The update answers
    ///     MemoryStoreFailed and the text is still the old one. Red: run the statements outside a transaction and the
    ///     new text survives.
    /// </summary>
    [Fact]
    public async Task Update_rolls_the_text_back_when_the_importance_write_fails()
    {
        var store = new PostgresMemoryStore(fixture.Db, TimeProvider.System);
        var record = Record();
        (await store.CreateAsync(record, CancellationToken.None)).IsSuccess.Should().BeTrue();
        await ExecuteAsync(
            """
            CREATE FUNCTION refuse_importance() RETURNS trigger AS $$
            BEGIN
                IF NEW."Importance" IS DISTINCT FROM OLD."Importance" THEN
                    RAISE EXCEPTION 'importance write refused' USING ERRCODE = 'P0001';
                END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER refuse_importance BEFORE UPDATE ON "AgentMemories"
                FOR EACH ROW EXECUTE FUNCTION refuse_importance();
            """);
        try
        {
            var result = await store.UpdateAsync(
                record.Id, new MemoryUpdate { Text = "half done", Importance = 0.1 }, CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be(AgentErrorCode.MemoryStoreFailed);
        }
        finally
        {
            await ExecuteAsync("""DROP TRIGGER refuse_importance ON "AgentMemories"; DROP FUNCTION refuse_importance();""");
        }

        var stored = (await store.GetAsync(record.Id, CancellationToken.None)).Value;
        stored.Text.Should().Be(record.Text);
        stored.Importance.Should().Be(record.Importance);
    }

    /// <summary>
    ///     A recall adds one to the stored count inside the statement, so two recalls make two and an unknown id is
    ///     skipped. Red: set the count to a value read earlier instead of computing it in SQL.
    /// </summary>
    [Fact]
    public async Task Recalls_add_one_each_in_the_statement()
    {
        var store = new PostgresMemoryStore(fixture.Db, TimeProvider.System);
        var first = Record() with { RecallCount = 4, LastRecalledAt = null };
        var second = Record() with { RecallCount = 0, LastRecalledAt = null };
        await store.CreateAsync(first, CancellationToken.None);
        await store.CreateAsync(second, CancellationToken.None);

        await store.MarkRecalledAsync([first.Id, first.Id, MemoryId.New()], _at.AddHours(1), CancellationToken.None);
        await store.MarkRecalledAsync([first.Id, second.Id], _at.AddHours(2), CancellationToken.None);

        var one = (await store.GetAsync(first.Id, CancellationToken.None)).Value;
        one.RecallCount.Should().Be(6);
        one.LastRecalledAt.Should().Be(_at.AddHours(2));
        (await store.GetAsync(second.Id, CancellationToken.None)).Value.RecallCount.Should().Be(1);
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

    /// <summary>A clock that runs a hook the first time the store reads it, then reads on as the fixed time.</summary>
    private sealed class HookClock : TimeProvider
    {
        public DateTimeOffset Now { get; init; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        public Action? OnNextRead { get; set; }

        public bool Fired { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            if (OnNextRead is { } hook)
            {
                OnNextRead = null;
                Fired = true;
                hook();
            }

            return Now;
        }
    }
}
