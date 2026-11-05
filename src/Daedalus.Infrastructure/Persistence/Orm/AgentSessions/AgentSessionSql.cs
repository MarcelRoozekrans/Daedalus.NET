using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using AgentMessage = Daedalus.Domain.Entities.AgentMessage;
using AgentSession = Daedalus.Domain.Entities.AgentSession;
using AgentSessionState = Daedalus.Domain.Entities.AgentSessionState;

namespace Daedalus.Infrastructure.Persistence.Orm.AgentSessions;

/// <summary>
///     The SQL of the <c>"AgentSessions"</c> and <c>"AgentMessages"</c> tables, over one open connection. Its public
///     methods take and return <see cref="AgentSession"/> and <see cref="AgentMessage"/>; the generated queries, their
///     companions and the row records stay inside.
/// </summary>
/// <remarks>
///     A session row has no version column that works and none is checked: every write is independent and touches
///     only the columns it means to change, so a turn's counters, a state save and a claim never undo one another.
///     Counters are computed in the statement from the stored value; a claim is a compare-and-swap on the state. An
///     append locks the session row <c>FOR UPDATE</c> in its transaction, so concurrent appends number their messages
///     one after the other. The inserts are raw SQL, as the ORM has no LINQ insert (ruling R10), and list every column.
/// </remarks>
public sealed partial class AgentSessionSql(IAsyncDbConnection connection)
{
    private const string InsertSessionSql = """
        INSERT INTO "AgentSessions"
            ("Id", "AgentId", "OwnerId", "State", "CreatedAt", "LastActivityAt", "TurnCount", "TotalInputTokens",
             "TotalOutputTokens")
        VALUES (@id, @agentId, @ownerId, @state, @createdAt, @lastActivityAt, @turnCount, @totalInputTokens,
                @totalOutputTokens)
        """;

    private const string InsertMessagesSql = """
        INSERT INTO "AgentMessages"
            ("Id", "SessionId", "Sequence", "Role", "ContentJson", "InputTokens", "OutputTokens", "ModelId", "CreatedAt")
        VALUES (@Id, @SessionId, @Sequence, @Role, @ContentJson, @InputTokens, @OutputTokens, @ModelId, @CreatedAt)
        """;

    /// <summary>Inserts <paramref name="session"/> with every column as given.</summary>
    /// <param name="session">The session to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public async Task InsertAsync(AgentSession session, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(session);

