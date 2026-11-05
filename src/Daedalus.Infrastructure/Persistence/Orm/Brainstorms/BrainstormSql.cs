using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using BrainstormMessage = Daedalus.Domain.Entities.BrainstormMessage;
using BrainstormPhase = Daedalus.Domain.Entities.BrainstormPhase;
using BrainstormSession = Daedalus.Domain.Entities.BrainstormSession;

namespace Daedalus.Infrastructure.Persistence.Orm.Brainstorms;

/// <summary>
///     The SQL of the <c>"BrainstormSessions"</c> and <c>"BrainstormMessages"</c> tables, over one open connection. Its
///     public methods take and return <see cref="BrainstormSession"/>; the generated queries, their companions and the
///     row records stay inside.
/// </summary>
/// <remarks>
///     A session and its messages read as one command of two statements, a tuple, in place of EF Core's
///     <c>Include</c>. Messages are insert-only: no code changes or removes one, so a save writes the session's columns
///     under the <c>xmin</c> it was read with and inserts its messages with <c>ON CONFLICT ("Id") DO NOTHING</c>, which
///     stores the ones added since the read and skips the stored ones, as EF inserted only the added ones. Both writes
///     share one transaction, as EF's one <c>SaveChanges</c> did. The inserts are raw SQL, as the ORM has no LINQ insert
///     (ruling R10), and list every column; migration 2 dropped the dead <c>bytea</c> <c>"RowVersion"</c>.
/// </remarks>
public sealed partial class BrainstormSql(IAsyncDbConnection connection)
{
    private const string InsertSessionSql = """
        INSERT INTO "BrainstormSessions"
            ("Id", "ProjectId", "Phase", "DesignDocument", "ImplementationPlan", "PhaseCompleteSignaled", "CreatedAt",
             "CompletedAt")
        VALUES (@id, @projectId, @phase, @designDocument, @implementationPlan, @phaseCompleteSignaled, @createdAt,
                @completedAt)
        """;

    private const string InsertMessagesSql = """
        INSERT INTO "BrainstormMessages" ("Id", "BrainstormSessionId", "Role", "Content", "Phase", "CreatedAt")
        VALUES (@Id, @BrainstormSessionId, @Role, @Content, @Phase, @CreatedAt)
        ON CONFLICT ("Id") DO NOTHING
        """;

