using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents a single composable section of a Ralph loop prompt.
///     Sections are ordered by priority (lower = higher priority) following
///     the RLP numbering convention (0a, 1, 2, 999, 9999, etc.).
///     Immutable value object.
/// </summary>
public readonly record struct PromptSection
{
    private PromptSection(int priority, string priorityLabel, string content, PromptSectionCategory category,
        bool isEnabled)
    {
        Priority = priority;
        PriorityLabel = priorityLabel;
        Content = content;
        Category = category;
        IsEnabled = isEnabled;
    }

    /// <summary>Priority ordering. Lower values appear first in the prompt.</summary>
    public int Priority { get; }

    /// <summary>The priority label as displayed in the prompt (e.g., "0a", "1", "999").</summary>
    public string PriorityLabel { get; }

    /// <summary>The content/instruction of this section.</summary>
    public string Content { get; }

    /// <summary>Category for grouping and filtering.</summary>
    public PromptSectionCategory Category { get; }

    /// <summary>Whether this section is enabled (can be disabled for specific contexts).</summary>
    public bool IsEnabled { get; }

    /// <summary>
    ///     Creates a new prompt section with validation.
    /// </summary>
    public static Result<PromptSection> Create(
        int priority,
        string priorityLabel,
        string content,
        PromptSectionCategory category,
        bool isEnabled = true)
    {
        if (priority < 0)
        {
            return Result.Failure<PromptSection>("Priority must be non-negative");
        }

        if (string.IsNullOrWhiteSpace(priorityLabel))
        {
            return Result.Failure<PromptSection>("Priority label cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Result.Failure<PromptSection>("Content cannot be empty");
        }

        return Result.Success(new PromptSection(priority, priorityLabel.Trim(), content.Trim(), category, isEnabled));
    }

    /// <summary>
    ///     Creates a disabled copy of this section.
    /// </summary>
    public PromptSection Disable() => new(Priority, PriorityLabel, Content, Category, false);

    /// <summary>
    ///     Creates an enabled copy of this section.
    /// </summary>
    public PromptSection Enable() => new(Priority, PriorityLabel, Content, Category, true);
}
