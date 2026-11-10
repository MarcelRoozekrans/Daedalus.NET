using Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;
using RunStep = Daedalus.Domain.Entities.RunStep;
using ScheduledRunExecution = Daedalus.Domain.Entities.ScheduledRunExecution;

namespace Daedalus.Infrastructure.Persistence.Orm.ScheduledRunExecutions;

/// <summary>
///     A stored execution and the <c>xmin</c> it was read with, the version a guarded write of it compares.
/// </summary>
/// <param name="Execution">The execution as stored.</param>
/// <param name="Version">The row's <c>xmin</c> when it was read.</param>
public sealed record VersionedScheduledRunExecution(ScheduledRunExecution Execution, uint Version);

/// <summary>
///     One <c>"ScheduledRunExecutions"</c> row, every column and its <c>xmin</c>, as
///     <see cref="ScheduledRunExecutionSql"/> reads it.
/// </summary>
internal sealed record ScheduledRunExecutionRow(
    Guid Id,
    Guid ScheduleId,
    DateTime OccurrenceAt,
    RunStep Step,
    RunStep? FailedAtStep,
    string? Findings,
    string? Digest,
    string ChannelId,
    string ConversationId,
    string PrincipalId,
    string Roles,
    int Attempts,
    string? LastError,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    uint Version)
{
    /// <summary>
    ///     The execution the row holds, as stored: <see cref="ScheduledRunExecution.Rehydrate"/> validates nothing.
    /// </summary>
    /// <returns>The execution and its version.</returns>
    public VersionedScheduledRunExecution ToVersioned() =>
        new(
            ScheduledRunExecution.Rehydrate(
                Id, ScheduleId, OccurrenceAt, Step, FailedAtStep, Findings, Digest, ChannelId, ConversationId,
                PrincipalId, ScheduledRunRoles.Split(Roles), Attempts, LastError, CreatedAt, UpdatedAt),
            Version);
}
