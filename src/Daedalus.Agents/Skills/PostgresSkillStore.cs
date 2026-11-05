using System.Data.Common;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.Skills;
using Thalos;
using Thalos.Skills;
using ZeroAlloc.ORM;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Skills;

/// <summary>
///     Thalos skill store over <see cref="SkillSql"/> (tables <c>Skills</c> and <c>SkillVersions</c>). Documents only — the
///     skill index is an in-process, rebuildable cache. One pooled connection per call (the store is a singleton).
/// </summary>
/// <remarks>
///     Files are the source of truth, so <see cref="UpsertAsync"/> is a <b>full replace</b> keyed on the name rather
///     than a patch, and <see cref="DeactivateMissingAsync"/> flips rows whose file disappeared instead of deleting
///     them (history and references stay resolvable). An upsert's timestamp comes from the document — the sync decides
///     when a skill changed — but a deactivation has no document to read, so it stamps <paramref name="clock"/> with
///     the moment the file was found missing. Validation failures come back as <see cref="AgentError"/>s;
///     Npgsql/connection exceptions propagate, the session-store policy.
/// </remarks>
public sealed class PostgresSkillStore(DaedalusDb db, TimeProvider clock) : ISkillStore
{
    private readonly DaedalusDb _db = db ?? throw new ArgumentNullException(nameof(db));

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <inheritdoc />
    public async ValueTask<Result<SkillDocument, AgentError>> UpsertAsync(SkillDocument skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var name = skill.Name.Value;
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var sql = new SkillSql(connection);
        var row = await sql.FindAsync(name, ct).ConfigureAwait(false);
        var skillExists = row is not null;

        if (row is null)
        {
            var created = Skill.Create(
                name, skill.Description, skill.Body, skill.Tags, skill.SourcePath, skill.ContentHash,
                skill.IsActive, skill.UpdatedAt.UtcDateTime);
            if (created.IsFailure)
            {
                return Result<SkillDocument, AgentError>.Failure(AgentError.SkillValidationFailed(created.Error));
            }

            row = created.Value;
        }
        else
        {
            var applied = row.Update(
                skill.Description, skill.Body, skill.Tags, skill.SourcePath, skill.ContentHash,
                skill.IsActive, skill.UpdatedAt.UtcDateTime);
            if (applied.IsFailure)
            {
                return Result<SkillDocument, AgentError>.Failure(AgentError.SkillValidationFailed(applied.Error));
            }
        }

        // Append-only history: a run pinned to this hash must be able to load it later, even after a further sync
        // replaces the current Skills row above or DeactivateMissingAsync marks it inactive. Skipped when the hash
        // has already been captured (an unchanged file re-synced, or the same content re-uploaded under the name).
        SkillVersion? addedVersion = null;
        var versionExists = await sql.VersionExistsAsync(name, skill.ContentHash, ct).ConfigureAwait(false);
        if (!versionExists)
        {
            var version = SkillVersion.Create(name, skill.ContentHash, skill.Description, skill.Body, skill.Tags, skill.SourcePath, skill.UpdatedAt.UtcDateTime);
            if (version.IsFailure)
            {
                return Result<SkillDocument, AgentError>.Failure(AgentError.SkillValidationFailed(version.Error));
            }

            addedVersion = version.Value;
        }

        // One transaction for the skill and its version, as EF's single SaveChanges was. Another host syncing the
        // same content between the check above and this write loses nothing: Daedalus.Api and Daedalus.Cli both
        // sync skills into these shared tables at start, and the version insert is ON CONFLICT DO NOTHING because
        // a version row is a pure function of its key. Two hosts both creating the same Skills row is still a
        // failure: the loser's insert breaks the primary key and rolls both rows back.
        try
        {
            await sql.StoreAsync(row, skillExists, addedVersion, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbException or ZeroAllocOrmConcurrencyException)
        {
            return Result<SkillDocument, AgentError>.Failure(
                AgentError.SkillStoreFailed("Could not store the skill.", ex.GetType().Name));
        }

        return Result<SkillDocument, AgentError>.Success(ToDocument(row));
    }

    /// <inheritdoc />
    public async ValueTask<Result<SkillDocument, AgentError>> GetVersionAsync(SkillName name, string contentHash, CancellationToken ct)
    {
        var key = name.Value;
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var sql = new SkillSql(connection);
        var version = await sql.FindVersionAsync(key, contentHash, ct).ConfigureAwait(false);

        if (version is null)
        {
            return Result<SkillDocument, AgentError>.Failure(AgentError.SkillNotFound($"{key}@{contentHash}"));
        }

        // IsActive comes from the current Skills row, not the version: a version has no activity of its own, and a
        // skill whose file has since disappeared must still report IsActive false for a run reading a pinned copy.
        var current = await sql.FindAsync(key, ct).ConfigureAwait(false);
        return Result<SkillDocument, AgentError>.Success(ToDocument(version, current?.IsActive ?? false));
    }

    /// <inheritdoc />
    public async ValueTask<Result<SkillDocument, AgentError>> GetAsync(SkillName name, CancellationToken ct)
    {
        var key = name.Value;
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var row = await new SkillSql(connection).FindAsync(key, ct).ConfigureAwait(false);

        return row is null
            ? Result<SkillDocument, AgentError>.Failure(AgentError.SkillNotFound(key))
            : Result<SkillDocument, AgentError>.Success(ToDocument(row));
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<SkillDocument>, AgentError>> ListAsync(SkillQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        IReadOnlyList<string> names = query.Names is { Count: > 0 } ? [.. query.Names.Select(n => n.Value)] : [];

        // Every listed tag must be present (AND), normalised like stored tags. A blank query tag matches nothing.
        var tags = new List<string>();
        foreach (var raw in query.Tags ?? [])
        {
            var tag = NormaliseTag(raw);
            if (string.IsNullOrEmpty(tag))
            {
                return Result<IReadOnlyList<SkillDocument>, AgentError>.Success([]);
            }

            tags.Add(tag);
        }

        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await new SkillSql(connection).ListAsync(names, tags, query.IncludeInactive, ct).ConfigureAwait(false);

        // Sorted by name: the catalogue is rendered in this order (design section 5). The sort is ordinal and therefore
        // deliberately NOT done in SQL: the contract requires code-point order ("a-b" < "a0b" < "a_b" < "aab"), and a
        // Postgres database collation such as en_US.UTF-8 treats punctuation as variable-weight and returns a different
        // order. Filtering stays in SQL; only the ordering is client-side, and the table is repo-sized.
        IReadOnlyList<SkillDocument> documents = [.. rows.OrderBy(static r => r.Id, StringComparer.Ordinal).Select(ToDocument)];
        return Result<IReadOnlyList<SkillDocument>, AgentError>.Success(documents);
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<SkillName> seen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seen);

        // Rows survive so history and references stay resolvable - a removed procedure leaves the catalogues but keeps
        // its row. UpdatedAt IS stamped: the contract records when the file stopped existing, and the IsActive filter
        // means an already-inactive skill is never stamped a second time by a later sweep.
        var names = seen.Select(n => n.Value).Distinct(StringComparer.Ordinal).ToList();
        var now = _clock.GetUtcNow().UtcDateTime;
        await using var connection = await _db.OpenAsync(ct).ConfigureAwait(false);
        await new SkillSql(connection).DeactivateMissingAsync(names, now, ct).ConfigureAwait(false);

        return UnitResult<AgentError>.Success();
    }

#pragma warning disable CA1308 // tags are lowercase identifiers by definition, not user-facing text
    private static string? NormaliseTag(string? tag) => tag?.Trim().ToLowerInvariant();
#pragma warning restore CA1308

    private static SkillDocument ToDocument(Skill s) => new()
    {
        Name = SkillName.Parse(s.Id),
        Description = s.Description,
        Body = s.Body,
        Tags = s.Tags.ToList(),
        SourcePath = s.SourcePath,
        ContentHash = s.ContentHash,
        IsActive = s.IsActive,
        UpdatedAt = new DateTimeOffset(s.UpdatedAt, TimeSpan.Zero),
    };

    private static SkillDocument ToDocument(SkillVersion v, bool isActive) => new()
    {
        Name = SkillName.Parse(v.Name),
        Description = v.Description,
        Body = v.Body,
        Tags = v.Tags.ToList(),
        SourcePath = v.SourcePath,
        ContentHash = v.ContentHash,
        IsActive = isActive,
        UpdatedAt = new DateTimeOffset(v.CreatedAt, TimeSpan.Zero),
    };
}
