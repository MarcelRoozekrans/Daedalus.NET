using Daedalus.Agents.Channels;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Channels;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Channels;

/// <summary>
///     Integration tests for <see cref="PostgresConversationMap"/> against a real PostgreSQL database. Covers the
///     <see cref="IConversationMap"/> contract exactly: unbound is success(null) not a failure, bind replaces rather
///     than duplicates, bindings are scoped per channel, unbind is idempotent, and lookups are keyed on
///     <see cref="ConversationId.Value"/> rather than the struct (so <c>default(ConversationId)</c> finds a binding
///     stored under <c>new ConversationId("")</c>).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresConversationMapTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid _sessionId = new(0x6f9b1c2a, 0x4d3e, 0x4f5a, 0x8b, 0x6c, 0x1a, 0x2b, 0x3c, 0x4d, 0x5e, 0x6f);
    private static readonly Guid _agentId = new(0x9e8d7c6b, 0x5a4f, 0x4321, 0x98, 0x76, 0xfe, 0xdc, 0xba, 0x09, 0x87, 0x65);
    private static readonly Guid _secondSessionId = new(0x11111111, 0x2222, 0x3333, 0x44, 0x44, 0x55, 0x55, 0x55, 0x55, 0x55, 0x55);
    private static readonly Guid _secondAgentId = new(0x66666666, 0x7777, 0x8888, 0x99, 0x99, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa, 0xaa);
    private static readonly Guid _consoleSessionId = new(0x22222222, 0x3333, 0x4444, 0x55, 0x55, 0x66, 0x66, 0x66, 0x66, 0x66, 0x66);
    private static readonly DateTimeOffset _lastActivityAt = new(2026, 8, 20, 9, 30, 15, TimeSpan.Zero);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private PostgresConversationMap CreateMap() => new(new FixtureDbContextFactory(fixture));

    [Fact]
    public async Task Get_of_an_unbound_conversation_is_success_null_not_a_failure()
    {
        var map = CreateMap();

        var result = await map.GetAsync("telegram", new ConversationId("482910337"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Bind_then_get_round_trips_every_member()
    {
        var map = CreateMap();
        var binding = new ConversationBinding(
            "telegram", new ConversationId("482910337"), new SessionId(_sessionId), new AgentId(_agentId), _lastActivityAt);

        var bound = await map.BindAsync(binding, CancellationToken.None);
        bound.IsSuccess.Should().BeTrue();

        var result = await map.GetAsync("telegram", new ConversationId("482910337"), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        var loaded = result.Value;
        loaded.Should().NotBeNull();
        loaded!.ChannelId.Should().Be("telegram");
        loaded.ConversationId.Should().Be(new ConversationId("482910337"));
        loaded.SessionId.Should().Be(new SessionId(_sessionId));
        loaded.AgentId.Should().Be(new AgentId(_agentId));
        loaded.LastActivityAt.Should().Be(_lastActivityAt);
    }

    [Fact]
    public async Task Bind_twice_replaces_rather_than_duplicating()
    {
        var map = CreateMap();
        var conversationId = new ConversationId("482910337");
        var firstSessionId = new SessionId(_sessionId);
        var secondSessionId = new SessionId(_secondSessionId);
        var secondAgentId = new AgentId(_secondAgentId);
        var secondActivityAt = _lastActivityAt.AddMinutes(5);

        await map.BindAsync(
            new ConversationBinding("telegram", conversationId, firstSessionId, new AgentId(_agentId), _lastActivityAt),
            CancellationToken.None);
        await map.BindAsync(
            new ConversationBinding("telegram", conversationId, secondSessionId, secondAgentId, secondActivityAt),
            CancellationToken.None);

        await using var db = fixture.CreateDbContext();
        var rows = await db.ChannelConversations
            .Where(c => c.ChannelId == "telegram" && c.ConversationId == "482910337")
            .ToListAsync();
        rows.Should().HaveCount(1, "the second bind must replace, not duplicate, the row");

        var result = await map.GetAsync("telegram", conversationId, CancellationToken.None);
        result.Value.Should().NotBeNull();
        result.Value!.SessionId.Should().Be(secondSessionId, "the second bind's values must win");
        result.Value.AgentId.Should().Be(secondAgentId);
        result.Value.LastActivityAt.Should().Be(secondActivityAt);
    }

    [Fact]
    public async Task Bindings_are_scoped_by_channel()
    {
        var map = CreateMap();
        var conversationId = new ConversationId("482910337");
        var telegramSessionId = new SessionId(_sessionId);
        var consoleSessionId = new SessionId(_consoleSessionId);

        await map.BindAsync(
            new ConversationBinding("telegram", conversationId, telegramSessionId, new AgentId(_agentId), _lastActivityAt),
            CancellationToken.None);
        await map.BindAsync(
            new ConversationBinding("console", conversationId, consoleSessionId, new AgentId(_agentId), _lastActivityAt),
            CancellationToken.None);

        var telegramResult = await map.GetAsync("telegram", conversationId, CancellationToken.None);
        var consoleResult = await map.GetAsync("console", conversationId, CancellationToken.None);

        telegramResult.Value.Should().NotBeNull();
        telegramResult.Value!.SessionId.Should().Be(telegramSessionId);
        consoleResult.Value.Should().NotBeNull();
        consoleResult.Value!.SessionId.Should().Be(consoleSessionId);
    }

    [Fact]
    public async Task Unbind_removes_the_binding_and_unbind_again_still_succeeds()
    {
        var map = CreateMap();
        var conversationId = new ConversationId("482910337");
        await map.BindAsync(
            new ConversationBinding("telegram", conversationId, new SessionId(_sessionId), new AgentId(_agentId), _lastActivityAt),
            CancellationToken.None);

        var firstUnbind = await map.UnbindAsync("telegram", conversationId, CancellationToken.None);
        firstUnbind.IsSuccess.Should().BeTrue();

        var afterUnbind = await map.GetAsync("telegram", conversationId, CancellationToken.None);
        afterUnbind.Value.Should().BeNull();

        var secondUnbind = await map.UnbindAsync("telegram", conversationId, CancellationToken.None);
        secondUnbind.IsSuccess.Should().BeTrue("removing an absent binding is idempotent");
    }

    [Fact]
    public async Task A_binding_stored_under_an_empty_conversation_id_is_retrievable_via_default()
    {
        // The sharp edge: ConversationId.Value normalises a defaulted struct to "" on read, but its
        // compiler-generated equality compares the private backing field, so default(ConversationId) and
        // new ConversationId("") are unequal structs that read the same string. The store must key on .Value.
        var map = CreateMap();
        var binding = new ConversationBinding(
            "telegram", new ConversationId(""), new SessionId(_sessionId), new AgentId(_agentId), _lastActivityAt);

        var bound = await map.BindAsync(binding, CancellationToken.None);
        bound.IsSuccess.Should().BeTrue();

        var result = await map.GetAsync("telegram", default, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull("a binding keyed on .Value must be found via the struct's default, too");
        result.Value!.SessionId.Should().Be(new SessionId(_sessionId));
    }

    /// <summary>The store disposes every context it creates, so the factory needs no tracking.</summary>
    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
