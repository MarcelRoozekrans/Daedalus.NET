namespace Daedalus.Web.Services;

/// <summary>
///     The values the create and edit dialogs bind, in the contract's field order. Priority and complexity default to
///     Medium. A blank prompt means "use the description".
/// </summary>
public sealed class TaskForm
{
    /// <summary>The Medium value of both the priority and the complexity scales.</summary>
    public const int Medium = 1;

    public Guid? ProjectId { get; set; }

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public int Priority { get; set; } = Medium;

    public int EstimatedComplexity { get; set; } = Medium;

    public string Prompt { get; set; } = "";

    /// <summary>The prompt to send, or null for a blank one.</summary>
    public string? PromptOrNull => string.IsNullOrWhiteSpace(Prompt) ? null : Prompt;

    /// <summary>The project to preselect: the only one when there is exactly one, else none.</summary>
    public static Guid? Preselect(IReadOnlyList<ProjectDto> projects)
    {
        ArgumentNullException.ThrowIfNull(projects);
        return projects.Count == 1 ? projects[0].Id : null;
    }

    /// <summary>The form holding <paramref name="task"/>'s current values.</summary>
    public static TaskForm From(TaskDto task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new TaskForm
        {
            ProjectId = task.ProjectId,
            Title = task.Title,
            Description = task.Description,
            Priority = task.Priority,
            EstimatedComplexity = task.EstimatedComplexity,
            Prompt = task.Prompt,
        };
    }

    /// <summary>The create request: no phase, parallel group, dependencies or files, so the API's defaults apply.</summary>
    public CreateTaskDto ToCreate() =>
        new(ProjectId ?? Guid.Empty, null, Title.Trim(), Description.Trim(), Priority, null, null, EstimatedComplexity,
            PromptOrNull?.Trim(), null, null);

    /// <summary>
    ///     The update request for <paramref name="task"/>. Phase and parallel group are left as they are. A blank prompt is
    ///     replaced by the description, as a create does.
    /// </summary>
    public UpdateTaskDto ToUpdate(TaskDto task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new UpdateTaskDto(
            Title.Trim(),
            Description.Trim(),
            Priority != task.Priority ? Priority : null,
            null,
            null,
            EstimatedComplexity != task.EstimatedComplexity ? EstimatedComplexity : null,
            (PromptOrNull ?? Description).Trim());
    }
}
