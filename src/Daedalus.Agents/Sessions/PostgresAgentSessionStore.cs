using System.Text.Json;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Thalos;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Sessions;

/// <summary>
///     Thalos session store over <see cref="ApplicationDbContext"/>. Uses a fresh short-lived DbContext per call
///     (the store is a singleton inside Thalos; DbContexts are not) and reads time from the injected
///     <see cref="TimeProvider"/> so tests can drive the clock.
/// </summary>
/// <remarks>
///     Concurrency: <see cref="RecordTurnAsync"/> and <see cref="TryTransitionAsync"/> are single atomic
///     <c>UPDATE</c>s (no read-modify-write, so parallel calls never lose counters and a claim succeeds exactly once);
///     <see cref="AppendMessagesAsync"/> takes a <c>FOR UPDATE</c> row lock on the session inside a transaction so
///     concurrent appends get strictly increasing sequence numbers.
/// </remarks>
public sealed class PostgresAgentSessionStore(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock)
    : IAgentSessionStore
{
    // jsonb normalises key order on write, so the "$type" discriminator of AIContent is not guaranteed to come first
    // when read back — allow out-of-order metadata on the M.E.AI defaults (same converters/resolver otherwise).
    private static readonly JsonSerializerOptions Json = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(AIJsonUtilities.DefaultOptions) { AllowOutOfOrderMetadataProperties = true };
        options.MakeReadOnly();
        return options;
    }

    /// <inheritdoc />
    public async ValueTask<Result<AgentSessionRecord, AgentError>> CreateAsync(AgentId agentId, string ownerId, CancellationToken ct)
    {
        var id = SessionId.New();
        var created = AgentSession.Create(id.Value, agentId.Value, ownerId, UtcNow());
        if (created.IsFailure)
        {
            return Result<AgentSessionRecord, AgentError>.Failure(AgentError.Validation(created.Error));
        }

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.AgentSessions.Add(created.Value);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<AgentSessionRecord, AgentError>.Success(ToRecord(created.Value));
    }

    /// <inheritdoc />
    public async ValueTask<Result<AgentSessionRecord, AgentError>> GetAsync(SessionId id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await db.AgentSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id.Value, ct)
            .ConfigureAwait(false);

        return session is null
            ? Result<AgentSessionRecord, AgentError>.Failure(AgentError.SessionNotFound(id))
            : Result<AgentSessionRecord, AgentError>.Success(ToRecord(session));
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<AgentSessionRecord>, AgentError>> ListAsync(string ownerId, int skip, int take, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.AgentSessions.AsNoTracking()
            .Where(s => s.OwnerId == ownerId)
            .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id)
            .Skip(skip).Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IReadOnlyList<AgentSessionRecord> page = rows.ConvertAll(ToRecord);
        return Result<IReadOnlyList<AgentSessionRecord>, AgentError>.Success(page);
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<ChatMessage>, AgentError>> LoadMessagesAsync(SessionId id, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        if (!await db.AgentSessions.AnyAsync(s => s.Id == id.Value, ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<ChatMessage>, AgentError>.Failure(AgentError.SessionNotFound(id));
        }

        var rows = await db.AgentMessages.AsNoTracking()
            .Where(m => m.SessionId == id.Value)
            .OrderBy(m => m.Sequence)
            .Select(m => m.ContentJson)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var messages = new List<ChatMessage>(rows.Count);
        foreach (var json in rows)
        {
            var message = JsonSerializer.Deserialize<ChatMessage>(json, Json);
            if (message is not null)
            {
                messages.Add(message);
            }
        }

        return Result<IReadOnlyList<ChatMessage>, AgentError>.Success(messages);
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> AppendMessagesAsync(SessionId id, IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);

        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Row lock on the session header serialises concurrent appends so sequence numbers never collide.
        var locked = await db.AgentSessions
            .FromSql($"""SELECT * FROM "AgentSessions" WHERE "Id" = {id.Value} FOR UPDATE""")
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);
        if (locked.Count == 0)
        {
            return UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id));
        }

        var next = await db.AgentMessages
            .Where(m => m.SessionId == id.Value)
            .MaxAsync(m => (int?)m.Sequence, ct)
            .ConfigureAwait(false) ?? -1;

        var now = UtcNow();
        foreach (var message in messages)
        {
            next++;
            var usage = message.Contents.OfType<UsageContent>().FirstOrDefault()?.Details;
            var entity = AgentMessage.Create(
                id.Value,
                next,
                message.Role.Value,
                JsonSerializer.Serialize(message, Json),
                (int?)usage?.InputTokenCount,
                (int?)usage?.OutputTokenCount,
                modelId: null,
                now);
            if (entity.IsFailure)
            {
                return UnitResult<AgentError>.Failure(AgentError.StoreError(entity.Error));
            }

            db.AgentMessages.Add(entity.Value);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return UnitResult<AgentError>.Success();
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> RecordTurnAsync(SessionId id, TurnUsage usage, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = UtcNow();

        // Single atomic UPDATE with in-database increments: concurrent turn records never lose counters
        // (a read-modify-write would, because the bytea RowVersion is not DB-generated on PostgreSQL).
        var affected = await db.AgentSessions
            .Where(s => s.Id == id.Value)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.TurnCount, s => s.TurnCount + 1)
                .SetProperty(s => s.TotalInputTokens, s => s.TotalInputTokens + usage.InputTokens)
                .SetProperty(s => s.TotalOutputTokens, s => s.TotalOutputTokens + usage.OutputTokens)
                .SetProperty(s => s.LastActivityAt, now), ct)
            .ConfigureAwait(false);

        return affected == 1
            ? UnitResult<AgentError>.Success()
            : UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id));
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> UpdateStateAsync(SessionId id, SessionState state, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var session = await db.AgentSessions.FirstOrDefaultAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
        if (session is null)
        {
            return UnitResult<AgentError>.Failure(AgentError.SessionNotFound(id));
        }

        session.SetState(ToDomain(state), UtcNow());
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            return UnitResult<AgentError>.Failure(AgentError.SessionBusy(id));
        }

        return UnitResult<AgentError>.Success();
    }

    /// <inheritdoc />
    /// <remarks>Atomic compare-and-swap used by the runtime to claim a turn (see <see cref="IAgentSessionStore"/> remarks).</remarks>
    public async ValueTask<Result<bool, AgentError>> TryTransitionAsync(SessionId id, SessionState from, SessionState target, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = UtcNow();
        var fromState = ToDomain(from);
        var targetState = ToDomain(target);

        // Single UPDATE … WHERE State = @from — atomic without an explicit transaction or row lock.
        var affected = await db.AgentSessions
            .Where(s => s.Id == id.Value && s.State == fromState)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.State, targetState)
                .SetProperty(s => s.LastActivityAt, now), ct)
            .ConfigureAwait(false);

        if (affected == 1)
        {
            return Result<bool, AgentError>.Success(true);
        }

        var exists = await db.AgentSessions.AsNoTracking().AnyAsync(s => s.Id == id.Value, ct).ConfigureAwait(false);
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
