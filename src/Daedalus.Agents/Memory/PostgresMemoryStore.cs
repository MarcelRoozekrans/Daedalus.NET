using System.Runtime.CompilerServices;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Memory;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Memory;

/// <summary>
///     Thalos memory store over <see cref="ApplicationDbContext"/> (table <c>AgentMemories</c>). Records only — vectors live
///     in the Rag.NET index. Fresh short-lived DbContext per call (the store is a singleton), time from the injected
///     <see cref="TimeProvider"/>, atomic <c>UPDATE</c>s for counters. Same patterns as <c>PostgresAgentSessionStore</c>.
/// </summary>
/// <remarks>
///     <see cref="StreamAsync"/> uses row-value keyset paging by <c>(CreatedAt, Id)</c> in batches of
///     <see cref="DefaultStreamBatchSize"/> so no connection is held while the consumer (reindex) embeds and updates the
///     yielded records, and rows that drop out of the filter mid-stream (e.g. <c>IndexPending</c> cleared) are neither
///     skipped nor repeated — as the <see cref="IMemoryStore"/> contract requires. Validation failures and duplicate ids
///     come back as <see cref="AgentError"/>s; Npgsql/connection exceptions propagate (same policy as the session store —
///     <c>IMemoryService</c> maps a throwing stream to <c>MemoryStoreFailed</c>).
/// </remarks>
public sealed class PostgresMemoryStore : IMemoryStore
{
    /// <summary>Rows fetched per keyset page in <see cref="StreamAsync"/>.</summary>
    internal const int DefaultStreamBatchSize = 256;

    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory;
    private readonly TimeProvider _clock;
    private readonly int _streamBatchSize;

    /// <summary>Creates the store; DbContexts come from <paramref name="contextFactory"/>, time from <paramref name="clock"/>.</summary>
    public PostgresMemoryStore(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock)
        : this(contextFactory, clock, DefaultStreamBatchSize)
    {
    }