        await InsertSession(
            session.Id, session.AgentId, session.OwnerId, (int)session.State, session.CreatedAt,
            session.LastActivityAt, session.TurnCount, session.TotalInputTokens, session.TotalOutputTokens, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Reads the session with <paramref name="id"/>.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The session as stored, or null when there is none.</returns>
    public async Task<AgentSession?> FindAsync(Guid id, CancellationToken ct)
    {
        var row = await FindRow(id, ct).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <summary>Whether a session with <paramref name="id"/> is stored.</summary>
    /// <param name="id">The session id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>True when the row exists.</returns>
    public Task<bool> ExistsAsync(Guid id, CancellationToken ct) => Exists(id, ct);

    /// <summary>Reads one page of the sessions <paramref name="ownerId"/> owns, newest first.</summary>
    /// <param name="ownerId">The owner subject, compared ordinally.</param>
    /// <param name="skip">How many sessions to skip.</param>
    /// <param name="take">How many sessions to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The sessions as stored, ordered by <c>CreatedAt</c> then id, both descending.</returns>
    public async Task<IReadOnlyList<AgentSession>> ListByOwnerAsync(string ownerId, int skip, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownerId);
        var rows = await ListByOwner(ownerId, skip, take, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain());
    }

    /// <summary>Reads the stored JSON of every message of a session, in append order.</summary>
    /// <param name="sessionId">The session id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The <c>ContentJson</c> of each message, ordered by sequence; empty for a session without messages.</returns>
    public async Task<IReadOnlyList<string>> LoadMessageContentsAsync(Guid sessionId, CancellationToken ct) =>
        await MessageContents(sessionId, ct).ConfigureAwait(false);

    /// <summary>
    ///     Appends <paramref name="batch"/> to the session in one transaction: locks the session row
    ///     <c>FOR UPDATE</c>, reads the highest stored sequence, and inserts every message numbered after it. Each
    ///     message's <see cref="AgentMessage.Sequence"/> is its offset in the batch, 0 for the first; the stored sequence
    ///     is the session's next free number plus that offset. A concurrent append waits on the lock, so the two batches
    ///     never share a number. Nothing is written when the session does not exist or any insert fails.
    /// </summary>
    /// <param name="sessionId">The session to append to.</param>
    /// <param name="batch">The messages, each numbered by its offset in the batch.</param>
    /// <param name="ct">Cancels the append.</param>
    /// <returns>False when the session does not exist, true when the batch is committed.</returns>
    public async Task<bool> AppendAsync(Guid sessionId, IReadOnlyList<AgentMessage> batch, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var locked = await Lock(sessionId, tx, ct).ConfigureAwait(false);
        if (locked.Count == 0)
        {
            return false;
        }

        if (batch.Count > 0)
        {
            var first = (await LastSequence(sessionId, tx, ct).ConfigureAwait(false) ?? -1) + 1;
            var rows = new AgentMessageInsertRow[batch.Count];
            for (var i = 0; i < batch.Count; i++)
            {
                var m = batch[i];
                rows[i] = new AgentMessageInsertRow(
                    m.Id, sessionId, first + m.Sequence, m.Role, m.ContentJson, m.InputTokens, m.OutputTokens,
                    m.ModelId, m.CreatedAt);
            }

            await InsertMessages(rows, tx, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    ///     Counts one turn on the session: one more <c>TurnCount</c> and the tokens added to the totals, computed in the
    ///     statement from the stored values, and <c>LastActivityAt</c> set to <paramref name="at"/>.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="inputTokens">The turn's input tokens.</param>
    /// <param name="outputTokens">The turn's output tokens.</param>
    /// <param name="at">When the turn was recorded.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>The number of rows updated, 1 or 0 when the session does not exist.</returns>
    public Task<int> RecordTurnAsync(Guid id, long inputTokens, long outputTokens, DateTime at, CancellationToken ct) =>
        RecordTurn(id, inputTokens, outputTokens, at, ct);

    /// <summary>
    ///     Sets the session's state and <c>LastActivityAt</c>, and nothing else, whatever the stored state is.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="state">The new state.</param>
    /// <param name="at">When the state changed.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>The number of rows updated, 1 or 0 when the session does not exist.</returns>
    public Task<int> SetStateAsync(Guid id, AgentSessionState state, DateTime at, CancellationToken ct) =>
        SetState(id, state, at, ct);

    /// <summary>
    ///     Compare-and-swap: sets the state to <paramref name="target"/> and <c>LastActivityAt</c> only when the stored
    ///     state is <paramref name="from"/>. Two transitions from the same state on one session update one row between
    ///     them, since the second one's condition is checked again against the first one's committed row.
    /// </summary>
    /// <param name="id">The session id.</param>
    /// <param name="from">The state the session must be in.</param>
    /// <param name="target">The new state.</param>
    /// <param name="at">When the state changed.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>The number of rows updated: 1 when applied, 0 when the state differed or the session does not exist.</returns>
    public Task<int> TransitionAsync(
        Guid id, AgentSessionState from, AgentSessionState target, DateTime at, CancellationToken ct) =>
        Transition(id, from, target, at, ct);

    /// <summary>Sets every <c>Running</c> session to <c>Idle</c>, with <c>LastActivityAt</c> set to <paramref name="at"/>.</summary>
    /// <param name="at">When the sessions were reset.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>The number of sessions reset.</returns>
    public Task<int> ResetRunningAsync(DateTime at, CancellationToken ct) => ResetRunning(at, ct);

    [Command(InsertSessionSql)]
    private partial Task<int> InsertSession(
        Guid id, Guid agentId, string ownerId, int state, DateTime createdAt, DateTime lastActivityAt, int turnCount,
        long totalInputTokens, long totalOutputTokens, CancellationToken ct);

    [Command(InsertMessagesSql, Kind = CommandKind.BulkInsert)]
    private partial Task<int> InsertMessages(
        IReadOnlyList<AgentMessageInsertRow> rows, IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<AgentSessionRow?> FindRow(Guid id, CancellationToken ct);

    private static IQuery<AgentSessionRow> FindRowQuery(Guid id) =>
        Sql.Table<AgentSession>()
            .Where(s => s.Id == id)
            .Select(s => new AgentSessionRow(
                s.Id, s.AgentId, s.OwnerId, s.State, s.CreatedAt, s.LastActivityAt, s.TurnCount, s.TotalInputTokens,
                s.TotalOutputTokens));

    [LinqQuery(nameof(ExistsQuery))]
    private partial Task<bool> Exists(Guid id, CancellationToken ct);

    private static IQuery<bool> ExistsQuery(Guid id) =>
        Sql.Table<AgentSession>().Where(s => s.Id == id).Any();

    [LinqQuery(nameof(ListByOwnerQuery))]
    private partial Task<List<AgentSessionRow>> ListByOwner(string ownerId, int skip, int take, CancellationToken ct);

    private static IQuery<AgentSessionRow> ListByOwnerQuery(string ownerId, int skip, int take) =>
        Sql.Table<AgentSession>()
            .Where(s => string.Equals(s.OwnerId, ownerId, StringComparison.Ordinal))
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Skip(skip)
            .Take(take)
            .Select(s => new AgentSessionRow(
                s.Id, s.AgentId, s.OwnerId, s.State, s.CreatedAt, s.LastActivityAt, s.TurnCount, s.TotalInputTokens,
                s.TotalOutputTokens));

    [LinqQuery(nameof(MessageContentsQuery))]
    private partial Task<List<string>> MessageContents(Guid sessionId, CancellationToken ct);

    private static IQuery<string> MessageContentsQuery(Guid sessionId) =>
        Sql.Table<AgentMessage>()
            .Where(m => m.SessionId == sessionId)
            .OrderBy(m => m.Sequence)
            .Select(m => m.ContentJson);

    [LinqQuery(nameof(LockQuery))]
    private partial Task<List<Guid>> Lock(Guid id, IAsyncDbTransaction tx, CancellationToken ct);

    private static IQuery<Guid> LockQuery(Guid id) =>
        Sql.Table<AgentSession>().Where(s => s.Id == id).ForUpdate().Select(s => s.Id);

    [LinqQuery(nameof(LastSequenceQuery))]
    private partial Task<int?> LastSequence(Guid sessionId, IAsyncDbTransaction tx, CancellationToken ct);

    private static IQuery<int?> LastSequenceQuery(Guid sessionId) =>
        Sql.Table<AgentMessage>().Where(m => m.SessionId == sessionId).Max(m => (int?)m.Sequence);

    [LinqCommand(nameof(RecordTurnQuery))]
    private partial Task<int> RecordTurn(
        Guid id, long inputTokens, long outputTokens, DateTime at, CancellationToken ct);

    private static int RecordTurnQuery(Guid id, long inputTokens, long outputTokens, DateTime at) =>
        Sql.Table<AgentSession>()
            .Where(s => s.Id == id)
            .ExecuteUpdate(set => set
                .SetProperty(s => s.TurnCount, s => s.TurnCount + 1)
                .SetProperty(s => s.TotalInputTokens, s => s.TotalInputTokens + inputTokens)
                .SetProperty(s => s.TotalOutputTokens, s => s.TotalOutputTokens + outputTokens)
                .SetProperty(s => s.LastActivityAt, at));

    [LinqCommand(nameof(SetStateQuery))]
    private partial Task<int> SetState(Guid id, AgentSessionState state, DateTime at, CancellationToken ct);

    private static int SetStateQuery(Guid id, AgentSessionState state, DateTime at) =>
        Sql.Table<AgentSession>()
            .Where(s => s.Id == id)
            .ExecuteUpdate(set => set
                .SetProperty(s => s.State, state)
                .SetProperty(s => s.LastActivityAt, at));

    [LinqCommand(nameof(TransitionQuery))]
    private partial Task<int> Transition(
        Guid id, AgentSessionState from, AgentSessionState target, DateTime at, CancellationToken ct);

    private static int TransitionQuery(Guid id, AgentSessionState from, AgentSessionState target, DateTime at) =>
        Sql.Table<AgentSession>()
            .Where(s => s.Id == id && s.State == from)
            .ExecuteUpdate(set => set
                .SetProperty(s => s.State, target)
                .SetProperty(s => s.LastActivityAt, at));

    [LinqCommand(nameof(ResetRunningQuery))]
    private partial Task<int> ResetRunning(DateTime at, CancellationToken ct);

    private static int ResetRunningQuery(DateTime at) =>
        Sql.Table<AgentSession>()
            .Where(s => s.State == AgentSessionState.Running)
            .ExecuteUpdate(set => set
                .SetProperty(s => s.State, AgentSessionState.Idle)
                .SetProperty(s => s.LastActivityAt, at));
}
