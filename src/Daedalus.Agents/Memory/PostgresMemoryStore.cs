using System.Data.Common;
using System.Runtime.CompilerServices;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.AgentMemories;
using Thalos;
using Thalos.Memory;
using ZeroAlloc.ORM;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Memory;

/// <summary>
///     Thalos memory store over <see cref="AgentMemorySql"/> (table <c>AgentMemories</c>). Records only — vectors live
///     in the Rag.NET index. One pooled connection per call (the store is a singleton), time from the injected
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

    private readonly DaedalusDb _db;
    private readonly TimeProvider _clock;
    private readonly int _streamBatchSize;

    /// <summary>Creates the store; connections come from <paramref name="db"/>, time from <paramref name="clock"/>.</summary>
    public PostgresMemoryStore(DaedalusDb db, TimeProvider clock)
        : this(db, clock, DefaultStreamBatchSize)
    {
    }

    /// <summary>Test seam: a smaller keyset page makes the paging seam reachable with a handful of rows.</summary>
    internal PostgresMemoryStore(DaedalusDb db, TimeProvider clock, int streamBatchSize)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThan(streamBatchSize, 1);
        _db = db;
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
            await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
            await new AgentMemorySql(connection).InsertAsync(created.Value, ct).ConfigureAwait(false);
            return Result<MemoryRecord, AgentError>.Success(ToRecord(created.Value));
        }
        catch (DbException ex)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryStoreFailed("Could not store the memory.", ex.GetType().Name));
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecord, AgentError>> GetAsync(MemoryId id, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var row = await new AgentMemorySql(connection).FindAsync(id.Value, ct).ConfigureAwait(false);

        return row is null
            ? Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryNotFound(id))
            : Result<MemoryRecord, AgentError>.Success(ToRecord(row));
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryRecord, AgentError>> UpdateAsync(MemoryId id, MemoryUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var sql = new AgentMemorySql(connection);
        var row = await sql.FindAsync(id.Value, ct).ConfigureAwait(false);
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
            // Only the supplied fields are written, as EF's change tracking wrote only the modified columns: a recall
            // counted between the read above and this write must stand.
            await sql.WriteUpdateAsync(
                row, update.Text is not null, update.Tags is not null, update.Importance is not null,
                update.IsArchived is not null, update.IndexPending is not null, ct).ConfigureAwait(false);
        }
        catch (ZeroAllocOrmConcurrencyException)
        {
            // Hard-deleted between the read and the write: to the caller the memory no longer exists.
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryNotFound(id));
        }
        catch (DbException ex)
        {
            return Result<MemoryRecord, AgentError>.Failure(AgentError.MemoryStoreFailed("Could not update the memory.", ex.GetType().Name));
        }

        return Result<MemoryRecord, AgentError>.Success(ToRecord(row));
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> DeleteAsync(MemoryId id, CancellationToken ct)
    {
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var affected = await new AgentMemorySql(connection).DeleteAsync(id.Value, ct).ConfigureAwait(false);

        return affected == 1 ? UnitResult<AgentError>.Success() : UnitResult<AgentError>.Failure(AgentError.MemoryNotFound(id));
    }

    /// <inheritdoc />
    public async ValueTask<Result<MemoryPage, AgentError>> ListAsync(MemoryQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, MemoryQuery.MaxPageSize);

        // A blank query tag matches nothing, like MemoryQuery.Matches: no statement is needed to say so.
        if (ToFilter(query) is not { } filter)
        {
            return Result<MemoryPage, AgentError>.Success(new MemoryPage([], page, size, 0));
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var sql = new AgentMemorySql(connection);
        var total = await sql.CountAsync(filter, ct).ConfigureAwait(false);

        // Skip in long: Page may be int.MaxValue; a page beyond the matches is simply empty.
        var skip = (long)(page - 1) * size;
        IReadOnlyList<MemoryRecord> items = [];
        if (skip < total)
        {
            var rows = await sql.ListPageAsync(filter, skip, size, ct).ConfigureAwait(false);
            items = [.. rows.Select(ToRecord)];
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

        // ids are a set (duplicates count once); the statement touches each row once anyway, distinct keeps the parameter small.
        var guids = ids.Select(i => i.Value).Distinct().ToList();
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await new AgentMemorySql(connection).MarkRecalledAsync(guids, at.UtcDateTime, ct).ConfigureAwait(false);

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
        if (ToFilter(query) is not { } filter)
        {
            yield break; // a blank query tag matches nothing
        }

        // Row-value keyset paging: ORDER BY ("CreatedAt", "Id") and WHERE ("CreatedAt", "Id") > (@lastCreatedAt, @lastId) use the
        // same total order in SQL (uuid byte order), so ties on CreatedAt need no client-side bookkeeping.
        (DateTime CreatedAt, Guid Id)? last = null;
        while (true)
        {
            IReadOnlyList<AgentMemory> batch;
            await using (var connection = await _db.OpenAsync(ct).ConfigureAwait(false))
            {
                batch = await new AgentMemorySql(connection)
                    .StreamPageAsync(filter, last, _streamBatchSize, ct)
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

    /// <summary>
    ///     The <see cref="MemoryQuery.Matches"/> semantics as a SQL filter: null/empty filter lists mean "no filter". Null when
    ///     a query tag is blank, because that matches nothing.
    /// </summary>
    private static AgentMemoryFilter? ToFilter(MemoryQuery query)
    {
        // Every listed tag must be present (AND), normalised like stored tags. A blank query tag matches nothing.
        var tags = new List<string>();
        foreach (var raw in query.Tags ?? [])
        {
            var tag = NormaliseTag(raw);
            if (string.IsNullOrEmpty(tag))
            {
                return null;
            }

            tags.Add(tag);
        }

        return new AgentMemoryFilter(
            query.OwnerIds is { Count: > 0 } ? [.. query.OwnerIds] : [],
            query.AgentId?.Value, // pinned to this agent only (owner-wide rows need AgentId = null)
            query.Kinds is { Count: > 0 } ? [.. query.Kinds.Select(k => k.Value)] : [],
            tags,
            query.IncludeArchived,
            query.IndexPending);
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
