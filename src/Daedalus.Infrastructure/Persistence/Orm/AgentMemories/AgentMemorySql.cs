using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using AgentMemory = Daedalus.Domain.Entities.AgentMemory;

namespace Daedalus.Infrastructure.Persistence.Orm.AgentMemories;

/// <summary>
///     The SQL of the <c>"AgentMemories"</c> table, over one open connection. Its public methods take and return
///     <see cref="AgentMemory"/>; the generated queries, their companions and the row record stay inside.
/// </summary>
/// <remarks>
///     The table has no version column and was never guarded; last writer wins, and a write touches only the columns
///     it means to change, so an index flag, a recall counter and a content edit never undo one another. The insert is
///     raw SQL, as the ORM has no LINQ insert (ruling R10), and lists every column. The three filtered reads, the
///     count, a list page and a stream page, are raw because ZeroAlloc.ORM cannot yet test membership of an array
///     column in a LINQ chain (ZeroAlloc.ORM issue 450) and every one of them filters on the tags.
/// </remarks>
public sealed partial class AgentMemorySql(IAsyncDbConnection connection)
{
    // One WHERE for the count, the list page and the stream page. Each filter is switched on by a flag, so the
    // statement text never changes. "Tags" @> '{}' holds for every row, which is no tag filter.
    private const string Filter = """
        WHERE (NOT @filterOwners OR "OwnerId" = ANY(@owners))
          AND (NOT @filterAgent OR "AgentId" = @agentId)
          AND (NOT @filterKinds OR "Kind" = ANY(@kinds))
          AND "Tags" @> @tags
          AND (@includeArchived OR NOT "IsArchived")
          AND (NOT @filterPending OR "IndexPending" = @pending)
        """;

    private const string InsertSql = """
        INSERT INTO "AgentMemories"
            ("Id", "OwnerId", "AgentId", "Kind", "Text", "Tags", "Source", "Importance", "CreatedAt", "UpdatedAt",
             "LastRecalledAt", "RecallCount", "IsArchived", "IndexPending")
        VALUES (@id, @ownerId, @agentId, @kind, @text, @tags, @source, @importance, @createdAt, @updatedAt,
                @lastRecalledAt, @recallCount, @isArchived, @indexPending)
        """;

    private const string Columns = """
        "Id", "OwnerId", "AgentId", "Kind", "Text", "Tags", "Source", "Importance", "CreatedAt", "UpdatedAt",
               "LastRecalledAt", "RecallCount", "IsArchived", "IndexPending"
        """;

    /// <summary>Inserts <paramref name="memory"/> with every column as given.</summary>
    /// <param name="memory">The memory to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public async Task InsertAsync(AgentMemory memory, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(memory);

