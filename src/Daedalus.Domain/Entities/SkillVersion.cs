using ZeroAlloc.Results;

namespace Daedalus.Domain.Entities;

/// <summary>
///     One immutable version of a <see cref="Skill"/>'s content, keyed by (<see cref="Name"/>, <see cref="ContentHash"/>).
///     Appended by <c>PostgresSkillStore.UpsertAsync</c> whenever a synced document's hash has not been seen before for
///     that name. Rows are insert-only — never updated, never deleted — so a workflow run pinned to a specific hash
///     can always load the exact body it started with, even after a later sync replaces the current <see cref="Skill"/>
///     row or the skill is deactivated.
/// </summary>
/// <remarks>
///     Not an <see cref="Entity{TId}"/>: the key is the composite (<see cref="Name"/>, <see cref="ContentHash"/>) pair,
///     not a single id, so it follows <see cref="Skill"/>'s own style instead — <c>private SkillVersion()</c> for EF,
///     a validating static factory, read-only properties. Limits mirror <see cref="Skill"/>'s own constants exactly,
///     since a version is a snapshot of a document that was already validated as a <see cref="Skill"/> once.
/// </remarks>
public sealed class SkillVersion
{
    private readonly List<string> _tags = [];

    /// <summary>Gets the skill name this is a version of.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Gets the hash of the raw file this version's <see cref="Body"/> was synced from.</summary>
    public string ContentHash { get; private set; } = string.Empty;

    /// <summary>Gets the one-line description as it read at this version.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Gets the procedure body, verbatim, as it read at this version.</summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>Gets the normalised tags (lower-case, distinct, insertion order) as they read at this version.</summary>
    public IReadOnlyList<string> Tags => _tags.AsReadOnly();

    /// <summary>Gets the repo-relative path the version was synced from.</summary>
    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>Gets when this version was created (UTC) — the synced document's own <c>UpdatedAt</c>, not the row's insert time.</summary>
    public DateTime CreatedAt { get; private set; }

    private SkillVersion() { } // EF Core

    /// <summary>Creates a version snapshot from a synced document's fields. Rejects the same shapes <see cref="Skill.Create"/> rejects.</summary>
    /// <returns>A Result containing the new version or the first validation error.</returns>
    public static Result<SkillVersion> Create(
        string name, string contentHash, string description, string body,
        IEnumerable<string>? tags, string sourcePath, DateTime createdAt)
    {
        if (!IsValidName(name))
            return Result<SkillVersion>.Failure($"Name must match ^[a-z][a-z0-9_-]{{0,{Skill.MaxNameLength - 1}}}$.");

        if (string.IsNullOrWhiteSpace(contentHash))
            return Result<SkillVersion>.Failure("Content hash is required.");

        if (contentHash.Length > Skill.MaxContentHashLength)
            return Result<SkillVersion>.Failure($"Content hash must be at most {Skill.MaxContentHashLength} characters.");

        var fields = ValidateFields(description, body, tags, sourcePath);
        if (fields.IsFailure)
            return Result<SkillVersion>.Failure(fields.Error);

        var version = new SkillVersion
        {
            Name = name,
            ContentHash = contentHash,
            Description = description,
            Body = body,
            SourcePath = sourcePath,
            CreatedAt = createdAt,
        };
        version._tags.AddRange(fields.Value);
        return Result<SkillVersion>.Success(version);
    }

    /// <summary>Same rule as the Thalos skill name: <c>^[a-z][a-z0-9_-]{0,63}$</c>.</summary>
    private static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= Skill.MaxNameLength
        && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    private static Result<List<string>> ValidateFields(string description, string body, IEnumerable<string>? tags, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(description))
            return Result<List<string>>.Failure("Description is required.");

        if (description.Length > Skill.MaxDescriptionLength)
            return Result<List<string>>.Failure($"Description must be at most {Skill.MaxDescriptionLength} characters.");

        if (string.IsNullOrWhiteSpace(body))
            return Result<List<string>>.Failure("Body is required.");

        if (body.Length > Skill.MaxBodyLength)
            return Result<List<string>>.Failure($"Body must be at most {Skill.MaxBodyLength} characters.");

        if (string.IsNullOrWhiteSpace(sourcePath))
            return Result<List<string>>.Failure("Source path is required.");

        return sourcePath.Length > Skill.MaxSourcePathLength
            ? Result<List<string>>.Failure($"Source path must be at most {Skill.MaxSourcePathLength} characters.")
            : NormaliseTags(tags);
    }

    private static Result<List<string>> NormaliseTags(IEnumerable<string>? tags)
    {
#pragma warning disable CA1308 // tags are lowercase identifiers by definition, not user-facing text
        var list = (tags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
#pragma warning restore CA1308

        if (list.Count > Skill.MaxTags)
            return Result<List<string>>.Failure($"At most {Skill.MaxTags} tags are allowed.");

        return list.Exists(t => t.Length > Skill.MaxTagLength)
            ? Result<List<string>>.Failure($"Tags must be at most {Skill.MaxTagLength} characters.")
            : Result<List<string>>.Success(list);
    }
}
