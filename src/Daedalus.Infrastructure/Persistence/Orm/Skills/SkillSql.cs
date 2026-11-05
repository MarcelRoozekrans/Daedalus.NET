using System.Data.Async;
using ZeroAlloc.ORM;
using ZeroAlloc.ORM.Linq;
using Skill = Daedalus.Domain.Entities.Skill;
using SkillVersion = Daedalus.Domain.Entities.SkillVersion;

namespace Daedalus.Infrastructure.Persistence.Orm.Skills;

/// <summary>
///     The SQL of the <c>"Skills"</c> and <c>"SkillVersions"</c> tables, over one open connection. Its public
///     methods take and return <see cref="Skill"/> and <see cref="SkillVersion"/>; the generated queries, their
///     companions and the row records stay inside.
/// </summary>
/// <remarks>
///     Neither table has a version column and neither was ever guarded; last writer wins. The versions table is
///     insert-only. The inserts are raw SQL, as the ORM has no LINQ insert (ruling R10), and list every column. The
///     list read is one raw query because ZeroAlloc.ORM cannot yet test membership of an array column in a LINQ
///     chain (ZeroAlloc.ORM issue 450).
/// </remarks>
public sealed partial class SkillSql(IAsyncDbConnection connection)
{
    /// <summary>Whether a version of <paramref name="name"/> with content hash <paramref name="contentHash"/> is stored.</summary>
    /// <param name="name">The skill name.</param>
    /// <param name="contentHash">The content hash of the skill file.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>True when the version exists.</returns>
    public Task<bool> VersionExistsAsync(string name, string contentHash, CancellationToken ct) =>
        VersionExists(name, contentHash, ct);

