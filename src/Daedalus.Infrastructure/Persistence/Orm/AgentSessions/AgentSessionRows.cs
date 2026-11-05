using Daedalus.Domain.Entities;
using ZeroAlloc.ORM;

namespace Daedalus.Infrastructure.Persistence.Orm.AgentSessions;

/// <summary>One <c>"AgentSessions"</c> row, every column but the dead row version, as <see cref="AgentSessionSql"/> reads it.</summary>
internal sealed record AgentSessionRow(
    Guid Id,
    Guid AgentId,
    string OwnerId,
    AgentSessionState State,
    DateTime CreatedAt,
    DateTime LastActivityAt,
    int TurnCount,
    long TotalInputTokens,
    long TotalOutputTokens)
{
    /// <summary>The session the row holds, as stored: <see cref="AgentSession.Rehydrate"/> validates nothing.</summary>
    /// <returns>The domain session.</returns>
    public AgentSession ToDomain() =>
        AgentSession.Rehydrate(
            Id, AgentId, OwnerId, State, CreatedAt, LastActivityAt, TurnCount, TotalInputTokens, TotalOutputTokens);
}

/// <summary>One <c>"AgentMessages"</c> row as the bulk insert writes it, every column.</summary>
internal sealed record AgentMessageInsertRow(
    Guid Id,
    Guid SessionId,
    int Sequence,
    string Role,
    [Json] string ContentJson,
    int? InputTokens,
    int? OutputTokens,
    string? ModelId,
    DateTime CreatedAt);
