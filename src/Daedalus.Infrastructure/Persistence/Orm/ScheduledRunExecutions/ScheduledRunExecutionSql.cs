using System.Data.Async;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using RunStep = Daedalus.Domain.Entities.RunStep;
using ScheduledRunExecution = Daedalus.Domain.Entities.ScheduledRunExecution;

namespace Daedalus.Infrastructure.Persistence.Orm.ScheduledRunExecutions;

/// <summary>
///     The SQL of the <c>"ScheduledRunExecutions"</c> table, over one open connection. Its public methods take and
///     return <see cref="ScheduledRunExecution"/>; the generated queries, their companions and the row records stay
///     inside.
/// </summary>
/// <remarks>
///     The reads a write follows carry the row's <c>xmin</c>, and every update is guarded by it, as EF Core's shadow
///     concurrency token guarded every save of the entity: a row another write changed since it was read throws
///     <see cref="ZeroAllocOrmConcurrencyException"/>. The insert is raw SQL, as the ORM has no LINQ insert and no
///     upsert (ruling R10), lists every column, and inserts nothing when the occurrence already has an execution. The
///     roles are stored comma-joined and the steps by their member names, as EF Core stored them (ruling R119).
/// </remarks>
public sealed partial class ScheduledRunExecutionSql(IAsyncDbConnection connection)
{
    /// <summary>
    ///     The claim of one occurrence: every column, and nothing when the unique
    ///     <c>("ScheduleId", "OccurrenceAt")</c> index already holds the occurrence. <c>internal</c> so the
    ///     column-drift test can compare its column list with the table's.
    /// </summary>
    internal const string InsertSql = """
        INSERT INTO "ScheduledRunExecutions"
            ("Id", "ScheduleId", "OccurrenceAt", "Step", "FailedAtStep", "Findings", "Digest", "ChannelId",
             "ConversationId", "PrincipalId", "Roles", "Attempts", "LastError", "CreatedAt", "UpdatedAt")
        VALUES (@id, @scheduleId, @occurrenceAt, @step, @failedAtStep, @findings, @digest, @channelId,
                @conversationId, @principalId, @roles, @attempts, @lastError, @createdAt, @updatedAt)
        ON CONFLICT ("ScheduleId", "OccurrenceAt") DO NOTHING
        """;

    /// <summary>
    ///     Inserts <paramref name="execution"/>'s row with every column as given, unless its schedule's occurrence
    ///     already has an execution.
    /// </summary>
    /// <param name="execution">The execution to store.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns><see langword="true"/> if the row was inserted; <see langword="false"/> if the occurrence was taken.</returns>
    public async Task<bool> TryInsertAsync(ScheduledRunExecution execution, IAsyncDbTransaction tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);

