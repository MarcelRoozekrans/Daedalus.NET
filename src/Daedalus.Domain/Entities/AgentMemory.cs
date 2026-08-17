using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     One curated agent memory (Thalos <c>MemoryRecord</c> persisted by Daedalus). Domain stays framework-free: ids are
///     GUIDs (Thalos typed ids are Guid-backed; the adapter converts), the kind is its wire string, times are UTC.
///     Vectors are not stored here — the Rag.NET index is a rebuildable cache; <see cref="IndexPending"/> marks rows
///     whose vector still has to be (re)built.
/// </summary>
/// <remarks>
///     Limits mirror Thalos <c>MemoryRules</c> (owner ≤ 256, kind <c>^[a-z][a-z0-9_-]{0,31}$</c>, text ≤ 4000, ≤ 10 tags of
///     ≤ 32 chars, source ≤ 256, importance 0..1), so a violation is a validation error, never a database constraint failure.
///     Text and kind are stored verbatim (multi-line texts at the limit must round-trip; the memory service validates the
///     kind upstream); tags are normalised (trimmed, lower-cased, blanks dropped, de-duplicated) like Thalos does.
/// </remarks>
public sealed class AgentMemory : Entity<Guid>
{
    /// <summary>Maximum length of <see cref="OwnerId"/>.</summary>
    public const int MaxOwnerIdLength = 256;

    /// <summary>Maximum length of <see cref="Kind"/> (a lowercase identifier: <c>^[a-z][a-z0-9_-]{0,31}$</c>).</summary>
    public const int MaxKindLength = 32;

    /// <summary>Maximum length of <see cref="Text"/>.</summary>
    public const int MaxTextLength = 4000;

    /// <summary>Maximum number of <see cref="Tags"/>.</summary>
    public const int MaxTags = 10;

    /// <summary>Maximum length of one tag.</summary>
    public const int MaxTagLength = 32;

    /// <summary>Maximum length of <see cref="Source"/>.</summary>
    public const int MaxSourceLength = 256;

    private readonly List<string> _tags = [];

    /// <summary>Gets the owner (security-context subject, or the host's shared owner).</summary>
    public string OwnerId { get; private set; } = string.Empty;

    /// <summary>Gets the agent the memory is pinned to; <see langword="null"/> = visible to every agent of the owner.</summary>
    public Guid? AgentId { get; private set; }

    /// <summary>Gets the memory kind (Thalos <c>MemoryKind</c> wire value, a lowercase identifier).</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>Gets the memory text (verbatim, at most <see cref="MaxTextLength"/> chars).</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>Gets the normalised tags (lower-case, distinct, insertion order).</summary>
    public IReadOnlyList<string> Tags => _tags.AsReadOnly();

    /// <summary>Gets the provenance, e.g. <c>tool:memory__remember</c> or <c>ralph:task/&lt;id&gt;</c>.</summary>
    public string Source { get; private set; } = string.Empty;

    /// <summary>Gets the importance in 0..1.</summary>
    public double Importance { get; private set; }

    /// <summary>Gets when the memory was created (UTC).</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Gets when the content was last changed (UTC); index bookkeeping and recalls do not bump it.</summary>
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Gets when the memory was last recalled into a turn (UTC), or <see langword="null"/>.</summary>
    public DateTime? LastRecalledAt { get; private set; }

    /// <summary>Gets how many times the memory was recalled.</summary>
    public int RecallCount { get; private set; }

    /// <summary>Gets a value indicating whether the memory is soft-deleted.</summary>
    public bool IsArchived { get; private set; }

    /// <summary>Gets a value indicating whether the vector still has to be (re)built in the index.</summary>
    public bool IndexPending { get; private set; }

    private AgentMemory() { } // EF Core

