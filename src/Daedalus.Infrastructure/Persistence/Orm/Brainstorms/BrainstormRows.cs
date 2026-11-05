using Daedalus.Domain.Entities;

namespace Daedalus.Infrastructure.Persistence.Orm.Brainstorms;

/// <summary>
///     One <c>"BrainstormSessions"</c> row, every live column and its <c>xmin</c>, as <see cref="BrainstormSql"/> reads
///     it.
/// </summary>
internal sealed record BrainstormSessionRow(
    Guid Id,
    Guid ProjectId,
    BrainstormPhase Phase,
    string? DesignDocument,
    string? ImplementationPlan,
    bool PhaseCompleteSignaled,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    uint RowVersion)
{
    /// <summary>The session the row holds, as stored: <see cref="BrainstormSession.Rehydrate"/> validates nothing.</summary>
    /// <param name="messages">The session's messages, empty for a read that loads none.</param>
    /// <returns>The domain session.</returns>
    public BrainstormSession ToDomain(IEnumerable<BrainstormMessage> messages) =>
        BrainstormSession.Rehydrate(
            Id, ProjectId, Phase, DesignDocument, ImplementationPlan, PhaseCompleteSignaled, CreatedAt, CompletedAt,
            RowVersion, messages);
}

/// <summary>One <c>"BrainstormMessages"</c> row, every column, as <see cref="BrainstormSql"/> reads it.</summary>
internal sealed record BrainstormMessageRow(
    Guid Id,
    Guid BrainstormSessionId,
    MessageRole Role,
    string Content,
    BrainstormPhase Phase,
    DateTime CreatedAt)
{
    /// <summary>The message the row holds, as stored: <see cref="BrainstormMessage.Rehydrate"/> validates nothing.</summary>
    /// <returns>The domain message.</returns>
    public BrainstormMessage ToDomain() =>
        BrainstormMessage.Rehydrate(Id, BrainstormSessionId, Role, Content, Phase, CreatedAt);
}

/// <summary>
///     One <c>"BrainstormMessages"</c> row as the bulk insert writes it, every column; the enums bind as the integers EF
///     Core stored.
/// </summary>
internal sealed record BrainstormMessageInsertRow(
    Guid Id,
    Guid BrainstormSessionId,
    int Role,
    string Content,
    int Phase,
    DateTime CreatedAt);
