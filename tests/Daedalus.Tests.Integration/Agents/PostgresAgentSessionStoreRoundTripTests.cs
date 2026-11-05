using Daedalus.Agents.Sessions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.AgentSessions;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Phase 3.5: <see cref="PostgresAgentSessionStore"/> on ZeroAlloc.ORM stores every column of a session, loads a row
///     whatever <c>AgentSession.Create</c> would say of it today, appends a batch of messages all or nothing, and crash
///     recovery resets every running session in one statement.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresAgentSessionStoreRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     The aggregate itself: a stored session with a state, counters and an activity time apart from its creation
    ///     comes back equal in every property, with UTC times, through the get and through the list, which selects its
    ///     columns itself. Red: read any column wrongly, and the comparison names it.
    /// </summary>
    [Fact]
    public async Task Stored_session_loads_back_whole()
    {
        var session = AgentSession.Create(Guid.NewGuid(), Guid.NewGuid(), "alice", _at).Value;
        session.RecordTurn(120, 30, _at.AddMinutes(1));
        session.SetState(AgentSessionState.AwaitingApproval, _at.AddMinutes(2));
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new AgentSessionSql(connection);

        await sql.InsertAsync(session, CancellationToken.None);
        var loaded = await sql.FindAsync(session.Id, CancellationToken.None);
        var listed = await sql.ListByOwnerAsync("alice", 0, 10, CancellationToken.None);

        loaded.Should().BeEquivalentTo(session, options => options.ComparingByMembers<AgentSession>());
        loaded!.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.LastActivityAt.Kind.Should().Be(DateTimeKind.Utc);
        listed.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(session, options => options.ComparingByMembers<AgentSession>());
    }

    /// <summary>
    ///     Review focus 1: a row <c>Create</c> refuses, an empty agent id and a blank owner, with negative counters and an
    ///     activity time before the creation, still loads, as written. The dead <c>RowVersion</c> column holds bytes the
    ///     store never reads. Red: build the loaded row through <c>Create</c>, and the load fails.
    /// </summary>
    [Fact]
    public async Task A_row_Create_rejects_still_loads()
    {
        var id = Guid.NewGuid();
        AgentSession.Create(id, Guid.Empty, " ", _at).IsFailure.Should().BeTrue("the row below is one Create refuses");
        await ExecuteAsync(
            """
            INSERT INTO "AgentSessions"
                ("Id", "AgentId", "OwnerId", "State", "CreatedAt", "LastActivityAt", "TurnCount", "TotalInputTokens",
                 "TotalOutputTokens", "RowVersion")
            VALUES (@id, @agentId, ' ', 3, @at, @before, -1, -2, -3, '\x0102'::bytea)
            """,
            ("id", id), ("agentId", Guid.Empty), ("at", _at), ("before", _at.AddHours(-1)));
        var store = new PostgresAgentSessionStore(fixture.Db, TimeProvider.System);

        var loaded = (await store.GetAsync(new SessionId(id), CancellationToken.None)).Value;

        loaded.AgentId.Value.Should().Be(Guid.Empty);
        loaded.OwnerId.Should().Be(" ");
        loaded.State.Should().Be(SessionState.Closed);
        loaded.CreatedAt.Should().Be(new DateTimeOffset(_at));
        loaded.LastActivityAt.Should().Be(new DateTimeOffset(_at.AddHours(-1)));
        loaded.TurnCount.Should().Be(-1);
        loaded.TotalInputTokens.Should().Be(-2);
        loaded.TotalOutputTokens.Should().Be(-3);
    }

    /// <summary>
    ///     A batch of messages commits together or not at all, as EF's single SaveChanges in its transaction did. The bulk
    ///     insert writes 100 rows per statement, so 101 messages take two statements; a trigger fails the second one. The
    ///     append throws and no message is stored. Red: run the inserts outside the transaction, and the first 100 stay.
    /// </summary>
    [Fact]
    public async Task Append_stores_no_message_when_a_later_insert_fails()
    {
        var store = new PostgresAgentSessionStore(fixture.Db, new FakeTimeProvider(new DateTimeOffset(_at)));
        var session = (await store.CreateAsync(new AgentId(Guid.NewGuid()), "alice", CancellationToken.None)).Value;
        var messages = Enumerable.Range(0, 101).Select(i => new ChatMessage(ChatRole.User, $"message {i}")).ToList();
        await ExecuteAsync(
            """
            CREATE FUNCTION refuse_sequence_100() RETURNS trigger AS $$
            BEGIN
                IF NEW."Sequence" = 100 THEN
                    RAISE EXCEPTION 'message 100 refused' USING ERRCODE = 'P0001';
                END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER refuse_sequence_100 BEFORE INSERT ON "AgentMessages"
                FOR EACH ROW EXECUTE FUNCTION refuse_sequence_100();
            """);
        try
        {
            var append = () => store.AppendMessagesAsync(session.Id, messages, CancellationToken.None).AsTask();

            (await append.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0001");
        }
        finally
        {
            await ExecuteAsync("""DROP TRIGGER refuse_sequence_100 ON "AgentMessages"; DROP FUNCTION refuse_sequence_100();""");
        }

        (await store.LoadMessagesAsync(session.Id, CancellationToken.None)).Value.Should().BeEmpty();
    }

    /// <summary>
    ///     Crash recovery sets every <c>Running</c> session to <c>Idle</c> with the start time as its last activity, and
    ///     leaves sessions in any other state alone. Red: drop the state condition, and the closed session reopens.
    /// </summary>
    [Fact]
    public async Task Crash_recovery_resets_only_running_sessions()
    {
        var store = new PostgresAgentSessionStore(fixture.Db, new FakeTimeProvider(new DateTimeOffset(_at)));
        var running = (await store.CreateAsync(new AgentId(Guid.NewGuid()), "alice", CancellationToken.None)).Value;
        var closed = (await store.CreateAsync(new AgentId(Guid.NewGuid()), "alice", CancellationToken.None)).Value;
        await store.UpdateStateAsync(running.Id, SessionState.Running, CancellationToken.None);
        await store.UpdateStateAsync(closed.Id, SessionState.Closed, CancellationToken.None);
        var restart = _at.AddHours(1);

        await new AgentSessionCrashRecovery(
                fixture.Db, new FakeTimeProvider(new DateTimeOffset(restart)), NullLogger<AgentSessionCrashRecovery>.Instance)
            .StartAsync(CancellationToken.None);

        var reset = (await store.GetAsync(running.Id, CancellationToken.None)).Value;
        reset.State.Should().Be(SessionState.Idle);
        reset.LastActivityAt.Should().Be(new DateTimeOffset(restart));
        var untouched = (await store.GetAsync(closed.Id, CancellationToken.None)).Value;
        untouched.State.Should().Be(SessionState.Closed);
        untouched.LastActivityAt.Should().Be(new DateTimeOffset(_at));
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
}
