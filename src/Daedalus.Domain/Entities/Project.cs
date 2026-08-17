#pragma warning disable CA1819 // Use byte[] instead of property returning array (EF Core concurrency token standard pattern)
#pragma warning disable S1144 // EF Core sets RowVersion via reflection (unused private setter is required)
#pragma warning disable CA1054, CA1056 // Repository URL stored as string for EF Core mapping and git CLI consumption

using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Aggregate root representing a project containing multiple tasks to be executed by the Ralph loop.
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
    ///     Gets the concurrency token (row version) for optimistic locking.
    ///     Prevents lost updates when multiple operations modify project metadata.
    /// </summary>
    public byte[]? RowVersion { get; private set; }

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
            return Result.Failure<Project>("Project name cannot be empty");
        }

        if (projectName.Length > 256)
        {
            return Result.Failure<Project>("Project name cannot exceed 256 characters");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return Result.Failure<Project>("Description cannot be empty");
        }

        if (description.Length > 2000)
        {
            return Result.Failure<Project>("Description cannot exceed 2000 characters");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            return Result.Failure<Project>("Version cannot be empty");
        }

        if (version.Length > 50)
        {
            return Result.Failure<Project>("Version cannot exceed 50 characters");
        }

        return Result.Success(new Project
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
#pragma warning restore S1144
#pragma warning restore CA1819
#pragma warning restore CA1054, CA1056
