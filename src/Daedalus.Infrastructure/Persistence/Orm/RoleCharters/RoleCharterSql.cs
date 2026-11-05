using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using RoleCharterHead = Daedalus.Domain.Entities.RoleCharterHead;
using RoleCharterVersion = Daedalus.Domain.Entities.RoleCharterVersion;

namespace Daedalus.Infrastructure.Persistence.Orm.RoleCharters;

/// <summary>
///     The SQL of the <c>"RoleCharterVersions"</c> and <c>"RoleCharters"</c> tables, over one open connection. Its
///     public methods take and return <see cref="RoleCharterVersion"/> and <see cref="RoleCharterHead"/>; the
///     generated queries, their companions and the row records stay inside.
/// </summary>
/// <remarks>
///     The versions table is insert-only. The head table has one row per role: a first sync inserts it and a later
///     one updates it, as two separate statements, so two writers that both see no head race on the primary key and
///     the loser's insert fails with Npgsql's <c>PostgresException</c>, unwrapped, as before. No row has a version
///     column and neither table was ever guarded; last writer wins. The inserts are raw SQL, as the ORM has no LINQ
///     insert (ruling R10), and list every column.
/// </remarks>
public sealed partial class RoleCharterSql(IAsyncDbConnection connection)
{
    /// <summary>Whether a version of <paramref name="role"/> with content hash <paramref name="contentHash"/> is stored.</summary>
    /// <param name="role">The role.</param>
    /// <param name="contentHash">The content hash of the charter file.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>True when the version exists.</returns>
    public Task<bool> VersionExistsAsync(string role, string contentHash, CancellationToken ct) =>
        VersionExists(role, contentHash, ct);

