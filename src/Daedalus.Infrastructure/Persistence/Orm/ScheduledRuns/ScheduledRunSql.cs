using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using ScheduledRun = Daedalus.Domain.Entities.ScheduledRun;
using ScheduleOrigin = Daedalus.Domain.Entities.ScheduleOrigin;

namespace Daedalus.Infrastructure.Persistence.Orm.ScheduledRuns;

/// <summary>
///     The SQL of the <c>"ScheduledRuns"</c> table, over one open connection, each statement in the caller's
///     transaction. Its public methods take and return <see cref="ScheduledRun"/>; the generated queries, their
///     companions and the row records stay inside.
/// </summary>
/// <remarks>
///     Every read carries the row's <c>xmin</c>, and every update is guarded by it, as EF Core's shadow concurrency
///     token guarded every save of the entity: a row another write changed since it was read throws
///     <see cref="ZeroAllocOrmConcurrencyException"/>. Each update writes only the columns its domain methods change.
///     The insert is raw SQL, as the ORM has no LINQ insert (ruling R10), and lists every column. The roles are
///     stored comma-joined and the origin by its member name, as EF Core stored them (ruling R113).
/// </remarks>
public sealed partial class ScheduledRunSql(IAsyncDbConnection connection)
{
    private const string InsertSql = """
        INSERT INTO "ScheduledRuns"
            ("Id", "Name", "Cron", "Trigger", "ChannelId", "ConversationId", "PrincipalId", "Roles", "Origin",
             "NextRunAt", "LastRunAt", "Enabled", "MissedOccurrences", "Repository")
        VALUES (@id, @name, @cron, @trigger, @channelId, @conversationId, @principalId, @roles, @origin,
                @nextRunAt, @lastRunAt, @enabled, @missedOccurrences, @repository)
        """;

    /// <summary>Inserts <paramref name="run"/>'s row with every column as given.</summary>
    /// <param name="run">The schedule to store.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public async Task InsertAsync(ScheduledRun run, IAsyncDbTransaction tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);

