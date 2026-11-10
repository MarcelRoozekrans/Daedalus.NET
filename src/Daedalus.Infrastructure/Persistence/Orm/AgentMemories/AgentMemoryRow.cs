using Daedalus.Domain.Entities;

namespace Daedalus.Infrastructure.Persistence.Orm.AgentMemories;

/// <summary>One <c>"AgentMemories"</c> row, every column, as <see cref="AgentMemorySql"/> reads it.</summary>
internal sealed record AgentMemoryRow(
    Guid Id,
    string OwnerId,
    Guid? AgentId,
    string Kind,
    string Text,
    IReadOnlyList<string> Tags,
    string Source,
    double Importance,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? LastRecalledAt,
    int RecallCount,
    bool IsArchived,
    bool IndexPending)
{
    /// <summary>The memory the row holds, as stored: <see cref="AgentMemory.Rehydrate"/> validates nothing.</summary>
    /// <returns>The domain memory.</returns>
    public AgentMemory ToDomain() =>
        AgentMemory.Rehydrate(
            Id, OwnerId, AgentId, Kind, Text, Tags, Source, Importance, CreatedAt, UpdatedAt, LastRecalledAt,
            RecallCount, IsArchived, IndexPending);
}
