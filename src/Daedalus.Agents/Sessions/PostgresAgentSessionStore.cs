using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.AgentSessions;
using Microsoft.Extensions.AI;
using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Sessions;

/// <summary>
///     Thalos session store over <see cref="AgentSessionSql"/> (tables <c>AgentSessions</c> and <c>AgentMessages</c>).
///     Opens one pooled connection per call (the store is a singleton inside Thalos) and reads time from the injected
///     <see cref="TimeProvider"/> so tests can drive the clock.
/// </summary>
/// <remarks>
///     Concurrency: every session write is independent, with no version check. <see cref="RecordTurnAsync"/> and
///     <see cref="TryTransitionAsync"/> are single atomic <c>UPDATE</c>s (no read-modify-write, so parallel calls never
///     lose counters and a claim succeeds exactly once); <see cref="UpdateStateAsync"/> writes only the state and the
///     activity time, so it never undoes a turn's counters, and a session gone from under it is
///     <c>SessionNotFound</c>; <see cref="AppendMessagesAsync"/> takes a <c>FOR UPDATE</c> row lock on the session
///     inside a transaction so concurrent appends get strictly increasing sequence numbers.
/// </remarks>
public sealed class PostgresAgentSessionStore(DaedalusDb db, TimeProvider clock) : IAgentSessionStore
{
    /// <inheritdoc />
    public async ValueTask<Result<AgentSessionRecord, AgentError>> CreateAsync(AgentId agentId, string ownerId, CancellationToken ct)
    {
        var id = SessionId.New();
        var created = AgentSession.Create(id.Value, agentId.Value, ownerId, UtcNow());
        if (created.IsFailure)
        {
            return Result<AgentSessionRecord, AgentError>.Failure(AgentError.Validation(created.Error));
        }

        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        await new AgentSessionSql(connection).InsertAsync(created.Value, ct).ConfigureAwait(false);
        return Result<AgentSessionRecord, AgentError>.Success(ToRecord(created.Value));
    }

    /// <inheritdoc />
    public async ValueTask<Result<AgentSessionRecord, AgentError>> GetAsync(SessionId id, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var session = await new AgentSessionSql(connection).FindAsync(id.Value, ct).ConfigureAwait(false);

        return session is null
            ? Result<AgentSessionRecord, AgentError>.Failure(AgentError.SessionNotFound(id))
            : Result<AgentSessionRecord, AgentError>.Success(ToRecord(session));
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<AgentSessionRecord>, AgentError>> ListAsync(string ownerId, int skip, int take, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var sessions = await new AgentSessionSql(connection).ListByOwnerAsync(ownerId, skip, take, ct).ConfigureAwait(false);

        IReadOnlyList<AgentSessionRecord> page = [.. sessions.Select(ToRecord)];
        return Result<IReadOnlyList<AgentSessionRecord>, AgentError>.Success(page);
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<ChatMessage>, AgentError>> LoadMessagesAsync(SessionId id, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var sql = new AgentSessionSql(connection);
        if (!await sql.ExistsAsync(id.Value, ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<ChatMessage>, AgentError>.Failure(AgentError.SessionNotFound(id));
        }

        var rows = await sql.LoadMessageContentsAsync(id.Value, ct).ConfigureAwait(false);

        var messages = new List<ChatMessage>(rows.Count);
        foreach (var json in rows)
        {
            var message = AgentSessionJson.Deserialize(json);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return Result<IReadOnlyList<ChatMessage>, AgentError>.Success(messages);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     The messages are built and validated first, each numbered by its offset in the batch; the append then locks the
    ///     session, numbers them after the last stored message and inserts them in one transaction.
    /// </remarks>
    public async ValueTask<UnitResult<AgentError>> AppendMessagesAsync(SessionId id, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var now = UtcNow();
        var batch = new List<AgentMessage>(messages.Count);
        for (var offset = 0; offset < messages.Count; offset++)
        {
            var message = messages[offset];
            UsageDetails? usage = null;
            foreach (var content in message.Contents)
            {
                if (content is UsageContent usageContent)
                {
                    usage = usageContent.Details;
                    break;
                }
            }

            var entity = AgentMessage.Create(
                id.Value,
                offset,
                message.Role.Value,
                AgentSessionJson.Serialize(message),
                (int?)usage?.InputTokenCount,
                (int?)usage?.OutputTokenCount,
                modelId: null,
                now);
            if (entity.IsFailure)
            {
                return UnitResult<AgentError>.Failure(AgentError.StoreError(entity.Error));
            }

            batch.Add(entity.Value);
        }

        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var appended = await new AgentSessionSql(connection).AppendAsync(id.Value, batch, ct).ConfigureAwait(false);
        return appended
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id));
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> RecordTurnAsync(SessionId id, TurnUsage usage, CancellationToken ct)
    {
        var now = UtcNow();
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);

        // Single atomic UPDATE with in-database increments: concurrent turn records never lose counters.
        var affected = await new AgentSessionSql(connection)
            .RecordTurnAsync(id.Value, usage.InputTokens, usage.OutputTokens, now, ct)
            .ConfigureAwait(false);

        return affected == 1
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     One <c>UPDATE</c> of the state and the activity time, with no version check: the caller gives the state, so
    ///     there is nothing a concurrent write could make stale. Only a missing row gives it no row, which is
    ///     <c>SessionNotFound</c>.
    /// </remarks>
    public async ValueTask<UnitResult<AgentError>> UpdateStateAsync(SessionId id, SessionState state, CancellationToken ct)
    {
        var now = UtcNow();
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var affected = await new AgentSessionSql(connection)
            .SetStateAsync(id.Value, ToDomain(state), now, ct)
            .ConfigureAwait(false);

        return affected == 1
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id));
    }

    /// <inheritdoc />
    /// <remarks>Atomic compare-and-swap used by the runtime to claim a turn (see <see cref="IAgentSessionStore"/> remarks).</remarks>
    public async ValueTask<Result<bool, AgentError>> TryTransitionAsync(SessionId id, SessionState from, SessionState target, CancellationToken ct)
    {
        var now = UtcNow();
        await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
        var sql = new AgentSessionSql(connection);

        // Single UPDATE … WHERE State = @from — atomic without an explicit transaction or row lock.
        var affected = await sql.TransitionAsync(id.Value, ToDomain(from), ToDomain(target), now, ct).ConfigureAwait(false);
        if (affected == 1)
        {
            return Result<bool, AgentError>.Success(true);
        }

        var exists = await sql.ExistsAsync(id.Value, ct).ConfigureAwait(false);
        return exists
            ? Result<bool, AgentError>.Success(false)
            : Result<bool, AgentError>.Failure(AgentError.SessionNotFound(id));
    }

    private DateTime UtcNow() => clock.GetUtcNow().UtcDateTime;

    private static AgentSessionState ToDomain(SessionState state) => (AgentSessionState)(int)state;

    private static SessionState ToThalos(AgentSessionState state) => (SessionState)(int)state;

    private static AgentSessionRecord ToRecord(AgentSession s) => new(
        new SessionId(s.Id),
        new AgentId(s.AgentId),
        s.OwnerId,
        ToThalos(s.State),
        new DateTimeOffset(s.CreatedAt, TimeSpan.Zero),
        new DateTimeOffset(s.LastActivityAt, TimeSpan.Zero),
        s.TurnCount,
        s.TotalInputTokens,
        s.TotalOutputTokens);
}
