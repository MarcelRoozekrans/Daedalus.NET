using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     One agent skill: a named procedure document authored in git and synced into the database (Thalos
///     <c>SkillDocument</c> persisted by Daedalus). Domain stays framework-free: the id is the skill name, times are UTC.
///     Embeddings are not stored here — the skill index is a rebuildable, in-process cache.
/// </summary>
/// <remarks>
///     Limits mirror the Thalos skill rules (name <c>^[a-z][a-z0-9_-]{0,63}$</c>, description ≤ 300, body ≤ 64 KB,
///     ≤ 10 tags of ≤ 32 chars), so a violation is a validation error, never a database constraint failure. The body is
///     stored <b>verbatim</b> — what the model reads is byte-for-byte what is in git — and only tags are normalised
///     (trimmed, lower-cased, blanks dropped, de-duplicated), like <see cref="AgentMemory"/>.
/// </remarks>
public sealed class Skill : Entity<string>
{
    /// <summary>Maximum length of the name (the primary key): a lowercase identifier <c>^[a-z][a-z0-9_-]{0,63}$</c>.</summary>
    public const int MaxNameLength = 64;

    /// <summary>Maximum length of <see cref="Description"/> — it appears in every catalogue, so it stays short.</summary>
    public const int MaxDescriptionLength = 300;

    /// <summary>Maximum length of <see cref="Body"/>: 64 K UTF-16 units, so one runaway file cannot blow a context window.</summary>
    public const int MaxBodyLength = 64 * 1024;

    /// <summary>Maximum number of <see cref="Tags"/>.</summary>
    public const int MaxTags = 10;

    /// <summary>Maximum length of one tag.</summary>
    public const int MaxTagLength = 32;

    /// <summary>Maximum length of <see cref="SourcePath"/> (repo-relative, used in error messages).</summary>
    public const int MaxSourcePathLength = 1024;

    /// <summary>Maximum length of <see cref="ContentHash"/>; the encoding is the library's business (hex or base64).</summary>
    public const int MaxContentHashLength = 128;

    private readonly List<string> _tags = [];

    /// <summary>Gets the one-line description shown in every catalogue.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Gets the procedure body, verbatim (everything after the file's frontmatter).</summary>
    public string Body { get; private set; } = string.Empty;

    /// <summary>Gets the normalised tags (lower-case, distinct, insertion order).</summary>
    public IReadOnlyList<string> Tags => _tags.AsReadOnly();

    /// <summary>Gets the repo-relative path the skill was loaded from.</summary>
    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>Gets the hash of the raw file; an unchanged hash means the sync skips the file entirely.</summary>
    public string ContentHash { get; private set; } = string.Empty;

    /// <summary>Gets a value indicating whether the file still exists on disk. Inactive skills leave the catalogues but keep their row.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>Gets when the skill was last synced from a changed file (UTC).</summary>
    public DateTime UpdatedAt { get; private set; }

    private Skill() { } // EF Core

    /// <summary>Creates a skill from a synced document. Timestamps are supplied by the caller (UTC).</summary>
    /// <returns>A Result containing the new skill or the first validation error.</returns>
    public static Result<Skill> Create(
        string name, string description, string body, IEnumerable<string>? tags,
        string sourcePath, string contentHash, bool isActive, DateTime updatedAt)
    {
        if (!IsValidName(name))
            return Result.Failure<Skill>($"Name must match ^[a-z][a-z0-9_-]{{0,{MaxNameLength - 1}}}$.");

        var fields = ValidateFields(description, body, tags, sourcePath, contentHash);
        if (fields.IsFailure)
            return Result.Failure<Skill>(fields.Error);

        var skill = new Skill
        {
            Id = name,
            Description = description,
            Body = body,
            SourcePath = sourcePath,
            ContentHash = contentHash,
            IsActive = isActive,
            UpdatedAt = updatedAt,
        };
        skill._tags.AddRange(fields.Value);
        return Result.Success(skill);
    }

    /// <summary>
    ///     Replaces the whole document (files are the source of truth, so an upsert is a full replace, not a patch).
    ///     Validation runs before anything is applied, so a failed update leaves the aggregate unchanged.
    /// </summary>
    public Result Update(
        string description, string body, IReadOnlyList<string>? tags,
        string sourcePath, string contentHash, bool isActive, DateTime updatedAt)
    {
        var fields = ValidateFields(description, body, tags, sourcePath, contentHash);
        if (fields.IsFailure)
            return Result.Failure(fields.Error);

        Description = description;
        Body = body;
        _tags.Clear();
        _tags.AddRange(fields.Value);
        SourcePath = sourcePath;
        ContentHash = contentHash;
        IsActive = isActive;
        UpdatedAt = updatedAt;
        return Result.Success();
    }

    /// <summary>Same rule as the Thalos skill name: <c>^[a-z][a-z0-9_-]{0,63}$</c>.</summary>
    private static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name)
        && name.Length <= MaxNameLength
        && char.IsAsciiLetterLower(name[0])
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    private static Result<List<string>> ValidateFields(
        string description, string body, IEnumerable<string>? tags, string sourcePath, string contentHash)
    {
        if (string.IsNullOrWhiteSpace(description))
            return Result.Failure<List<string>>("Description is required.");

        if (description.Length > MaxDescriptionLength)
            return Result.Failure<List<string>>($"Description must be at most {MaxDescriptionLength} characters.");

        if (string.IsNullOrWhiteSpace(body))
            return Result.Failure<List<string>>("Body is required.");

        if (body.Length > MaxBodyLength)
            return Result.Failure<List<string>>($"Body must be at most {MaxBodyLength} characters.");

        if (string.IsNullOrWhiteSpace(sourcePath))
            return Result.Failure<List<string>>("Source path is required.");

        if (sourcePath.Length > MaxSourcePathLength)
            return Result.Failure<List<string>>($"Source path must be at most {MaxSourcePathLength} characters.");

        if (string.IsNullOrWhiteSpace(contentHash))
            return Result.Failure<List<string>>("Content hash is required.");

        return contentHash.Length > MaxContentHashLength
            ? Result.Failure<List<string>>($"Content hash must be at most {MaxContentHashLength} characters.")
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

        if (list.Count > MaxTags)
            return Result.Failure<List<string>>($"At most {MaxTags} tags are allowed.");

        return list.Exists(t => t.Length > MaxTagLength)
            ? Result.Failure<List<string>>($"Tags must be at most {MaxTagLength} characters.")
            : Result.Success(list);
    }
}