    /// <summary>Appends <paramref name="version"/>; a version with the same role and hash fails on the primary key.</summary>
    /// <param name="version">The version to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public Task InsertVersionAsync(RoleCharterVersion version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(version);
        return InsertVersion(
            version.Role, version.ContentHash, version.Description, version.Instructions, version.Model,
            version.SourcePath, version.UpdatedAt, [.. version.Skills], ct);
    }

    /// <summary>Reads every stored version.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The versions, as stored.</returns>
    public async Task<IReadOnlyList<RoleCharterVersion>> ListVersionsAsync(CancellationToken ct)
    {
        var rows = await ListVersionRows(ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain());
    }

    /// <summary>Reads the head of <paramref name="role"/>.</summary>
    /// <param name="role">The role.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The head as stored, or null when the role has none.</returns>
    public async Task<RoleCharterHead?> FindHeadAsync(string role, CancellationToken ct)
    {
        var row = await FindHeadRow(role, ct).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <summary>Reads every head.</summary>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The heads, as stored.</returns>
    public async Task<IReadOnlyList<RoleCharterHead>> ListHeadsAsync(CancellationToken ct)
    {
        var rows = await ListHeadRows(ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain());
    }

    /// <summary>Inserts <paramref name="head"/> as a role's first head; a second head for the role fails on the primary key.</summary>
    /// <param name="head">The head to store.</param>
    /// <param name="ct">Cancels the insert.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public Task InsertHeadAsync(RoleCharterHead head, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(head);
        return InsertHead(head.Role, head.CurrentHash, head.IsActive, head.UpdatedAt, ct);
    }

    /// <summary>
    ///     Repoints the head of <paramref name="head"/>'s role at its current hash, active, with its update time. Only
    ///     the three columns <see cref="RoleCharterHead.SetCurrent"/> changes are written.
    /// </summary>
    /// <param name="head">The head, after <see cref="RoleCharterHead.SetCurrent"/>.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the row is written.</returns>
    public Task UpdateHeadAsync(RoleCharterHead head, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(head);
        return SetCurrent(head.Role, head.CurrentHash, head.UpdatedAt, ct);
    }

    /// <summary>
    ///     Deactivates every active head whose role is not in <paramref name="seenRoles"/>, stamping
    ///     <paramref name="updatedAt"/>. A head that is inactive already is left alone, so a later sweep never stamps
    ///     it twice.
    /// </summary>
    /// <param name="seenRoles">The roles still synced from a configured root.</param>
    /// <param name="updatedAt">The time to stamp on each deactivated head.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    public Task DeactivateMissingAsync(IReadOnlyList<string> seenRoles, DateTime updatedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seenRoles);
        return DeactivateMissing([.. seenRoles], updatedAt, ct);
    }

    [LinqQuery(nameof(VersionExistsQuery))]
    private partial Task<bool> VersionExists(string role, string contentHash, CancellationToken ct);

    private static IQuery<bool> VersionExistsQuery(string role, string contentHash) =>
        Sql.Table<RoleCharterVersion>()
            .Where(v => string.Equals(v.Role, role, StringComparison.Ordinal)
                && string.Equals(v.ContentHash, contentHash, StringComparison.Ordinal))
            .Any();

    [Command("""
        INSERT INTO "RoleCharterVersions"
            ("Role", "ContentHash", "Description", "Instructions", "Model", "SourcePath", "UpdatedAt", "Skills")
        VALUES (@role, @contentHash, @description, @instructions, @model, @sourcePath, @updatedAt, @skills)
        """)]
    private partial Task<int> InsertVersion(
        string role, string contentHash, string description, string instructions, string? model, string sourcePath,
        DateTime updatedAt, string[] skills, CancellationToken ct);

    [LinqQuery(nameof(ListVersionRowsQuery))]
    private partial Task<List<RoleCharterVersionRow>> ListVersionRows(CancellationToken ct);

    private static IQuery<RoleCharterVersionRow> ListVersionRowsQuery() =>
        Sql.Table<RoleCharterVersion>()
            .Select(v => new RoleCharterVersionRow(
                v.Role, v.ContentHash, v.Description, v.Instructions, v.Model, v.SourcePath, v.UpdatedAt, v.Skills));

    [LinqQuery(nameof(FindHeadRowQuery))]
    private partial Task<RoleCharterHeadRow?> FindHeadRow(string role, CancellationToken ct);

    private static IQuery<RoleCharterHeadRow> FindHeadRowQuery(string role) =>
        Sql.Table<RoleCharterHead>()
            .Where(h => string.Equals(h.Role, role, StringComparison.Ordinal))
            .Select(h => new RoleCharterHeadRow(h.Role, h.CurrentHash, h.IsActive, h.UpdatedAt));

    [LinqQuery(nameof(ListHeadRowsQuery))]
    private partial Task<List<RoleCharterHeadRow>> ListHeadRows(CancellationToken ct);

    private static IQuery<RoleCharterHeadRow> ListHeadRowsQuery() =>
        Sql.Table<RoleCharterHead>()
            .Select(h => new RoleCharterHeadRow(h.Role, h.CurrentHash, h.IsActive, h.UpdatedAt));

    [Command("""
        INSERT INTO "RoleCharters" ("Role", "CurrentHash", "IsActive", "UpdatedAt")
        VALUES (@role, @currentHash, @isActive, @updatedAt)
        """)]
    private partial Task<int> InsertHead(string role, string currentHash, bool isActive, DateTime updatedAt, CancellationToken ct);

    [LinqCommand(nameof(SetCurrentQuery))]
    private partial Task<int> SetCurrent(string role, string currentHash, DateTime updatedAt, CancellationToken ct);

    private static int SetCurrentQuery(string role, string currentHash, DateTime updatedAt) =>
        Sql.Table<RoleCharterHead>()
            .Where(h => string.Equals(h.Role, role, StringComparison.Ordinal))
            .ExecuteUpdate(set => set
                .SetProperty(h => h.CurrentHash, currentHash)
                .SetProperty(h => h.IsActive, true)
                .SetProperty(h => h.UpdatedAt, updatedAt));

    [LinqCommand(nameof(DeactivateMissingQuery))]
    private partial Task<int> DeactivateMissing(List<string> roles, DateTime updatedAt, CancellationToken ct);

    private static int DeactivateMissingQuery(List<string> roles, DateTime updatedAt) =>
        Sql.Table<RoleCharterHead>()
            .Where(h => h.IsActive && !roles.Contains(h.Role))
            .ExecuteUpdate(set => set
                .SetProperty(h => h.IsActive, false)
                .SetProperty(h => h.UpdatedAt, updatedAt));
}