    /// <summary>Test seam: a small <paramref name="streamBatchSize"/> exercises the keyset paging of <see cref="StreamAsync"/>.</summary>
    internal PostgresMemoryStore(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock, int streamBatchSize)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(streamBatchSize, 1);
        _contextFactory = contextFactory;
        _clock = clock;
        _streamBatchSize = streamBatchSize;
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecord, AgentError>> CreateAsync(MemoryRecord record, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(record);

        // Inserted as given (timestamps and bookkeeping included); the aggregate validates and normalises tags/kind.
        var created = AgentMemory.Create(
            record.Id.Value, record.OwnerId, record.AgentId?.Value, record.Kind.Value, record.Text, record.Tags, record.Source,
            record.Importance, record.CreatedAt.UtcDateTime, record.IndexPending,
            record.UpdatedAt.UtcDateTime, record.IsArchived, record.RecallCount, record.LastRecalledAt?.UtcDateTime);
        if (created.IsFailure)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryValidationFailed(created.Error));
        }

        try
        {
            await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.AgentMemories.Add(created.Value);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<MemoryRecord, AgentError>.Success(ToRecord(created.Value));
        }
        catch (DbUpdateException ex)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryStoreFailed("Could not store the memory.", ex.GetType().Name));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecord, AgentError>> GetAsync(MemoryId id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.AgentMemories.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id.Value, ct)
            .ConfigureAwait(false);

        return row is null
            ? Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryNotFound(id))
            : Result<MemoryRecord, AgentError>.Success(ToRecord(row));
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecord, AgentError>> UpdateAsync(MemoryId id, MemoryUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.AgentMemories.FirstOrDefaultAsync(m => m.Id == id.Value, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryNotFound(id));
        }

        // The aggregate bumps UpdatedAt only for content changes (same rule as MemoryUpdate.TouchesContent).
        var applied = row.Update(update.Text, update.Tags, update.Importance, update.IsArchived, update.IndexPending, UtcNow());
        if (applied.IsFailure)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryValidationFailed(applied.Error));
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Hard-deleted between the read and the save: to the caller the memory no longer exists.
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryNotFound(id));
        }
        catch (DbUpdateException ex)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryStoreFailed("Could not update the memory.", ex.GetType().Name));
        }

        return Result<MemoryRecord, AgentError>.Success(ToRecord(row));
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> DeleteAsync(MemoryId id, CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var affected = await db.AgentMemories
            .Where(m => m.Id == id.Value)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        return affected == 1 ? UnitResult<AgentError>.Success() : UnitResult<AgentError>.Failure(AgentError.MemoryNotFound(id));
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MemoryQuery.MaxPageSize);

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var filtered = Apply(db.AgentMemories.AsNoTracking(), query);
        var total = await filtered.CountAsync(ct).ConfigureAwait(false);

        // Skip in long: Page may be int.MaxValue; a page beyond the matches is simply empty.
        var skip = (long)(page - 1) * size;
        IReadOnlyList<MemoryRecord> items = [];
        if (skip < total)
        {
            var rows = await filtered
                .OrderByDescending(m => m.UpdatedAt).ThenByDescending(m => m.Id)
                .Skip((int)skip).Take(size)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            items = rows.ConvertAll(ToRecord);
        }

        return Result<MemoryPage, AgentError>.Success(new MemoryPage(items, page, size, total));
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> MarkRecalledAsync(IReadOnlyList<MemoryId> ids, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return UnitResult<AgentError>.Success();
        }

        // ids are a set (duplicates count once); "= ANY(@guids)" touches each row once anyway, distinct keeps the parameter small.
        var guids = ids.Select(i => i.Value).Distinct().ToList();
        var when = at.UtcDateTime;
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.AgentMemories
            .Where(m => guids.Contains(m.Id))
            .ExecuteUpdateAsync(set => set
                .SetProperty(m => m.RecallCount, m => m.RecallCount + 1)
                .SetProperty(m => m.LastRecalledAt, when), ct)
            .ConfigureAwait(false);

        return UnitResult<AgentError>.Success();
    }

    /// <inheritdoc />
    public IAsyncEnumerable<MemoryRecord> StreamAsync(MemoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        return StreamCoreAsync(query, ct);
    }

    private async IAsyncEnumerable<MemoryRecord> StreamCoreAsync(MemoryQuery query, [EnumeratorCancellation] CancellationToken ct)
    {
        // Row-value keyset paging: ORDER BY ("CreatedAt", "Id") and WHERE ("CreatedAt", "Id") > (@lastCreatedAt, @lastId) use the
        // same total order in SQL (uuid byte order), so ties on CreatedAt need no client-side bookkeeping.
        (DateTime CreatedAt, Guid Id)? last = null;
        while (true)
        {
            List<AgentMemory> batch;
            await using (var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
            {
                var q = Apply(db.AgentMemories.AsNoTracking(), query);
                if (last is { } after)
                {
                    q = q.Where(m => EF.Functions.GreaterThan(ValueTuple.Create(m.CreatedAt, m.Id), ValueTuple.Create(after.CreatedAt, after.Id)));
                }

                batch = await q
                    .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
                    .Take(_streamBatchSize)
                    .ToListAsync(ct)
                    .ConfigureAwait(false);
            }

            foreach (var row in batch)
            {
                yield return ToRecord(row);
            }

            if (batch.Count < _streamBatchSize)
            {
                yield break;
            }

            last = (batch[^1].CreatedAt, batch[^1].Id);
        }
    }

    /// <summary>The <see cref="MemoryQuery.Matches"/> semantics in SQL: null/empty filter lists mean "no filter".</summary>
    private static IQueryable<AgentMemory> Apply(IQueryable<AgentMemory> source, MemoryQuery query)
    {
        var q = source;

        if (query.OwnerIds is { Count: > 0 })
        {
            var owners = query.OwnerIds.ToList();
            q = q.Where(m => owners.Contains(m.OwnerId));
        }

        if (query.AgentId is { } agent)
        {
            var agentGuid = agent.Value;
            q = q.Where(m => m.AgentId == agentGuid); // pinned to this agent only (owner-wide rows need AgentId = null)
        }

        if (query.Kinds is { Count: > 0 })
        {
            var kinds = query.Kinds.Select(k => k.Value).ToList();
            q = q.Where(m => kinds.Contains(m.Kind));
        }

        if (query.Tags is { Count: > 0 })
        {
            // Every listed tag must be present (AND), normalised like stored tags. A blank query tag matches nothing.
            foreach (var raw in query.Tags)
            {
                var tag = NormaliseTag(raw);
                if (string.IsNullOrEmpty(tag))
                {
                    return q.Where(m => false);
                }

                q = q.Where(m => EF.Property<List<string>>(m, "_tags").Contains(tag)); // Npgsql: @tag = ANY("Tags")
            }
        }

        if (!query.IncludeArchived)
        {
            q = q.Where(m => !m.IsArchived);
        }

        if (query.IndexPending is { } pending)
        {
            q = q.Where(m => m.IndexPending == pending);
        }

        return q;
    }

#pragma warning disable CA1308 // tags are lowercase identifiers by definition, not user-facing text
    private static string? NormaliseTag(string? tag) => tag?.Trim().ToLowerInvariant();
#pragma warning restore CA1308

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    private static MemoryRecord ToRecord(AgentMemory m) => new()
    {
        Id = new MemoryId(m.Id),
        OwnerId = m.OwnerId,
        AgentId = m.AgentId is { } a ? new AgentId(a) : null,
        Kind = new MemoryKind(m.Kind),
        Text = m.Text,
        Tags = m.Tags.ToList(),
        Source = m.Source,
        Importance = m.Importance,
        CreatedAt = new DateTimeOffset(m.CreatedAt, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(m.UpdatedAt, TimeSpan.Zero),
        LastRecalledAt = m.LastRecalledAt is { } r ? new DateTimeOffset(r, TimeSpan.Zero) : null,
        RecallCount = m.RecallCount,
        IsArchived = m.IsArchived,
        IndexPending = m.IndexPending,
    };
}
