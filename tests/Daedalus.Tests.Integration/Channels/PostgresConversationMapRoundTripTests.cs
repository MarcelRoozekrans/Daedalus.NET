using Daedalus.Agents.Channels;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.ChannelConversations;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Thalos;
using Thalos.Channels;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Channels;

/// <summary>
///     Phase 3.5: <see cref="PostgresConversationMap"/> on ZeroAlloc.ORM stores every column of a binding, keeps the
///     creation time across a rebind, and loads a row whatever <c>ChannelConversation.Create</c> would say of it today.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresConversationMapRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid _sessionId = new(0x6f9b1c2a, 0x4d3e, 0x4f5a, 0x8b, 0x6c, 0x1a, 0x2b, 0x3c, 0x4d, 0x5e, 0x6f);
    private static readonly Guid _agentId = new(0x9e8d7c6b, 0x5a4f, 0x4321, 0x98, 0x76, 0xfe, 0xdc, 0xba, 0x09, 0x87, 0x65);
    private static readonly Guid _secondSessionId = new(0x11111111, 0x2222, 0x3333, 0x44, 0x44, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55);
    private static readonly DateTimeOffset _at = new(2026, 11, 5, 9, 15, 30, TimeSpan.Zero);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Every column of the stored row: a fresh id, the channel, the conversation, both ids, and a creation time
    ///     equal to the first bind's activity time. Red: leave any column out of the insert and it is null or default.
    /// </summary>
    [Fact]
    public async Task Stored_binding_loads_back_whole()
    {
        var map = new PostgresConversationMap(fixture.Db);
        var binding = new ConversationBinding(
            "telegram", new ConversationId("482910337"), new SessionId(_sessionId), new AgentId(_agentId), _at);

        (await map.BindAsync(binding, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var loaded = (await map.GetAsync("telegram", new ConversationId("482910337"), CancellationToken.None)).Value;

        loaded.Should().NotBeNull();
        loaded!.ChannelId.Should().Be("telegram");
        loaded.ConversationId.Should().Be(new ConversationId("482910337"));
        loaded.SessionId.Should().Be(new SessionId(_sessionId));
        loaded.AgentId.Should().Be(new AgentId(_agentId));
        loaded.LastActivityAt.Should().Be(_at);

        var row = await ReadRowAsync("telegram", "482910337");
        row.Id.Should().NotBe(Guid.Empty);
        row.CreatedAt.Should().Be(_at.UtcDateTime);
        row.LastActivityAt.Should().Be(_at.UtcDateTime);
    }

    /// <summary>
    ///     The aggregate itself: one stored <see cref="ChannelConversation"/> comes back equal in every property,
    ///     the id and creation time included. <c>Entity</c> overrides <c>Equals</c> to compare ids, so the comparison
    ///     goes member by member. Red: read any column wrongly, and the comparison names it.
    /// </summary>
    [Fact]
    public async Task Stored_aggregate_loads_back_whole()
    {
        var stored = ChannelConversation.Create("telegram", "482910337", _sessionId, _agentId, _at.UtcDateTime).Value;
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new ChannelConversationSql(connection);

        await sql.UpsertAsync(stored, CancellationToken.None);
        var loaded = await sql.FindAsync("telegram", "482910337", CancellationToken.None);

        loaded.Should().BeEquivalentTo(stored, options => options.ComparingByMembers<ChannelConversation>());
        loaded!.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
    }

    /// <summary>
    ///     The conflict branch of the upsert replaces the session, agent and activity time and leaves the id and the
    ///     creation time alone. Red: add "CreatedAt" or "Id" to the DO UPDATE list and the row changes.
    /// </summary>
    [Fact]
    public async Task A_rebind_keeps_the_row_id_and_creation_time()
    {
        var map = new PostgresConversationMap(fixture.Db);
        var conversationId = new ConversationId("482910337");
        await map.BindAsync(
            new ConversationBinding("telegram", conversationId, new SessionId(_sessionId), new AgentId(_agentId), _at),
            CancellationToken.None);
        var first = await ReadRowAsync("telegram", "482910337");

        await map.BindAsync(
            new ConversationBinding("telegram", conversationId, new SessionId(_secondSessionId), new AgentId(_agentId), _at.AddHours(2)),
            CancellationToken.None);
        var second = await ReadRowAsync("telegram", "482910337");

        second.Id.Should().Be(first.Id);
        second.CreatedAt.Should().Be(_at.UtcDateTime);
        second.LastActivityAt.Should().Be(_at.AddHours(2).UtcDateTime);
        second.SessionId.Should().Be(_secondSessionId);
    }

    /// <summary>
    ///     Review focus 1: a row <c>ChannelConversation.Create</c> refuses, a blank channel id and empty session and
    ///     agent ids here, still loads, as written. Red: build the loaded row through <c>Create</c>, and the load fails.
    /// </summary>
    [Fact]
    public async Task A_row_Create_rejects_still_loads()
    {
        await using (var connection = new NpgsqlConnection(fixture.ConnectionString))
        {
            await connection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO "ChannelConversations"
                    ("Id", "ChannelId", "ConversationId", "SessionId", "AgentId", "CreatedAt", "LastActivityAt")
                VALUES (@id, '   ', 'legacy-chat', @empty, @empty, @at, @at)
                """,
                connection);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("empty", Guid.Empty);
            insert.Parameters.AddWithValue("at", _at.UtcDateTime);
            await insert.ExecuteNonQueryAsync();
        }

        var loaded = (await new PostgresConversationMap(fixture.Db)
            .GetAsync("   ", new ConversationId("legacy-chat"), CancellationToken.None)).Value;

        loaded.Should().NotBeNull();
        loaded!.ChannelId.Should().Be("   ");
        loaded.SessionId.Should().Be(new SessionId(Guid.Empty));
        loaded.AgentId.Should().Be(new AgentId(Guid.Empty));
        loaded.LastActivityAt.Should().Be(_at);
    }

    private async Task<(Guid Id, Guid SessionId, DateTime CreatedAt, DateTime LastActivityAt)> ReadRowAsync(
        string channelId, string conversationId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var select = new NpgsqlCommand(
            """
            SELECT "Id", "SessionId", "CreatedAt", "LastActivityAt" FROM "ChannelConversations"
            WHERE "ChannelId" = @channelId AND "ConversationId" = @conversationId
            """,
            connection);
        select.Parameters.AddWithValue("channelId", channelId);
        select.Parameters.AddWithValue("conversationId", conversationId);
        await using var reader = await select.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetDateTime(2), reader.GetDateTime(3));
    }
}
