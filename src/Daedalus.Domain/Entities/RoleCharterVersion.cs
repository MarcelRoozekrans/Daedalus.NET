using ZeroAlloc.Results;

namespace Daedalus.Domain.Entities;

/// <summary>
///     One immutable version of a role charter's content, keyed by (<see cref="Role"/>, <see cref="ContentHash"/>).
///     Appended by <c>PostgresRoleCharterStore.UpsertAsync</c> whenever a synced charter's hash has not been seen
///     before for that role. Rows are insert-only — never updated, never deleted — so a workflow run pinned to a
///     specific charter hash can always resolve the exact body it started with, even after a later sync replaces
///     the role's current version.
/// </summary>
/// <remarks>
///     Which version is a role's current one, and whether the role is active at all, lives on
///     <see cref="RoleCharterHead"/>, not here — a variant of <see cref="Skill"/>/<see cref="SkillVersion"/>'s
///     split, except the "current" row for a charter is a thin pointer (role, current hash, active) rather than a
///     second full copy of the content: the versions table already holds every field a current row would need to
///     duplicate. Not an <see cref="Entity{TId}"/>, for the same reason <see cref="SkillVersion"/> is not: the key
///     is the composite (<see cref="Role"/>, <see cref="ContentHash"/>) pair, not a single id.
/// </remarks>
public sealed class RoleCharterVersion
{
    /// <summary>Maximum length of <see cref="Role"/>: a lowercase identifier <c>^[a-z][a-z0-9_-]{0,63}$</c>, the same rule Thalos skill/role names use.</summary>
    public const int MaxRoleLength = 64;

    /// <summary>Maximum length of <see cref="Description"/>.</summary>
    public const int MaxDescriptionLength = 300;

    /// <summary>Maximum length of <see cref="Model"/>.</summary>
    public const int MaxModelLength = 128;

    /// <summary>Maximum length of <see cref="SourcePath"/> (repo-relative, used in error messages).</summary>
    public const int MaxSourcePathLength = 1024;

    /// <summary>Maximum length of <see cref="ContentHash"/>.</summary>
    public const int MaxContentHashLength = 128;

    private readonly List<string> _skills = [];

    /// <summary>Gets the role this is a version of (also this file's own identity in <c>roles/</c>).</summary>
    public string Role { get; private set; } = string.Empty;

    /// <summary>Gets the hash of the raw charter file this version's <see cref="Instructions"/> was synced from.</summary>
    public string ContentHash { get; private set; } = string.Empty;

    /// <summary>Gets the one-line description as it read at this version.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Gets the charter body, verbatim, as it read at this version — the role's system instructions.</summary>
    public string Instructions { get; private set; } = string.Empty;

    /// <summary>Gets the role's preferred model as it read at this version, or null for the host default.</summary>
    public string? Model { get; private set; }

    /// <summary>Gets the skill names this role may load, as they read at this version.</summary>
    public IReadOnlyList<string> Skills => _skills.AsReadOnly();

    /// <summary>Gets the repo-relative path the version was synced from.</summary>
    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>Gets when this version was captured (UTC) — the synced charter's own <c>UpdatedAt</c>, not the row's insert time.</summary>
    public DateTime UpdatedAt { get; private set; }

    private RoleCharterVersion() { } // EF Core

    /// <summary>Creates a version snapshot from a synced charter's fields.</summary>
    /// <returns>A Result containing the new version or the first validation error.</returns>
    public static Result<RoleCharterVersion> Create(
        string role, string contentHash, string description, string instructions,
        string? model, IEnumerable<string>? skills, string sourcePath, DateTime updatedAt)
    {
        if (!IsValidRole(role))
            return Result<RoleCharterVersion>.Failure($"Role must match ^[a-z][a-z0-9_-]{{0,{MaxRoleLength - 1}}}$.");

        if (string.IsNullOrWhiteSpace(contentHash))
            return Result<RoleCharterVersion>.Failure("Content hash is required.");

        if (contentHash.Length > MaxContentHashLength)
            return Result<RoleCharterVersion>.Failure($"Content hash must be at most {MaxContentHashLength} characters.");

        if (string.IsNullOrWhiteSpace(description))
            return Result<RoleCharterVersion>.Failure("Description is required.");

        if (description.Length > MaxDescriptionLength)
            return Result<RoleCharterVersion>.Failure($"Description must be at most {MaxDescriptionLength} characters.");

        if (string.IsNullOrWhiteSpace(instructions))
            return Result<RoleCharterVersion>.Failure("Instructions is required.");

        if (model is { Length: > MaxModelLength })
            return Result<RoleCharterVersion>.Failure($"Model must be at most {MaxModelLength} characters.");

        if (string.IsNullOrWhiteSpace(sourcePath))
            return Result<RoleCharterVersion>.Failure("Source path is required.");

        if (sourcePath.Length > MaxSourcePathLength)
            return Result<RoleCharterVersion>.Failure($"Source path must be at most {MaxSourcePathLength} characters.");

        var version = new RoleCharterVersion
        {
            Role = role,
            ContentHash = contentHash,
            Description = description,
            Instructions = instructions,
            Model = model,
            SourcePath = sourcePath,
            UpdatedAt = updatedAt,
        };
        version._skills.AddRange(skills ?? []);
        return Result<RoleCharterVersion>.Success(version);
    }

    /// <summary>Same rule as the Thalos skill/role name: <c>^[a-z][a-z0-9_-]{0,63}$</c>.</summary>
    private static bool IsValidRole(string? role) =>
        !string.IsNullOrEmpty(role)
        && role.Length <= MaxRoleLength
        && char.IsAsciiLetterLower(role[0])
        && role.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');
}
