using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Channels;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Channels;

/// <summary>
///     Thalos conversation map over <see cref="ApplicationDbContext"/> (table <c>ChannelConversations</c>): remembers
///     which agent session is currently serving which external chat. Fresh short-lived DbContext per call (the store
///     is a singleton), same patterns as <see cref="Skills.PostgresSkillStore"/> and
///     <see cref="Memory.PostgresMemoryStore"/>.
/// </summary>
/// <remarks>
///     <see cref="ConversationId"/> is a hand-written <c>record struct</c> whose <c>Value</c> normalises a defaulted
///     instance to <see cref="string.Empty"/> on read, but whose compiler-generated equality compares the private
///     backing field — so <c>default(ConversationId)</c> and <c>new ConversationId("")</c> are unequal structs that
///     read the same string. Every lookup here is therefore keyed on <c>conversationId.Value</c>, never on the struct.
/// </remarks>
/// <remarks>
///     <see cref="BindAsync"/> is a genuine database upsert (<c>INSERT … ON CONFLICT … DO UPDATE</c>) against the
///     unique index on <c>(ChannelId, ConversationId)</c> that <c>ChannelConversationConfiguration</c> enforces,
///     rather than a read-then-write. A read-then-write has a TOCTOU window between the read and the save; the
///     Telegram poller is single-instance, but the CLI host can run concurrently against the same database, so two
///     processes really can race to bind the same conversation, and only a database-level upsert closes that window.
///     A raw <c>INSERT</c> also sidesteps <c>ChannelConversation.Create</c>'s "conversation id is required" rule —
///     deliberately: that domain rule guards normal creation, but the sharp edge this store must support (a binding
///     stored under <see cref="ConversationId"/>'s own empty-string default) is an <see cref="IConversationMap"/>
///     contract requirement, not a case that should be rejected as invalid input.
/// </remarks>
public sealed class PostgresConversationMap(IDbContextFactory<ApplicationDbContext> contextFactory) : IConversationMap
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    /// <inheritdoc />
    public async ValueTask<Result<ConversationBinding?, AgentError>> GetAsync(
        string channelId, ConversationId conversationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channelId);

        var conversationKey = conversationId.Value;
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.ChannelConversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.ChannelId == channelId && c.ConversationId == conversationKey, ct)
            .ConfigureAwait(false);

        // Unbound is the normal state of a first message, not a failure - see the IConversationMap contract.
        return Result<ConversationBinding?, AgentError>.Success(row is null ? null : ToBinding(row));
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> BindAsync(ConversationBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        var channelId = binding.ChannelId;
        var conversationKey = binding.ConversationId.Value;
        var sessionGuid = binding.SessionId.Value;
        var agentGuid = binding.AgentId.Value;
        var lastActivityAt = binding.LastActivityAt.UtcDateTime;

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // A true upsert on the unique (ChannelId, ConversationId) index - see the upsert remark on the type. This
        // is a raw statement rather than a SaveChanges round trip, so failures surface as the underlying Npgsql
        // exception directly rather than as a wrapped DbUpdateException, matching the session-store propagation
        // policy without needing a catch here.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "ChannelConversations" ("Id", "ChannelId", "ConversationId", "SessionId", "AgentId", "CreatedAt", "LastActivityAt")
             VALUES ({Guid.NewGuid()}, {channelId}, {conversationKey}, {sessionGuid}, {agentGuid}, {lastActivityAt}, {lastActivityAt})
             ON CONFLICT ("ChannelId", "ConversationId")
             DO UPDATE SET "SessionId" = EXCLUDED."SessionId", "AgentId" = EXCLUDED."AgentId", "LastActivityAt" = EXCLUDED."LastActivityAt"
             """, ct).ConfigureAwait(false);

        return UnitResult<AgentError>.Success();
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> UnbindAsync(string channelId, ConversationId conversationId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(channelId);

        var conversationKey = conversationId.Value;
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // ExecuteDeleteAsync affecting zero rows is not an error - removing an absent binding succeeds (idempotent).
        await db.ChannelConversations
            .Where(c => c.ChannelId == channelId && c.ConversationId == conversationKey)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return UnitResult<AgentError>.Success();
    }

    private static ConversationBinding ToBinding(Domain.Entities.ChannelConversation row) => new(
        row.ChannelId,
        new ConversationId(row.ConversationId),
        new SessionId(row.SessionId),
        new AgentId(row.AgentId),
        new DateTimeOffset(row.LastActivityAt, TimeSpan.Zero));
}
