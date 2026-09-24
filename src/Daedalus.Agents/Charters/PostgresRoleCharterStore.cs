using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Skills.Charters;
using ZeroAlloc.Results;

namespace Daedalus.Agents.Charters;

/// <summary>
///     Thalos role-charter store over <see cref="ApplicationDbContext"/> (tables <c>RoleCharterVersions</c> and
///     <c>RoleCharters</c>). Fresh short-lived DbContext per call (the store is a singleton), same pattern as
///     <see cref="Skills.PostgresSkillStore"/>.
/// </summary>
/// <remarks>
///     Unlike <see cref="Skills.PostgresSkillStore"/>, there is no full-content "current" table to keep in sync
///     with the version history: <c>RoleCharters</c> is a thin pointer (role, current hash, active), and
///     <see cref="ListVersionsAsync"/> computes each version's <c>IsActive</c> by joining it against that pointer
///     in memory. Files are the source of truth — nothing here is ever mutated except by a sync. Validation
///     failures come back as <see cref="AgentError"/>s; Npgsql/connection exceptions propagate, the session-store
///     policy.
/// </remarks>
public sealed class PostgresRoleCharterStore(IDbContextFactory<ApplicationDbContext> contextFactory, TimeProvider clock) : IRoleCharterStore
{
    private readonly IDbContextFactory<ApplicationDbContext> _contextFactory =
        contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));

    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <inheritdoc />
    public async ValueTask<Result<RoleCharter, AgentError>> UpsertAsync(RoleCharter charter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(charter);

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Append-only history: a run pinned to this hash must be able to resolve it later, even after a further
        // sync repoints RoleCharters at a different version. Skipped when the hash has already been captured (an
        // unchanged file re-synced, or a hash re-appearing after a rollback).
        var versionExists = await db.RoleCharterVersions
            .AnyAsync(v => v.Role == charter.Role && v.ContentHash == charter.ContentHash, ct).ConfigureAwait(false);
        if (!versionExists)
        {
            var created = RoleCharterVersion.Create(
                charter.Role, charter.ContentHash, charter.Description, charter.Instructions,
                charter.Model, charter.Skills, charter.SourcePath, charter.UpdatedAt.UtcDateTime);
            if (created.IsFailure)
            {
                return Result<RoleCharter, AgentError>.Failure(AgentError.SkillValidationFailed(created.Error));
            }

            db.RoleCharterVersions.Add(created.Value);
        }

        var head = await db.RoleCharters.FirstOrDefaultAsync(h => h.Role == charter.Role, ct).ConfigureAwait(false);
        if (head is null)
        {
            db.RoleCharters.Add(RoleCharterHead.Create(charter.Role, charter.ContentHash, charter.UpdatedAt.UtcDateTime));
        }
        else
        {
            head.SetCurrent(charter.ContentHash, charter.UpdatedAt.UtcDateTime);
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            return Result<RoleCharter, AgentError>.Failure(
                AgentError.SkillStoreFailed("Could not store the role charter.", ex.GetType().Name));
        }

        // The stored (and returned) charter just became its role's current, active version: IsActive is store
        // state, not something the caller gets to dictate (mirrors InMemoryRoleCharterStore's contract).
        return Result<RoleCharter, AgentError>.Success(charter with { IsActive = true });
    }

    /// <inheritdoc />
    public async ValueTask<Result<IReadOnlyList<RoleCharter>, AgentError>> ListVersionsAsync(CancellationToken ct)
    {
        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var versions = await db.RoleCharterVersions.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var heads = await db.RoleCharters.AsNoTracking().ToDictionaryAsync(h => h.Role, StringComparer.Ordinal, ct).ConfigureAwait(false);

        IReadOnlyList<RoleCharter> all = versions.ConvertAll(v => ToCharter(v, heads));
        return Result<IReadOnlyList<RoleCharter>, AgentError>.Success(all);
    }

    /// <inheritdoc />
    public async ValueTask<UnitResult<AgentError>> DeactivateMissingAsync(IReadOnlyList<string> seenRoles, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(seenRoles);

        // Pointer rows survive so history stays resolvable - a role that disappears from every root keeps its
        // RoleCharters row, only IsActive flips. UpdatedAt is stamped from the clock (there is no document to
        // read a timestamp from for a deactivation), and the IsActive filter means an already-inactive role is
        // never stamped a second time by a later sweep.
        var roles = seenRoles.Distinct(StringComparer.Ordinal).ToList();
        var now = _clock.GetUtcNow().UtcDateTime;

        await using var db = await _contextFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.RoleCharters
            .Where(h => h.IsActive && !roles.Contains(h.Role))
            .ExecuteUpdateAsync(
                set => set.SetProperty(h => h.IsActive, false).SetProperty(h => h.UpdatedAt, now), ct)
            .ConfigureAwait(false);

        return UnitResult<AgentError>.Success();
    }

    /// <summary>
    ///     Composes one <see cref="RoleCharter"/> from an immutable version row plus its role's current head, if
    ///     any. A version that <em>is</em> its role's current one (<see cref="RoleCharterHead.CurrentHash"/>
    ///     matches) reports the head's <see cref="RoleCharterHead.UpdatedAt"/> and
    ///     <see cref="RoleCharterHead.IsActive"/> — the head is what <see cref="DeactivateMissingAsync"/> stamps,
    ///     since a version row itself is never rewritten after insert. Every other, superseded version keeps its
    ///     own original <see cref="RoleCharterVersion.UpdatedAt"/> and reports inactive: a role's history is not
    ///     touched by a later sync or sweep, only which hash is current.
    /// </summary>
    private static RoleCharter ToCharter(RoleCharterVersion v, Dictionary<string, RoleCharterHead> heads)
    {
        var isCurrent = heads.TryGetValue(v.Role, out var head) && string.Equals(head.CurrentHash, v.ContentHash, StringComparison.Ordinal);
        var isActive = isCurrent && head!.IsActive;
        var updatedAt = isCurrent ? head!.UpdatedAt : v.UpdatedAt;

        return new RoleCharter
        {
            Role = v.Role,
            Description = v.Description,
            Instructions = v.Instructions,
            Model = v.Model,
            Skills = v.Skills.ToList(),
            SourcePath = v.SourcePath,
            ContentHash = v.ContentHash,
            IsActive = isActive,
            UpdatedAt = new DateTimeOffset(updatedAt, TimeSpan.Zero),
        };
    }
}