        await Insert(
            run.Id, run.Name, run.Cron, run.Trigger, run.ChannelId, run.ConversationId, run.PrincipalId,
            ScheduledRunRoles.Join(run.Roles), run.Origin.ToString(), run.NextRunAt, run.LastRunAt, run.Enabled,
            run.MissedOccurrences, run.Repository, tx, ct).ConfigureAwait(false);
    }

    /// <summary>
    ///     Reads every enabled schedule whose next occurrence is at or before <paramref name="now"/>, by id, so two
    ///     sweeps that write the same rows take their row locks in the same order.
    /// </summary>
    /// <param name="now">The current instant, in UTC.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The due schedules, each with its version.</returns>
    public async Task<IReadOnlyList<VersionedScheduledRun>> ListDueAsync(
        DateTime now, IAsyncDbTransaction tx, CancellationToken ct)
    {
        var rows = await ListDue(now, tx, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToVersioned());
    }

    /// <summary>Reads one schedule in the caller's transaction.</summary>
    /// <param name="id">The schedule's id.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The schedule, or <see langword="null"/> if there is none.</returns>
    public async Task<ScheduledRun?> FindAsync(Guid id, IAsyncDbTransaction tx, CancellationToken ct)
    {
        var row = await FindRowInTransaction(id, tx, ct).ConfigureAwait(false);
        return row?.ToVersioned().Run;
    }

    /// <summary>Reads one schedule.</summary>
    /// <param name="id">The schedule's id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The schedule, or <see langword="null"/> if there is none.</returns>
    public async Task<ScheduledRun?> FindAsync(Guid id, CancellationToken ct)
    {
        var row = await FindRow(id, ct).ConfigureAwait(false);
        return row?.ToVersioned().Run;
    }

    /// <summary>Reads every schedule, enabled or not, by name.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>All schedules.</returns>
    public async Task<IReadOnlyList<ScheduledRun>> ListAllAsync(CancellationToken ct)
    {
        var rows = await ListAll(ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToVersioned().Run);
    }

    /// <summary>Reads every schedule whose origin is <see cref="ScheduleOrigin.Config"/>, by id.</summary>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The configured schedules, each with its version.</returns>
    public async Task<IReadOnlyList<VersionedScheduledRun>> ListConfigAsync(IAsyncDbTransaction tx, CancellationToken ct)
    {
        var rows = await ListConfig(tx, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToVersioned());
    }

    /// <summary>
    ///     Writes the columns <see cref="ScheduledRun.AdvanceTo"/> changes, the next and last run and the missed count,
    ///     when the row still has the <c>xmin</c> it was read with.
    /// </summary>
    /// <param name="run">The schedule as advanced.</param>
    /// <param name="version">The row's <c>xmin</c> when it was read.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since it was read.</exception>
    public Task AdvanceAsync(ScheduledRun run, uint version, IAsyncDbTransaction tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        return Advance(run.Id, version, run.NextRunAt, run.LastRunAt, run.MissedOccurrences, tx, ct);
    }

    /// <summary>
    ///     Writes the column <see cref="ScheduledRun.Disable"/> changes, when the row still has the <c>xmin</c> it was
    ///     read with.
    /// </summary>
    /// <param name="id">The schedule's id.</param>
    /// <param name="version">The row's <c>xmin</c> when it was read.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since it was read.</exception>
    public Task DisableAsync(Guid id, uint version, IAsyncDbTransaction tx, CancellationToken ct) =>
        Disable(id, version, tx, ct);

    /// <summary>
    ///     Writes the columns <see cref="ScheduledRun.UpdateFromConfig"/>, <see cref="ScheduledRun.Enable"/> and
    ///     <see cref="ScheduledRun.Disable"/> change, the configured values and the enabled flag, when the row still has
    ///     the <c>xmin</c> it was read with. The name, origin and the sweep's columns are not written.
    /// </summary>
    /// <param name="run">The schedule as reconciled.</param>
    /// <param name="version">The row's <c>xmin</c> when it was read.</param>
    /// <param name="tx">The caller's transaction.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the row is written.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since it was read.</exception>
    public Task UpdateConfigAsync(ScheduledRun run, uint version, IAsyncDbTransaction tx, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        return UpdateConfig(
            run.Id, version, run.Cron, run.Trigger, run.ChannelId, run.ConversationId, run.PrincipalId,
            ScheduledRunRoles.Join(run.Roles), run.Repository, run.Enabled, tx, ct);
    }

    [Command(InsertSql)]
    private partial Task<int> Insert(
        Guid id, string name, string cron, string trigger, string channelId, string conversationId,
        string principalId, string roles, string origin, DateTime nextRunAt, DateTime? lastRunAt, bool enabled,
        int missedOccurrences, string? repository, IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<ScheduledRunRow?> FindRowInTransaction(Guid id, IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<ScheduledRunRow?> FindRow(Guid id, CancellationToken ct);

    private static IQuery<ScheduledRunRow> FindRowQuery(Guid id) =>
        Sql.Table<StoredScheduledRun>()
            .Where(r => r.Id == id)
            .Select(r => new ScheduledRunRow(
                r.Id, r.Name, r.Cron, r.Trigger, r.ChannelId, r.ConversationId, r.PrincipalId, r.Roles, r.Origin,
                r.NextRunAt, r.LastRunAt, r.Enabled, r.MissedOccurrences, r.Repository, Sql.Xmin(r)));

    [LinqQuery(nameof(ListDueQuery))]
    private partial Task<List<ScheduledRunRow>> ListDue(DateTime now, IAsyncDbTransaction tx, CancellationToken ct);

    private static IQuery<ScheduledRunRow> ListDueQuery(DateTime now) =>
        Sql.Table<StoredScheduledRun>()
            .Where(r => r.Enabled && r.NextRunAt <= now)
            .OrderBy(r => r.Id)
            .Select(r => new ScheduledRunRow(
                r.Id, r.Name, r.Cron, r.Trigger, r.ChannelId, r.ConversationId, r.PrincipalId, r.Roles, r.Origin,
                r.NextRunAt, r.LastRunAt, r.Enabled, r.MissedOccurrences, r.Repository, Sql.Xmin(r)));

    [LinqQuery(nameof(ListAllQuery))]
    private partial Task<List<ScheduledRunRow>> ListAll(CancellationToken ct);

    private static IQuery<ScheduledRunRow> ListAllQuery() =>
        Sql.Table<StoredScheduledRun>()
            .OrderBy(r => r.Name)
            .Select(r => new ScheduledRunRow(
                r.Id, r.Name, r.Cron, r.Trigger, r.ChannelId, r.ConversationId, r.PrincipalId, r.Roles, r.Origin,
                r.NextRunAt, r.LastRunAt, r.Enabled, r.MissedOccurrences, r.Repository, Sql.Xmin(r)));

    [LinqQuery(nameof(ListConfigQuery))]
    private partial Task<List<ScheduledRunRow>> ListConfig(IAsyncDbTransaction tx, CancellationToken ct);

    private static IQuery<ScheduledRunRow> ListConfigQuery() =>
        Sql.Table<StoredScheduledRun>()
            .Where(r => r.Origin == ScheduleOrigin.Config)
            .OrderBy(r => r.Id)
            .Select(r => new ScheduledRunRow(
                r.Id, r.Name, r.Cron, r.Trigger, r.ChannelId, r.ConversationId, r.PrincipalId, r.Roles, r.Origin,
                r.NextRunAt, r.LastRunAt, r.Enabled, r.MissedOccurrences, r.Repository, Sql.Xmin(r)));

    [LinqCommand(nameof(AdvanceQuery), ThrowOnNoRows = true)]
    private partial Task Advance(
        Guid id, uint version, DateTime nextRunAt, DateTime? lastRunAt, int missedOccurrences, IAsyncDbTransaction tx,
        CancellationToken ct);

    private static int AdvanceQuery(Guid id, uint version, DateTime nextRunAt, DateTime? lastRunAt, int missedOccurrences) =>
        Sql.Table<StoredScheduledRun>()
            .Where(r => r.Id == id && Sql.Xmin(r) == version)
            .ExecuteUpdate(set => set
                .SetProperty(r => r.NextRunAt, nextRunAt)
                .SetProperty(r => r.LastRunAt, lastRunAt)
                .SetProperty(r => r.MissedOccurrences, missedOccurrences));

    [LinqCommand(nameof(DisableQuery), ThrowOnNoRows = true)]
    private partial Task Disable(Guid id, uint version, IAsyncDbTransaction tx, CancellationToken ct);

    private static int DisableQuery(Guid id, uint version) =>
        Sql.Table<StoredScheduledRun>()
            .Where(r => r.Id == id && Sql.Xmin(r) == version)
            .ExecuteUpdate(set => set.SetProperty(r => r.Enabled, false));

    [LinqCommand(nameof(UpdateConfigQuery), ThrowOnNoRows = true)]
    private partial Task UpdateConfig(
        Guid id, uint version, string cron, string trigger, string channelId, string conversationId,
        string principalId, string roles, string? repository, bool enabled, IAsyncDbTransaction tx,
        CancellationToken ct);

    private static int UpdateConfigQuery(
        Guid id, uint version, string cron, string trigger, string channelId, string conversationId,
        string principalId, string roles, string? repository, bool enabled) =>
        Sql.Table<StoredScheduledRun>()
            .Where(r => r.Id == id && Sql.Xmin(r) == version)
            .ExecuteUpdate(set => set
                .SetProperty(r => r.Cron, cron)
                .SetProperty(r => r.Trigger, trigger)
                .SetProperty(r => r.ChannelId, channelId)
                .SetProperty(r => r.ConversationId, conversationId)
                .SetProperty(r => r.PrincipalId, principalId)
                .SetProperty(r => r.Roles, roles)
                .SetProperty(r => r.Repository, repository)
                .SetProperty(r => r.Enabled, enabled));
}