    /// <summary>
    ///     Inserts <paramref name="session"/>'s row with every live column as given, and its messages, in one
    ///     transaction: neither is stored without the other.
    /// </summary>
    /// <param name="session">The session to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    public async Task InsertAsync(BrainstormSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await InsertSession(
            session.Id, session.ProjectId, (int)session.Phase, session.DesignDocument, session.ImplementationPlan,
            session.PhaseCompleteSignaled, session.CreatedAt, session.CompletedAt, tx, ct).ConfigureAwait(false);
        await InsertMessagesAsync(session.Messages, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the session with <paramref name="id"/> and its messages, in one round trip.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>
    ///     The session as stored with its row version and its messages, oldest first and then by id; or null when there
    ///     is none.
    /// </returns>
    public async Task<BrainstormSession?> FindWithMessagesAsync(Guid id, CancellationToken ct)
    {
        var (head, messages) = await FindWithMessagesRows(id, ct).ConfigureAwait(false);
        return head?.ToDomain(messages.ConvertAll(m => m.ToDomain()));
    }

    /// <summary>Reads the sessions of <paramref name="projectId"/>, newest first, without their messages.</summary>
    /// <param name="projectId">The project id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The sessions as stored, ordered by <c>CreatedAt</c> then id, both descending.</returns>
    public async Task<IReadOnlyList<BrainstormSession>> ListByProjectAsync(Guid projectId, CancellationToken ct)
    {
        var rows = await ListByProject(projectId, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain([]));
    }

    /// <summary>Whether a session with <paramref name="id"/> is stored.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>True when the row exists.</returns>
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Exists(id, ct);

    /// <summary>
    ///     Saves <paramref name="session"/> in one transaction: first the columns its domain methods change, the phase,
    ///     the phase-complete flag, the completion time, the design document and the implementation plan, when the row
    ///     still has the <c>xmin</c> the session was read with; then its messages, each inserted unless its id is stored.
    ///     A failure of either write rolls both back.
    /// </summary>
    /// <param name="session">The session as changed, carrying the row version it was read with.</param>
    /// <param name="ct">Cancels the save.</param>
    /// <returns>A task that completes when the transaction is committed.</returns>
    /// <exception cref="ZeroAllocOrmConcurrencyException">The row was changed or deleted since the session was read.</exception>
    public async Task SaveAsync(BrainstormSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await Update(
            session.Id, session.RowVersion, session.Phase, session.PhaseCompleteSignaled, session.CompletedAt,
            session.DesignDocument, session.ImplementationPlan, tx, ct).ConfigureAwait(false);
        await InsertMessagesAsync(session.Messages, tx, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    private async Task InsertMessagesAsync(
        IReadOnlyList<BrainstormMessage> messages, IAsyncDbTransaction tx, CancellationToken ct)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var rows = new BrainstormMessageInsertRow[messages.Count];
        for (var i = 0; i < messages.Count; i++)
        {
            var m = messages[i];
            rows[i] = new BrainstormMessageInsertRow(
                m.Id, m.BrainstormSessionId, (int)m.Role, m.Content, (int)m.Phase, m.CreatedAt);
        }

        await InsertMessages(rows, tx, ct).ConfigureAwait(false);
    }

    [Command(InsertSessionSql)]
    private partial Task<int> InsertSession(
        Guid id, Guid projectId, int phase, string? designDocument, string? implementationPlan,
        bool phaseCompleteSignaled, DateTime createdAt, DateTime? completedAt, IAsyncDbTransaction tx,
        CancellationToken ct);

    [Command(InsertMessagesSql, Kind = CommandKind.BulkInsert)]
    private partial Task<int> InsertMessages(
        IReadOnlyList<BrainstormMessageInsertRow> rows, IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(FindWithMessagesRowsQuery))]
    private partial Task<(BrainstormSessionRow? Head, List<BrainstormMessageRow> Messages)> FindWithMessagesRows(
        Guid id, CancellationToken ct);

    private static (IQuery<BrainstormSessionRow>, IQuery<BrainstormMessageRow>) FindWithMessagesRowsQuery(Guid id) =>
        (Sql.Table<BrainstormSession>()
            .Where(s => s.Id == id)
            .Select(s => new BrainstormSessionRow(
                s.Id, s.ProjectId, s.Phase, s.DesignDocument, s.ImplementationPlan, s.PhaseCompleteSignaled,
                s.CreatedAt, s.CompletedAt, s.RowVersion)),
         Sql.Table<BrainstormMessage>()
            .Where(m => m.BrainstormSessionId == id)
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new BrainstormMessageRow(
                m.Id, m.BrainstormSessionId, m.Role, m.Content, m.Phase, m.CreatedAt)));

    [LinqQuery(nameof(ListByProjectQuery))]
    private partial Task<List<BrainstormSessionRow>> ListByProject(Guid projectId, CancellationToken ct);

    private static IQuery<BrainstormSessionRow> ListByProjectQuery(Guid projectId) =>
        Sql.Table<BrainstormSession>()
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new BrainstormSessionRow(
                s.Id, s.ProjectId, s.Phase, s.DesignDocument, s.ImplementationPlan, s.PhaseCompleteSignaled,
                s.CreatedAt, s.CompletedAt, s.RowVersion));

    [LinqQuery(nameof(ExistsQuery))]
    private partial Task<bool> Exists(Guid id, CancellationToken ct);

    private static IQuery<bool> ExistsQuery(Guid id) =>
        Sql.Table<BrainstormSession>().Where(s => s.Id == id).Any();

    [LinqCommand(nameof(UpdateQuery), ThrowOnNoRows = true)]
    private partial Task Update(
        Guid id, uint rowVersion, BrainstormPhase phase, bool phaseCompleteSignaled, DateTime? completedAt,
        string? designDocument, string? implementationPlan, IAsyncDbTransaction tx, CancellationToken ct);

    private static int UpdateQuery(
        Guid id, uint rowVersion, BrainstormPhase phase, bool phaseCompleteSignaled, DateTime? completedAt,
        string? designDocument, string? implementationPlan) =>
        Sql.Table<BrainstormSession>()
            .Where(s => s.Id == id && s.RowVersion == rowVersion)
            .ExecuteUpdate(set => set
                .SetProperty(s => s.Phase, phase)
                .SetProperty(s => s.PhaseCompleteSignaled, phaseCompleteSignaled)
                .SetProperty(s => s.CompletedAt, completedAt)
                .SetProperty(s => s.DesignDocument, designDocument)
                .SetProperty(s => s.ImplementationPlan, implementationPlan));
}
