#pragma warning disable CA1054, CA1056 // Repository URL stored as string for EF Core mapping and git CLI consumption

using ZeroAlloc.Results;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Aggregate root representing a project containing multiple tasks to be worked.
/// </summary>
public sealed class Project : AggregateRoot<Guid>
{
    private readonly List<Task> _tasks = [];

    /// <summary>Gets the version of the project structure.</summary>
    public string Version { get; private set; } = "1.0";

    /// <summary>Gets the project name.</summary>
    public string ProjectName { get; private set; } = string.Empty;

    /// <summary>Gets the project description.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Gets the repository URL for git operations.</summary>
    public string RepositoryUrl { get; private set; } = string.Empty;

    /// <summary>Gets the default branch name (e.g. "main").</summary>
    public string DefaultBranch { get; private set; } = "main";

    /// <summary>Gets all tasks in this project.</summary>
    public IReadOnlyList<Task> Tasks => _tasks.AsReadOnly();

    /// <summary>Gets when the project was created.</summary>
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    /// <summary>Gets when the project was last modified.</summary>
    public DateTime? ModifiedAt { get; private set; }

    /// <summary>
    ///     Gets the row's PostgreSQL <c>xmin</c> as it was read, the concurrency token of a guarded save: a save of a
    ///     project another write changed since it was read is refused. Zero for a project not read from the database.
    /// </summary>
    public uint RowVersion { get; private set; }

    private Project()
    {
    }

    /// <summary>
    ///     Rebuilds a stored project, for persistence only: it sets every value as given, validates nothing and raises
    ///     no domain event, so a row <see cref="Create"/> would refuse today still loads.
    /// </summary>
    /// <param name="id">The project's id.</param>
    /// <param name="version">The version of the project structure.</param>
    /// <param name="projectName">The project name.</param>
    /// <param name="description">The description.</param>
    /// <param name="createdAt">When the project was created.</param>
    /// <param name="modifiedAt">When the project was last modified, if ever.</param>
    /// <param name="defaultBranch">The default branch.</param>
    /// <param name="repositoryUrl">The repository URL, empty when none is set.</param>
    /// <param name="rowVersion">The row's <c>xmin</c> as it was read.</param>
    /// <param name="tasks">The project's tasks, empty for a read that loads none.</param>
    /// <returns>The project.</returns>
    public static Project Rehydrate(
        Guid id,
        string version,
        string projectName,
        string description,
        DateTime createdAt,
        DateTime? modifiedAt,
        string defaultBranch,
        string repositoryUrl,
        uint rowVersion,
        IEnumerable<Task> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);

        var project = new Project
        {
            Id = id,
            Version = version,
            ProjectName = projectName,
            Description = description,
            CreatedAt = createdAt,
            ModifiedAt = modifiedAt,
            DefaultBranch = defaultBranch,
            RepositoryUrl = repositoryUrl,
            RowVersion = rowVersion,
        };
        project._tasks.AddRange(tasks);
        return project;
    }

    /// <summary>
    ///     Creates a new project.
    /// </summary>
    public static Result<Project> Create(
        Guid id,
        string projectName,
        string description,
        string version = "1.0",
        string? repositoryUrl = null,
        string? defaultBranch = null,
        DateTime? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return Result<Project>.Failure("Project name cannot be empty");
        }

        if (projectName.Length > 256)
        {
            return Result<Project>.Failure("Project name cannot exceed 256 characters");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return Result<Project>.Failure("Description cannot be empty");
        }

        if (description.Length > 2000)
        {
            return Result<Project>.Failure("Description cannot exceed 2000 characters");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return Result<Project>.Failure("Version cannot be empty");
        }

        if (version.Length > 50)
        {
            return Result<Project>.Failure("Version cannot exceed 50 characters");
        }

        return Result<Project>.Success(new Project
        {
            Id = id,
            ProjectName = projectName.Trim(),
            Description = description.Trim(),
            Version = version.Trim(),
            RepositoryUrl = repositoryUrl?.Trim() ?? string.Empty,
            DefaultBranch = defaultBranch?.Trim() ?? "main",
            CreatedAt = createdAt ?? DateTime.UtcNow
        });
    }

    /// <summary>
    ///     Adds a new task to the project.
    /// </summary>
    public Result AddTask(Task? task)
    {
        if (task is null)
        {
            return Result.Failure("Task cannot be null");
        }

        if (_tasks.Exists(t => t.Id == task.Id))
        {
            return Result.Failure($"Task with ID {task.Id} already exists");
        }

        _tasks.Add(task);
        ModifiedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    ///     Removes a task from the project.
    /// </summary>
    public Result RemoveTask(string taskId)
    {
        var task = _tasks.FirstOrDefault(t => string.Equals(t.TaskId, taskId, StringComparison.Ordinal));
        if (task is null)
        {
            return Result.Failure($"Task with ID {taskId} not found");
        }

        _tasks.Remove(task);
        ModifiedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    ///     Updates the project version.
    /// </summary>
    public Result UpdateVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return Result.Failure("Version cannot be empty");
        }

        if (version.Length > 50)
        {
            return Result.Failure("Version cannot exceed 50 characters");
        }

        Version = version.Trim();
        ModifiedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    ///     Updates the project metadata.
    /// </summary>
    public Result UpdateMetadata(string projectName, string description)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return Result.Failure("Project name cannot be empty");
        }

        if (projectName.Length > 256)
        {
            return Result.Failure("Project name cannot exceed 256 characters");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return Result.Failure("Description cannot be empty");
        }

        if (description.Length > 2000)
        {
            return Result.Failure("Description cannot exceed 2000 characters");
        }

        ProjectName = projectName.Trim();
        Description = description.Trim();
        ModifiedAt = DateTime.UtcNow;
        return Result.Success();
    }

    /// <summary>
    ///     Updates the repository URL and optional default branch.
    /// </summary>
    public Result UpdateRepositoryUrl(string repositoryUrl, string? defaultBranch = null)
    {
        if (repositoryUrl.Length > 2000)
        {
            return Result.Failure("Repository URL cannot exceed 2000 characters");
        }

        RepositoryUrl = repositoryUrl.Trim();

        if (!string.IsNullOrWhiteSpace(defaultBranch))
        {
            if (defaultBranch.Length > 100)
            {
                return Result.Failure("Default branch cannot exceed 100 characters");
            }

            DefaultBranch = defaultBranch.Trim();
        }

        ModifiedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
#pragma warning restore CA1054, CA1056