        var inserted = await Insert(
            execution.Id, execution.ScheduleId, execution.OccurrenceAt, execution.Step.ToString(),
            execution.FailedAtStep?.ToString(), execution.Findings, execution.Digest, execution.ChannelId,
            execution.ConversationId, execution.PrincipalId, ScheduledRunRoles.Join(execution.Roles),
            execution.Attempts, execution.LastError, execution.CreatedAt, execution.UpdatedAt, tx, ct)
            .ConfigureAwait(false);
        return inserted == 1;
    }

    /// <summary>Reads one execution with its version, in the caller's transaction, for a guarded write.</summary>
    /// <param name="id">The execution's id.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The execution and its version, or <see langword="null"/> if there is none.</returns>
    public async Task<VersionedScheduledRunExecution?> FindAsync(Guid id, IAsyncDbTransaction tx, CancellationToken ct)
    {
        var row = await FindRowInTransaction(id, tx, ct).ConfigureAwait(false);
        return row?.ToVersioned();
    }

    /// <summary>Reads one execution.</summary>
    /// <param name="id">The execution's id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The execution, or <see langword="null"/> if there is none.</returns>
    public async Task<ScheduledRunExecution?> FindAsync(Guid id, CancellationToken ct)
    {
        var row = await FindRow(id, ct).ConfigureAwait(false);
        return row?.ToVersioned().Execution;
    }

    /// <summary>
    ///     The latest occurrence of <paramref name="scheduleId"/> before <paramref name="before"/> whose execution is
    ///     <see cref="RunStep.Done"/>.
    /// </summary>
    /// <param name="scheduleId">The schedule.</param>
    /// <param name="before">The occurrence, in UTC, that only strictly earlier ones are counted before.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>That occurrence, in UTC, or <see langword="null"/> if there is none.</returns>
    public Task<DateTime?> LatestDoneOccurrenceBeforeAsync(Guid scheduleId, DateTime before, CancellationToken ct) =>
        LatestDoneOccurrenceBefore(scheduleId, before, ct);

    /// <summary>
    ///     Writes the columns <see cref="ScheduledRunExecution.RecordFindings"/>,
    ///     <see cref="ScheduledRunExecution.RecordDigest"/> and <see cref="ScheduledRunExecution.Complete"/> change, the
    ///     step, the two outputs and the update time, when the row still has the <c>xmin</c> it was read with.
    /// </summary>
    /// <param name="execution">The execution as advanced.</param>
    /// <param name="version">The row's <c>xmin</c> when it was read.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since it was read.</exception>
    public Task AdvanceAsync(ScheduledRunExecution execution, uint version, IAsyncDbTransaction tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return Advance(
            execution.Id, version, execution.Step, execution.Findings, execution.Digest, execution.UpdatedAt, tx, ct);
    }

    /// <summary>
    ///     Writes the columns <see cref="ScheduledRunExecution.Fail"/> changes, the step, the step it failed at, the
    ///     error, the attempts and the update time, when the row still has the <c>xmin</c> it was read with.
    /// </summary>
    /// <param name="execution">The execution as failed.</param>
    /// <param name="version">The row's <c>xmin</c> when it was read.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since it was read.</exception>
    public Task FailAsync(ScheduledRunExecution execution, uint version, IAsyncDbTransaction tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(execution);
        return Fail(
            execution.Id, version, execution.Step, execution.FailedAtStep, execution.LastError, execution.Attempts,
            execution.UpdatedAt, tx, ct);
    }

    [Command(InsertSql)]
    private partial Task<int> Insert(
        Guid id, Guid scheduleId, DateTime occurrenceAt, string step, string? failedAtStep, string? findings,
        string? digest, string channelId, string conversationId, string principalId, string roles, int attempts,
        string? lastError, DateTime createdAt, DateTime updatedAt, IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<ScheduledRunExecutionRow?> FindRowInTransaction(
        Guid id, IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<ScheduledRunExecutionRow?> FindRow(Guid id, CancellationToken ct);

    private static IQuery<ScheduledRunExecutionRow> FindRowQuery(Guid id) =>
        Sql.Table<StoredScheduledRunExecution>()
            .Where(e => e.Id == id)
            .Select(e => new ScheduledRunExecutionRow(
                e.Id, e.ScheduleId, e.OccurrenceAt, e.Step, e.FailedAtStep, e.Findings, e.Digest, e.ChannelId,
                e.ConversationId, e.PrincipalId, e.Roles, e.Attempts, e.LastError, e.CreatedAt, e.UpdatedAt,
                Sql.Xmin(e)));

    [LinqQuery(nameof(LatestDoneOccurrenceBeforeQuery))]
    private partial Task<DateTime?> LatestDoneOccurrenceBefore(Guid scheduleId, DateTime before, CancellationToken ct);

    private static IQuery<DateTime?> LatestDoneOccurrenceBeforeQuery(Guid scheduleId, DateTime before) =>
        Sql.Table<StoredScheduledRunExecution>()
            .Where(e => e.ScheduleId == scheduleId && e.Step == RunStep.Done && e.OccurrenceAt < before)
            .Max(e => (DateTime?)e.OccurrenceAt);

    [LinqCommand(nameof(AdvanceQuery), ThrowOnNoRows = true)]
    private partial Task Advance(
        Guid id, uint version, RunStep step, string? findings, string? digest, DateTime updatedAt,
        IAsyncDbTransaction tx, CancellationToken ct);

    private static int AdvanceQuery(
        Guid id, uint version, RunStep step, string? findings, string? digest, DateTime updatedAt) =>
        Sql.Table<StoredScheduledRunExecution>()
            .Where(e => e.Id == id && Sql.Xmin(e) == version)
            .ExecuteUpdate(set => set
                .SetProperty(e => e.Step, step)
                .SetProperty(e => e.Findings, findings)
                .SetProperty(e => e.Digest, digest)
                .SetProperty(e => e.UpdatedAt, updatedAt));

    [LinqCommand(nameof(FailQuery), ThrowOnNoRows = true)]
    private partial Task Fail(
        Guid id, uint version, RunStep step, RunStep? failedAtStep, string? lastError, int attempts,
        DateTime updatedAt, IAsyncDbTransaction tx, CancellationToken ct);

    private static int FailQuery(
        Guid id, uint version, RunStep step, RunStep? failedAtStep, string? lastError, int attempts,
        DateTime updatedAt) =>
        Sql.Table<StoredScheduledRunExecution>()
            .Where(e => e.Id == id && Sql.Xmin(e) == version)
            .ExecuteUpdate(set => set
                .SetProperty(e => e.Step, step)
                .SetProperty(e => e.FailedAtStep, failedAtStep)
                .SetProperty(e => e.LastError, lastError)
                .SetProperty(e => e.Attempts, attempts)
                .SetProperty(e => e.UpdatedAt, updatedAt));
}