    /// <summary>
    ///     Stores a synced skill in one transaction: the skill row, inserted when <paramref name="skillExists"/> is
    ///     false and replaced otherwise, then the new <paramref name="version"/> when there is one, inserted with
    ///     <c>ON CONFLICT DO NOTHING</c> because a version row is a pure function of its key, so a host that lost the
    ///     race to write it has nothing left to write. The rows commit together or not at all, as EF's single save
    ///     did; any other failure, such as two hosts inserting the same skill, rolls both back and surfaces as
    ///     Npgsql's <c>PostgresException</c>. A replace that finds no row, a skill deleted since it was read, throws
    ///     <c>ZeroAllocOrmConcurrencyException</c> and rolls back, as EF's update of a missing row threw.
    /// </summary>
    /// <param name="skill">The skill to insert or, after <see cref="Skill.Update"/>, to replace.</param>
    /// <param name="skillExists">Whether the skill has a row already.</param>
    /// <param name="version">The version to append, or null when its hash is stored already.</param>
    /// <param name="ct">Cancels the writes.</param>
    /// <returns>A task that completes when the rows are committed.</returns>
    public async Task StoreAsync(Skill skill, bool skillExists, SkillVersion? version, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (skillExists)
        {
            await ReplaceSkill(
                skill.Id, skill.Description, skill.Body, [.. skill.Tags], skill.SourcePath, skill.ContentHash,
                skill.IsActive, skill.UpdatedAt, tx, ct).ConfigureAwait(false);
        }
        else
        {
            await InsertSkill(
                skill.Id, skill.Description, skill.Body, [.. skill.Tags], skill.SourcePath, skill.ContentHash,
                skill.IsActive, skill.UpdatedAt, tx, ct).ConfigureAwait(false);
        }

        if (version is not null)
        {
            await InsertVersion(
                version.Name, version.ContentHash, version.Description, version.Body, [.. version.Tags],
                version.SourcePath, version.CreatedAt, tx, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads the skill called <paramref name="name"/>.</summary>
    /// <param name="name">The skill name.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The skill as stored, or null when there is none.</returns>
    public async Task<Skill?> FindAsync(string name, CancellationToken ct)
    {
        var row = await FindRow(name, ct).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <summary>Reads one version of a skill.</summary>
    /// <param name="name">The skill name.</param>
    /// <param name="contentHash">The content hash of the version.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The version as stored, or null when there is none.</returns>
    public async Task<SkillVersion?> FindVersionAsync(string name, string contentHash, CancellationToken ct)
    {
        var row = await FindVersionRow(name, contentHash, ct).ConfigureAwait(false);
        return row?.ToDomain();
    }

    /// <summary>
    ///     Lists skills. Every filter is in the one statement: names when there are any, every tag in
    ///     <paramref name="tags"/> (none means no tag filter) and, unless asked for, only active skills. The rows
    ///     come back in no particular order; the caller sorts them ordinally.
    /// </summary>
    /// <param name="names">The names to keep, or empty for all.</param>
    /// <param name="tags">The tags each skill must carry, or empty for any.</param>
    /// <param name="includeInactive">Whether inactive skills are listed too.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>The skills as stored.</returns>
    public async Task<IReadOnlyList<Skill>> ListAsync(
        IReadOnlyList<string> names, IReadOnlyList<string> tags, bool includeInactive, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(tags);

        var rows = await ListRows(names.Count > 0, [.. names], [.. tags], includeInactive, ct).ConfigureAwait(false);
        return rows.ConvertAll(row => row.ToDomain());
    }

    /// <summary>
    ///     Deactivates every active skill whose name is not in <paramref name="seenNames"/>, stamping
    ///     <paramref name="updatedAt"/>. A skill that is inactive already is left alone, so a later sweep never stamps
    ///     it twice.
    /// </summary>
    /// <param name="seenNames">The skills still synced from a configured root.</param>
    /// <param name="updatedAt">The time to stamp on each deactivated skill.</param>
    /// <param name="ct">Cancels the update.</param>
    /// <returns>A task that completes when the rows are written.</returns>
    public Task DeactivateMissingAsync(IReadOnlyList<string> seenNames, DateTime updatedAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seenNames);
        return DeactivateMissing([.. seenNames], updatedAt, ct);
    }

    [LinqQuery(nameof(VersionExistsQuery))]
    private partial Task<bool> VersionExists(string name, string contentHash, CancellationToken ct);

    private static IQuery<bool> VersionExistsQuery(string name, string contentHash) =>
        Sql.Table<SkillVersion>()
            .Where(v => string.Equals(v.Name, name, StringComparison.Ordinal)
                && string.Equals(v.ContentHash, contentHash, StringComparison.Ordinal))
            .Any();

    [Command("""
        INSERT INTO "Skills"
            ("Name", "Description", "Body", "Tags", "SourcePath", "ContentHash", "IsActive", "UpdatedAt")
        VALUES (@name, @description, @body, @tags, @sourcePath, @contentHash, @isActive, @updatedAt)
        """)]
    private partial Task<int> InsertSkill(
        string name, string description, string body, string[] tags, string sourcePath, string contentHash,
        bool isActive, DateTime updatedAt, IAsyncDbTransaction tx, CancellationToken ct);

    [LinqCommand(nameof(ReplaceSkillQuery), ThrowOnNoRows = true)]
    private partial Task<int> ReplaceSkill(
        string name, string description, string body, string[] tags, string sourcePath, string contentHash,
        bool isActive, DateTime updatedAt, IAsyncDbTransaction tx, CancellationToken ct);

    private static int ReplaceSkillQuery(
        string name, string description, string body, string[] tags, string sourcePath, string contentHash,
        bool isActive, DateTime updatedAt) =>
        Sql.Table<Skill>()
            .Where(s => string.Equals(s.Id, name, StringComparison.Ordinal))
            .ExecuteUpdate(set => set
                .SetProperty(s => s.Description, description)
                .SetProperty(s => s.Body, body)
                .SetProperty(s => s.Tags, tags)
                .SetProperty(s => s.SourcePath, sourcePath)
                .SetProperty(s => s.ContentHash, contentHash)
                .SetProperty(s => s.IsActive, isActive)
                .SetProperty(s => s.UpdatedAt, updatedAt));

    [Command("""
        INSERT INTO "SkillVersions"
            ("Name", "ContentHash", "Description", "Body", "Tags", "SourcePath", "CreatedAt")
        VALUES (@name, @contentHash, @description, @body, @tags, @sourcePath, @createdAt)
        ON CONFLICT ("Name", "ContentHash") DO NOTHING
        """)]
    private partial Task<int> InsertVersion(
        string name, string contentHash, string description, string body, string[] tags, string sourcePath,
        DateTime createdAt, IAsyncDbTransaction tx, CancellationToken ct);

    [LinqQuery(nameof(FindRowQuery))]
    private partial Task<SkillRow?> FindRow(string name, CancellationToken ct);

    private static IQuery<SkillRow> FindRowQuery(string name) =>
        Sql.Table<Skill>()
            .Where(s => string.Equals(s.Id, name, StringComparison.Ordinal))
            .Select(s => new SkillRow(
                s.Id, s.Description, s.Body, s.Tags, s.SourcePath, s.ContentHash, s.IsActive, s.UpdatedAt));

    [LinqQuery(nameof(FindVersionRowQuery))]
    private partial Task<SkillVersionRow?> FindVersionRow(string name, string contentHash, CancellationToken ct);

    private static IQuery<SkillVersionRow> FindVersionRowQuery(string name, string contentHash) =>
        Sql.Table<SkillVersion>()
            .Where(v => string.Equals(v.Name, name, StringComparison.Ordinal)
                && string.Equals(v.ContentHash, contentHash, StringComparison.Ordinal))
            .Select(v => new SkillVersionRow(
                v.Name, v.ContentHash, v.Description, v.Body, v.Tags, v.SourcePath, v.CreatedAt));

    // Raw, not a LinqQuery: a LINQ chain cannot test membership of an array column yet, and the tags must all be
    // present in the one statement. Swap back to a LinqQuery once ZeroAlloc.ORM issue 450 ships (ruling R37).
    [Query("""
        SELECT "Name", "Description", "Body", "Tags", "SourcePath", "ContentHash", "IsActive", "UpdatedAt"
        FROM "Skills"
        WHERE (NOT @filterNames OR "Name" = ANY(@names))
          AND "Tags" @> @tags
          AND (@includeInactive OR "IsActive")
        """)]
    private partial Task<List<SkillRow>> ListRows(
        bool filterNames, string[] names, string[] tags, bool includeInactive, CancellationToken ct);

    [LinqCommand(nameof(DeactivateMissingQuery))]
    private partial Task<int> DeactivateMissing(List<string> names, DateTime updatedAt, CancellationToken ct);

    private static int DeactivateMissingQuery(List<string> names, DateTime updatedAt) =>
        Sql.Table<Skill>()
            .Where(s => s.IsActive && !names.Contains(s.Id))
            .ExecuteUpdate(set => set
                .SetProperty(s => s.IsActive, false)
                .SetProperty(s => s.UpdatedAt, updatedAt));
}
