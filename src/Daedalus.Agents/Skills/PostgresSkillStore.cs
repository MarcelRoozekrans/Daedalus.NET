using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Skills;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Skills;

/// <summary>
///     Thalos skill store over <see cref="ApplicationDbContext"/> (table <c>Skills</c>). Documents only — the skill
///     index is an in-process, rebuildable cache. Fresh short-lived DbContext per call (the store is a singleton), same
///     patterns as <see cref="Memory.PostgresMemoryStore"/>.
/// </summary>
/// <remarks>
///     Files are the source of truth, so <see cref="UpsertAsync"/> is a <b>full replace</b> keyed on the name rather
///     than a patch, and <see cref="DeactivateMissingAsync"/> flips rows whose file disappeared instead of deleting
///     them (history and references stay resolvable). An upsert's timestamp comes from the document — the sync decides
///     when a skill changed — but a deactivation has no document to read, so it stamps <paramref name="clock"/> with
///     the moment the file was found missing. Validation failures come back as <see cref="AgentError"/>s;
///     Npgsql/connection exceptions propagate, the session-store policy.
/// </remarks>
public sealed class PostgresSkillStore(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock) : ISkillStore
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <inheritdoc />
    public async ValueTask<Result<SkillDocument, AgentError>> UpsertAsync(SkillDocument skill, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var name = skill.Name.Value;
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Skills.FirstOrDefaultAsync(s => s.Id == name, ct).ConfigureAwait(false);

        if (row is null)
        {
            var created = Skill.Create(
                name, skill.Description, skill.Body, skill.Tags, skill.SourcePath, skill.ContentHash,
                skill.IsActive, skill.UpdatedAt.UtcDateTime);
            if (created.IsFailure)
            {
                return Result<SkillDocument, AgentError>.Failure(AgentError.SkillValidationFailed(created.Error));
            }

            db.Skills.Add(created.Value);
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

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            return Result<SkillDocument, AgentError>.Failure(
                AgentError.SkillStoreFailed("Could not store the skill.", ex.GetType().Name));
        }

        return Result<SkillDocument, AgentError>.Success(ToDocument(row));
    }

    /// <inheritdoc />
    public async ValueTask<Result<SkillDocument, AgentError>> GetAsync(SkillName name, CancellationToken ct)
    {
        var key = name.Value;
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var row = await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Id == key, ct).ConfigureAwait(false);

        return row is null
            ? Result<SkillDocument, AgentError>.Failure(AgentError.SkillNotFound(key))
            : Result<SkillDocument, AgentError>.Success(ToDocument(row));
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<SkillDocument>, AgentError>> ListAsync(SkillQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Skills.AsNoTracking().AsQueryable();

        if (query.Names is { Count: > 0 })
        {
            var names = query.Names.Select(n => n.Value).ToList();
            q = q.Where(s => names.Contains(s.Id));
        }

        if (query.Tags is { Count: > 0 })
        {
            // Every listed tag must be present (AND), normalised like stored tags. A blank query tag matches nothing.
            foreach (var raw in query.Tags)
            {
                var tag = NormaliseTag(raw);
                if (string.IsNullOrEmpty(tag))
                {
                    q = q.Where(s => false);
                    break;
                }

                q = q.Where(s => EF.Property<List<string>>(s, "_tags").Contains(tag)); // Npgsql: @tag = ANY("Tags")
            }
        }

        if (!query.IncludeInactive)
        {
            q = q.Where(s => s.IsActive);
        }

        // Sorted by name: the catalogue is rendered in this order (design section 5). The sort is ordinal and therefore
        // deliberately NOT done in SQL: the contract requires code-point order ("a-b" < "a0b" < "a_b" < "aab"), and a
        // Postgres database collation such as en_US.UTF-8 treats punctuation as variable-weight and returns a different
        // order. Filtering stays in SQL; only the ordering is client-side, and the table is repo-sized.
        var rows = await q.ToListAsync(ct).ConfigureAwait(false);
        rows.Sort(static (a, b) => string.CompareOrdinal(a.Id, b.Id));
        return Result<IReadOnlyList<SkillDocument>, AgentError>.Success(rows.ConvertAll(ToDocument));
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
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Skills
            .Where(s => s.IsActive && !names.Contains(s.Id))
            .ExecuteUpdateAsync(
                set => set.SetProperty(s => s.IsActive, false).SetProperty(s => s.UpdatedAt, now), ct)
            .ConfigureAwait(false);

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
}
