namespace Daedalus.Infrastructure.Persistence.Orm.AgentMemories;

/// <summary>
///     Which memories a list, count or stream reads. An empty list or a null value is no filter on that column. The
///     caller normalises the tags and answers a blank query tag itself, because a blank tag matches nothing.
/// </summary>
/// <param name="OwnerIds">The owners to keep, or empty for all.</param>
/// <param name="AgentId">Only memories pinned to this agent, or null for any.</param>
/// <param name="Kinds">The kinds to keep, or empty for all.</param>
/// <param name="Tags">The tags each memory must carry, or empty for any.</param>
/// <param name="IncludeArchived">Whether archived memories are read too.</param>
/// <param name="IndexPending">Only memories with this index flag, or null for any.</param>
public sealed record AgentMemoryFilter(
    IReadOnlyList<string> OwnerIds,
    Guid? AgentId,
    IReadOnlyList<string> Kinds,
    IReadOnlyList<string> Tags,
    bool IncludeArchived,
    bool? IndexPending);