        await Insert(
            memory.Id, memory.OwnerId, memory.AgentId, memory.Kind, memory.Text, [.. memory.Tags], memory.Source,
            memory.Importance, memory.CreatedAt, memory.UpdatedAt, memory.LastRecalledAt, memory.RecallCount,
            memory.IsArchived, memory.IndexPending, ct).ConfigureAwait(false);
    }

    /// <summary>Inserts <paramref name="memories"/> in one transaction: all of them or none.</summary>
    /// <param name="memories">The memories to store.</param>
    /// <param name="ct">Cancels the inserts.</param>
    /// <returns>A task that completes when the rows are committed.</returns>
    public async Task InsertAllAsync(IReadOnlyList<AgentMemory> memories, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(memories);

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var memory in memories)
        {
            await InsertInTransaction(
                memory.Id, memory.OwnerId, memory.AgentId, memory.Kind, memory.Text, [.. memory.Tags], memory.Source,
                memory.Importance, memory.CreatedAt, memory.UpdatedAt, memory.LastRecalledAt, memory.RecallCount,
                memory.IsArchived, memory.IndexPending, tx, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the memory with <paramref name="id"/>.</summary>
    /// <param name="id">The memory id.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The memory as stored, or null when there is none.</returns>
    public async Task<AgentMemory?> FindAsync(Guid id, CancellationToken ct)
    {
        var row = await FindRow(id, ct).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <summary>
    ///     Writes the fields of an update that was applied to <paramref name="memory"/> with
    ///     <see cref="AgentMemory.Update"/>, and only those: one statement that sets each supplied column and leaves
    ///     every other one at its stored value. A content field also stamps the memory's <c>UpdatedAt</c>; the index
    ///     flag alone does not, as in the aggregate. The recall counter and the last recall time are never written, so a recall that
    ///     lands between the caller's read and this write stands. Nothing is written when no field was supplied. A
    ///     memory deleted since it was read throws <c>ZeroAllocOrmConcurrencyException</c>.
    /// </summary>
    /// <param name="memory">The memory after the update, holding the new values and the new <c>UpdatedAt</c>.</param>
    /// <param name="text">Whether the text was supplied.</param>
    /// <param name="tags">Whether the tags were supplied.</param>
    /// <param name="importance">Whether the importance was supplied.</param>
    /// <param name="isArchived">Whether the archived flag was supplied.</param>
    /// <param name="indexPending">Whether the index flag was supplied.</param>
    /// <param name="ct">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are committed.</returns>
    public async Task WriteUpdateAsync(
        AgentMemory memory, bool text, bool tags, bool importance, bool isArchived, bool indexPending,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (!(text || tags || importance || isArchived || indexPending))
        {
            return;
        }

        var content = text || tags || importance || isArchived;
        await WriteUpdate(
            memory.Id, text, memory.Text, tags, [.. memory.Tags], importance, memory.Importance, isArchived,
            memory.IsArchived, content, memory.UpdatedAt, indexPending, memory.IndexPending, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes the memory with <paramref name="id"/>.</summary>
    /// <param name="id">The memory id.</param>
    /// <param name="ct">Cancels the delete.</param>
    /// <returns>The number of rows deleted, 1 or 0.</returns>
    public Task<int> DeleteAsync(Guid id, CancellationToken ct) => Delete(id, ct);

    /// <summary>
    ///     Counts a recall on each memory in <paramref name="ids"/>: one more <c>RecallCount</c>, computed in the
    ///     statement from the stored value, and <c>LastRecalledAt</c> set to <paramref name="at"/>. Ids that are not
    ///     stored are skipped.
    /// </summary>
    /// <param name="ids">The recalled memories; a repeated id counts once.</param>
    /// <param name="at">When they were recalled.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    public async Task MarkRecalledAsync(IReadOnlyList<Guid> ids, DateTime at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        await MarkRecalled([.. ids.Distinct()], at, ct).ConfigureAwait(false);
    }

    /// <summary>Counts the memories <paramref name="filter"/> matches.</summary>
    /// <param name="filter">Which memories to count.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The number of matches.</returns>
    public Task<int> CountAsync(AgentMemoryFilter filter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return Count(
            filter.OwnerIds.Count > 0, [.. filter.OwnerIds], filter.AgentId is not null, filter.AgentId ?? Guid.Empty,
            filter.Kinds.Count > 0, [.. filter.Kinds], [.. filter.Tags], filter.IncludeArchived,
            filter.IndexPending is not null, filter.IndexPending ?? false, ct);
    }

    /// <summary>Reads one page of the memories <paramref name="filter"/> matches, newest content first.</summary>
    /// <param name="filter">Which memories to read.</param>
    /// <param name="skip">How many matches to skip.</param>
    /// <param name="take">How many matches to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The memories as stored, ordered by <c>UpdatedAt</c> then id, both descending.</returns>
    public async Task<IReadOnlyList<AgentMemory>> ListPageAsync(
        AgentMemoryFilter filter, long skip, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var rows = await ListPage(
            filter.OwnerIds.Count > 0, [.. filter.OwnerIds], filter.AgentId is not null, filter.AgentId ?? Guid.Empty,
            filter.Kinds.Count > 0, [.. filter.Kinds], [.. filter.Tags], filter.IncludeArchived,
            filter.IndexPending is not null, filter.IndexPending ?? false, skip, take, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain());
    }

    /// <summary>
    ///     Reads one keyset page of the memories <paramref name="filter"/> matches, oldest first: ordered by
    ///     <c>("CreatedAt", "Id")</c>, and when <paramref name="after"/> is given, only the rows after that
    ///     pair, so ties on the creation time are neither skipped nor repeated.
    /// </summary>
    /// <param name="filter">Which memories to read.</param>
    /// <param name="after">The last row of the previous page, or null for the first page.</param>
    /// <param name="take">How many rows to read.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The memories as stored.</returns>
    public async Task<IReadOnlyList<AgentMemory>> StreamPageAsync(
        AgentMemoryFilter filter, (DateTime CreatedAt, Guid Id)? after, int take, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var rows = await StreamPage(
            filter.OwnerIds.Count > 0, [.. filter.OwnerIds], filter.AgentId is not null, filter.AgentId ?? Guid.Empty,
            filter.Kinds.Count > 0, [.. filter.Kinds], [.. filter.Tags], filter.IncludeArchived,
            filter.IndexPending is not null, filter.IndexPending ?? false,
            after is not null, after?.CreatedAt ?? DateTime.UnixEpoch, after?.Id ?? Guid.Empty, take, ct)
            .ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain());
    }

    [Command(InsertSql)]
    private partial Task<int> Insert(
        Guid id, string ownerId, Guid? agentId, string kind, string text, string[] tags, string source,
        double importance, DateTime createdAt, DateTime updatedAt, DateTime? lastRecalledAt, int recallCount,
        bool isArchived, bool indexPending, CancellationToken ct);

    [Command(InsertSql)]
    private partial Task<int> InsertInTransaction(
        Guid id, string ownerId, Guid? agentId, string kind, string text, string[] tags, string source,
        double importance, DateTime createdAt, DateTime updatedAt, DateTime? lastRecalledAt, int recallCount,
        bool isArchived, bool indexPending, IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<AgentMemoryRow?> FindRow(Guid id, CancellationToken ct);

    private static IQuery<AgentMemoryRow> FindRowQuery(Guid id) =>
        Sql.Table<AgentMemory>()
            .Where(m => m.Id == id)
            .Select(m => new AgentMemoryRow(
                m.Id, m.OwnerId, m.AgentId, m.Kind, m.Text, m.Tags, m.Source, m.Importance, m.CreatedAt, m.UpdatedAt,
                m.LastRecalledAt, m.RecallCount, m.IsArchived, m.IndexPending));

    // One statement: each column keeps its stored value unless its flag is set (ruling R42).
    [LinqCommand(nameof(WriteUpdateQuery), ThrowOnNoRows = true)]
    private partial Task<int> WriteUpdate(
        Guid id, bool setText, string text, bool setTags, string[] tags, bool setImportance, double importance,
        bool setArchived, bool isArchived, bool setUpdatedAt, DateTime updatedAt, bool setIndexPending,
        bool indexPending, CancellationToken ct);

    private static int WriteUpdateQuery(
        Guid id, bool setText, string text, bool setTags, string[] tags, bool setImportance, double importance,
        bool setArchived, bool isArchived, bool setUpdatedAt, DateTime updatedAt, bool setIndexPending,
        bool indexPending) =>
        Sql.Table<AgentMemory>()
            .Where(m => m.Id == id)
            .ExecuteUpdate(set => set
                .SetProperty(m => m.Text, m => setText ? text : m.Text)
                .SetProperty(m => m.Tags, m => setTags ? tags : m.Tags)
                .SetProperty(m => m.Importance, m => setImportance ? importance : m.Importance)
                .SetProperty(m => m.IsArchived, m => setArchived ? isArchived : m.IsArchived)
                .SetProperty(m => m.UpdatedAt, m => setUpdatedAt ? updatedAt : m.UpdatedAt)
                .SetProperty(m => m.IndexPending, m => setIndexPending ? indexPending : m.IndexPending));

    [LinqCommand(nameof(DeleteQuery))]
    private partial Task<int> Delete(Guid id, CancellationToken ct);

    private static int DeleteQuery(Guid id) =>
        Sql.Table<AgentMemory>().Where(m => m.Id == id).ExecuteDelete();

    [LinqCommand(nameof(MarkRecalledQuery))]
    private partial Task<int> MarkRecalled(List<Guid> ids, DateTime? at, CancellationToken ct);

    private static int MarkRecalledQuery(List<Guid> ids, DateTime? at) =>
        Sql.Table<AgentMemory>()
            .Where(m => ids.Contains(m.Id))
            .ExecuteUpdate(set => set
                .SetProperty(m => m.RecallCount, m => m.RecallCount + 1)
                .SetProperty(m => m.LastRecalledAt, at));

    // The three reads below are raw, not LinqQuery: a LINQ chain cannot test membership of an array column yet, and
    // every tag must be present in the one statement. Swap them back once ZeroAlloc.ORM issue 450 ships (R37, R44).
    [Query("SELECT count(*)::int FROM \"AgentMemories\" " + Filter)]
    private partial Task<int> Count(
        bool filterOwners, string[] owners, bool filterAgent, Guid agentId, bool filterKinds, string[] kinds,
        string[] tags, bool includeArchived, bool filterPending, bool pending, CancellationToken ct);

    [Query("SELECT " + Columns + " FROM \"AgentMemories\" " + Filter
        + " ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT @take OFFSET @skip")]
    private partial Task<List<AgentMemoryRow>> ListPage(
        bool filterOwners, string[] owners, bool filterAgent, Guid agentId, bool filterKinds, string[] kinds,
        string[] tags, bool includeArchived, bool filterPending, bool pending, long skip, int take,
        CancellationToken ct);

    [Query("SELECT " + Columns + " FROM \"AgentMemories\" " + Filter
        + " AND (NOT @hasAfter OR (\"CreatedAt\", \"Id\") > (@afterCreatedAt, @afterId))"
        + " ORDER BY \"CreatedAt\", \"Id\" LIMIT @take")]
    private partial Task<List<AgentMemoryRow>> StreamPage(
        bool filterOwners, string[] owners, bool filterAgent, Guid agentId, bool filterKinds, string[] kinds,
        string[] tags, bool includeArchived, bool filterPending, bool pending, bool hasAfter, DateTime afterCreatedAt,
        Guid afterId, int take, CancellationToken ct);
}
