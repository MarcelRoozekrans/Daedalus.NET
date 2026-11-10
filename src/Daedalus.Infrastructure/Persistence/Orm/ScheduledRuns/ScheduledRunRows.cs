using ScheduledRun = Daedalus.Domain.Entities.ScheduledRun;
using ScheduleOrigin = Daedalus.Domain.Entities.ScheduleOrigin;

namespace Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;

/// <summary>
///     A stored schedule and the <c>xmin</c> it was read with, the version a guarded write of it compares.
/// </summary>
/// <param name="Run">The schedule as stored.</param>
/// <param name="Version">The row's <c>xmin</c> when it was read.</param>
public sealed record VersionedScheduledRun(ScheduledRun Run, uint Version);

/// <summary>One <c>"ScheduledRuns"</c> row, every column and its <c>xmin</c>, as <see cref="ScheduledRunSql"/> reads it.</summary>
internal sealed record ScheduledRunRow(
    Guid Id,
    string Name,
    string Cron,
    string Trigger,
    string ChannelId,
    string ConversationId,
    string PrincipalId,
    string Roles,
    ScheduleOrigin Origin,
    DateTime NextRunAt,
    DateTime? LastRunAt,
    bool Enabled,
    int MissedOccurrences,
    string? Repository,
    uint Version)
{
    /// <summary>The schedule the row holds, as stored: <see cref="ScheduledRun.Rehydrate"/> validates nothing.</summary>
    /// <returns>The schedule and its version.</returns>
    public VersionedScheduledRun ToVersioned() =>
        new(
            ScheduledRun.Rehydrate(
                Id, Name, Cron, Trigger, ChannelId, ConversationId, PrincipalId, ScheduledRunRoles.Split(Roles),
                Repository, Origin, NextRunAt, LastRunAt, Enabled, MissedOccurrences),
            Version);
}

/// <summary>
///     The stored form of a roles list, one comma-joined string, exactly as EF Core's value converter wrote and read it:
///     joined with <c>','</c>, split with empty entries removed.
/// </summary>
internal static class ScheduledRunRoles
{
    /// <summary>The stored form of <paramref name="roles"/>.</summary>
    /// <param name="roles">The roles, in order.</param>
    /// <returns>The roles joined with commas.</returns>
    public static string Join(IReadOnlyList<string> roles) => string.Join(',', roles);

    /// <summary>The roles of a stored value.</summary>
    /// <param name="stored">The comma-joined column value.</param>
    /// <returns>The roles, in stored order, without empty entries.</returns>
    public static string[] Split(string stored) => stored.Split(',', StringSplitOptions.RemoveEmptyEntries);
}
