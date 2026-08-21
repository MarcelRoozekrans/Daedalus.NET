using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Binds one external chat (a Telegram chat, the CLI's single "console" conversation, …) to the Thalos agent
///     session currently serving it. Domain stays framework-free: <see cref="SessionId"/> and <see cref="AgentId"/>
///     are the <c>Guid</c> backing the Thalos ULID-based <c>[TypedId]</c> types (<c>SessionId.Value</c> /
///     <c>AgentId.Value</c>), never the 26-character rendered form; timestamps are UTC.
/// </summary>
/// <remarks>
///     The natural key is composite: (<see cref="ChannelId"/>, <see cref="ConversationId"/>) — one row per external
///     chat, regardless of channel. <see cref="Entity{TId}"/> carries a single id, so this aggregate keeps a surrogate
///     <c>Guid</c> primary key (like <see cref="AgentMessage"/>'s surrogate id next to its unique
///     (SessionId, Sequence) pair) and the natural key is enforced by a UNIQUE database index on
///     (<see cref="ChannelId"/>, <see cref="ConversationId"/>), configured in
///     <c>ChannelConversationConfiguration</c>. A synthesised string id (e.g. "telegram:123") was considered and
///     rejected: it would make correctness depend on a separator no <see cref="ChannelId"/> value may ever contain,
///     where a plain composite unique index needs no such argument and matches the codebase's existing precedent for
///     composite natural keys.
/// </remarks>
public sealed class ChannelConversation : Entity<Guid>
{
    /// <summary>Maximum length of <see cref="ChannelId"/> (e.g. "telegram", "console").</summary>
    public const int MaxChannelIdLength = 32;

    /// <summary>Maximum length of <see cref="ConversationId"/> (the Telegram chat id or "console", as a string).</summary>
    public const int MaxConversationIdLength = 128;

    /// <summary>Gets which channel adapter owns this conversation (e.g. "telegram", "console").</summary>
    public string ChannelId { get; private set; } = string.Empty;

    /// <summary>Gets the channel-specific conversation identifier (the Telegram chat id or "console", as a string).</summary>
    public string ConversationId { get; private set; } = string.Empty;

    /// <summary>Gets the Thalos session currently serving this conversation (the Guid backing Thalos's <c>SessionId</c>).</summary>
    public Guid SessionId { get; private set; }

    /// <summary>Gets the Thalos agent definition running the session (the Guid backing Thalos's <c>AgentId</c>).</summary>
    public Guid AgentId { get; private set; }

    /// <summary>Gets when the conversation was created (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Gets when the conversation was last active (UTC).</summary>
    public DateTime LastActivityAt { get; private set; }

    private ChannelConversation() { } // EF Core

    /// <summary>Creates a new channel-to-session binding. Timestamps are supplied by the caller (UTC).</summary>
    /// <param name="channelId">Which channel adapter owns this conversation (e.g. "telegram", "console").</param>
    /// <param name="conversationId">The channel-specific conversation identifier.</param>
    /// <param name="sessionId">The Thalos session serving this conversation (the Guid backing <c>SessionId</c>).</param>
    /// <param name="agentId">The Thalos agent definition running the session (the Guid backing <c>AgentId</c>).</param>
    /// <param name="utcNow">The creation timestamp (UTC).</param>
    /// <returns>A Result containing the new binding or the first validation error.</returns>
    public static Result<ChannelConversation> Create(
        string channelId, string conversationId, Guid sessionId, Guid agentId, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(channelId))
            return Result.Failure<ChannelConversation>("Channel id is required.");

        if (channelId.Length > MaxChannelIdLength)
            return Result.Failure<ChannelConversation>($"Channel id must be at most {MaxChannelIdLength} characters.");

        // Unlike ChannelId, a blank/empty ConversationId is deliberately NOT rejected here: ConversationId.Value
        // (Thalos.NET) normalises a defaulted struct to "" on read, and PostgresConversationMap's IConversationMap
        // contract requires that exact empty string to round-trip through Bind/Get. ConversationId is still bounded
        // by MaxConversationIdLength below - only the "non-blank" rule is gone, and only for this field.
        if (conversationId is null)
            return Result.Failure<ChannelConversation>("Conversation id is required.");

        if (conversationId.Length > MaxConversationIdLength)
            return Result.Failure<ChannelConversation>($"Conversation id must be at most {MaxConversationIdLength} characters.");

        if (sessionId == Guid.Empty)
            return Result.Failure<ChannelConversation>("Session id is required.");

        if (agentId == Guid.Empty)
            return Result.Failure<ChannelConversation>("Agent id is required.");

        return Result.Success(new ChannelConversation
        {
            Id = Guid.NewGuid(),
            ChannelId = channelId,
            ConversationId = conversationId,
            SessionId = sessionId,
            AgentId = agentId,
            CreatedAt = utcNow,
            LastActivityAt = utcNow,
        });
    }
}