    /// <summary>
    ///     Creates a memory; all timestamps are supplied by the caller (UTC). The optional trailing parameters let the store
    ///     insert a Thalos record "as given" (an import may carry its own <c>UpdatedAt</c>, recall bookkeeping or archived flag);
    ///     a freshly remembered memory leaves them at their defaults.
    /// </summary>
    /// <param name="id">The memory id (supplied by the adapter; Thalos typed ids are Guid-backed).</param>
    /// <param name="ownerId">The owner subject.</param>
    /// <param name="agentId">The pinned agent, or <see langword="null"/> for owner-wide.</param>
    /// <param name="kind">The kind identifier (<c>^[a-z][a-z0-9_-]{0,31}$</c>, stored as given).</param>
    /// <param name="text">The memory text (stored verbatim).</param>
    /// <param name="tags">Tags (normalised).</param>
    /// <param name="source">Provenance.</param>
    /// <param name="importance">Importance in 0..1.</param>
    /// <param name="utcNow">Creation timestamp (UTC).</param>
    /// <param name="indexPending">Whether the vector still has to be built.</param>
    /// <param name="updatedAt">Last content change (UTC); <see langword="null"/> = <paramref name="utcNow"/>. Must not precede creation.</param>
    /// <param name="isArchived">Whether the memory starts soft-deleted.</param>
    /// <param name="recallCount">Recalls so far (zero or more).</param>
    /// <param name="lastRecalledAt">Last recall (UTC), or <see langword="null"/>.</param>
    /// <returns>A Result containing the new memory or a validation error.</returns>
    public static Result<AgentMemory> Create(
        Guid id, string ownerId, Guid? agentId, string kind, string text, IEnumerable<string>? tags, string? source,
        double importance, DateTime utcNow, bool indexPending,
        DateTime? updatedAt = null, bool isArchived = false, int recallCount = 0, DateTime? lastRecalledAt = null)
    {
        if (id == Guid.Empty)
            return Result.Failure<AgentMemory>("Memory id is required.");

        if (string.IsNullOrWhiteSpace(ownerId))
            return Result.Failure<AgentMemory>("Owner id is required.");

        if (ownerId.Length > MaxOwnerIdLength)
            return Result.Failure<AgentMemory>($"Owner id must be at most {MaxOwnerIdLength} characters.");

        if (!IsValidKind(kind))
            return Result.Failure<AgentMemory>("Kind must match ^[a-z][a-z0-9_-]{0,31}$.");

        var textCheck = ValidateText(text);
        if (textCheck.IsFailure)
            return Result.Failure<AgentMemory>(textCheck.Error);

        var normalisedTags = NormaliseTags(tags);
        if (normalisedTags.IsFailure)
            return Result.Failure<AgentMemory>(normalisedTags.Error);

        var normalisedSource = source ?? string.Empty;
        if (normalisedSource.Length > MaxSourceLength)
            return Result.Failure<AgentMemory>($"Source must be at most {MaxSourceLength} characters.");

        var importanceCheck = ValidateImportance(importance);
        if (importanceCheck.IsFailure)
            return Result.Failure<AgentMemory>(importanceCheck.Error);

        if (updatedAt is { } u && u < utcNow)
            return Result.Failure<AgentMemory>("UpdatedAt must not precede CreatedAt.");

        if (recallCount < 0)
            return Result.Failure<AgentMemory>("Recall count must not be negative.");

        var memory = new AgentMemory
        {
            Id = id,
            OwnerId = ownerId,
            AgentId = agentId,
            Kind = kind,
            Text = text,
            Source = normalisedSource,
            Importance = importance,
            CreatedAt = utcNow,
            UpdatedAt = updatedAt ?? utcNow,
            LastRecalledAt = lastRecalledAt,
            RecallCount = recallCount,
            IsArchived = isArchived,
            IndexPending = indexPending,
        };
        memory._tags.AddRange(normalisedTags.Value);
        return Result.Success(memory);
    }

    /// <summary>
    ///     Applies a partial update; <see langword="null"/> leaves a field untouched. Bumps <see cref="UpdatedAt"/> only for
    ///     content changes (text, tags, importance, archived flag) — <paramref name="indexPending"/> alone is bookkeeping.
    ///     Validation runs before anything is applied, so a failed update leaves the aggregate unchanged.
    /// </summary>
    /// <param name="text">New text, or <see langword="null"/>.</param>
    /// <param name="tags">New tags (an empty list clears them), or <see langword="null"/>.</param>
    /// <param name="importance">New importance in 0..1, or <see langword="null"/>.</param>
    /// <param name="isArchived">Archive/restore, or <see langword="null"/>.</param>
    /// <param name="indexPending">Set/clear the index-pending flag, or <see langword="null"/>.</param>
    /// <param name="utcNow">The update timestamp (UTC), used only when content changes.</param>
    /// <returns>Success, or the first validation error.</returns>
    public Result Update(string? text, IReadOnlyList<string>? tags, double? importance, bool? isArchived, bool? indexPending, DateTime utcNow)
    {
        if (text is not null)
        {
            var textCheck = ValidateText(text);
            if (textCheck.IsFailure)
                return textCheck;
        }

        List<string>? newTags = null;
        if (tags is not null)
        {
            var tagsCheck = NormaliseTags(tags);
            if (tagsCheck.IsFailure)
                return Result.Failure(tagsCheck.Error);

            newTags = tagsCheck.Value;
        }

        if (importance is { } value)
        {
            var importanceCheck = ValidateImportance(value);
            if (importanceCheck.IsFailure)
                return importanceCheck;
        }

        if (text is not null)
            Text = text;

        if (newTags is not null)
        {
            _tags.Clear();
            _tags.AddRange(newTags);
        }

        if (importance is { } i)
            Importance = i;

        if (isArchived is { } a)
            IsArchived = a;

        if (indexPending is { } p)
            IndexPending = p;

        if (text is not null || tags is not null || importance is not null || isArchived is not null)
            UpdatedAt = utcNow;

        return Result.Success();
    }

    private static Result ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failure("Text is required.");

        return text.Length > MaxTextLength
            ? Result.Failure($"Text must be at most {MaxTextLength} characters.")
            : Result.Success();
    }

    /// <summary>Same rule as Thalos <c>MemoryKind.IsValid</c>: <c>^[a-z][a-z0-9_-]{0,31}$</c>.</summary>
    private static bool IsValidKind(string? kind) =>
        !string.IsNullOrEmpty(kind)
        && kind.Length <= MaxKindLength
        && char.IsAsciiLetterLower(kind[0])
        && kind.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '_' or '-');

    private static Result ValidateImportance(double importance) =>
        double.IsNaN(importance) || importance is < 0 or > 1
            ? Result.Failure("Importance must be between 0 and 1.")
            : Result.Success();

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

        if (list.Exists(t => t.Length > MaxTagLength))
            return Result.Failure<List<string>>($"Tags must be at most {MaxTagLength} characters.");

        return Result.Success(list);
    }
}
