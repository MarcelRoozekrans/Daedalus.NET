#pragma warning disable CA1819 // Use byte[] instead of property returning array (EF Core concurrency token standard pattern)
#pragma warning disable S1144 // EF Core sets RowVersion via reflection (unused private setter is required)

using ZeroAlloc.Results;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Represents a task within a project to be executed by the Ralph loop.
///     This is an aggregate root that owns task executions and manages execution lifecycle.
/// </summary>
public sealed class Task : AggregateRoot<Guid>
{
    private readonly List<string> _dependencies = [];
    private readonly List<TaskExecution> _executions = [];
    private readonly List<string> _filesToModify = [];

    /// <summary>Gets the task identifier string (e.g., "TASK-001").</summary>
    public string TaskId { get; private set; } = string.Empty;

    /// <summary>Gets the parent project ID.</summary>
    public Guid ProjectId { get; private set; }

    /// <summary>Gets the task title.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>Gets the task description.</summary>
    public string Description { get; private set; } = string.Empty;

    /// <summary>Gets the priority level.</summary>
    public Priority Priority { get; private set; } = Priority.Medium;

    /// <summary>Gets the current status of the task.</summary>
    public TaskStatus Status { get; private set; } = TaskStatus.Pending;

    /// <summary>Gets the phase/stage of the project (e.g., "Backend", "Frontend").</summary>
    public string Phase { get; private set; } = string.Empty;

    /// <summary>Gets the parallel group for task scheduling.</summary>
    public int ParallelGroup { get; private set; } = 1;

    /// <summary>Gets task IDs this task depends on.</summary>
    public IReadOnlyList<string> Dependencies => _dependencies.AsReadOnly();

    /// <summary>Gets file paths that will be modified by this task.</summary>
    public IReadOnlyList<string> FilesToModify => _filesToModify.AsReadOnly();

    /// <summary>Gets the estimated complexity of the task.</summary>
    public Complexity EstimatedComplexity { get; private set; } = Complexity.Medium;

    /// <summary>Gets the prompt to be repeatedly sent to the LLM.</summary>
    public string Prompt { get; private set; } = string.Empty;

    /// <summary>Gets the completion promise - exact string that signals success.</summary>
    public string CompletionPromise { get; private set; } = string.Empty;

    /// <summary>Gets the maximum iterations before failure.</summary>
    public int MaxIterations { get; private set; }

    /// <summary>Gets the session currently executing this task, if any.</summary>
    public Guid? CurrentSessionId { get; private set; }

    /// <summary>Gets the final result/output from the LLM when completed.</summary>
    public string? Result { get; private set; }

    /// <summary>Gets the number of iterations completed.</summary>
    public int IterationCount { get; private set; }

    /// <summary>Gets when the task was created.</summary>
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    /// <summary>Gets when the task completed or failed.</summary>
    public DateTime? CompletedAt { get; private set; }

    /// <summary>Gets all execution records (iterations attempted).</summary>
    public IReadOnlyList<TaskExecution> Executions => _executions.AsReadOnly();

    /// <summary>Gets accumulated learnings from previous executions of this task.</summary>
    public string Learnings { get; private set; } = string.Empty;

    /// <summary>Gets when learnings were last updated.</summary>
    public DateTime? LearningsUpdatedAt { get; private set; }

    /// <summary>
    ///     Gets the concurrency token (row version) for optimistic locking.
    ///     Prevents lost updates in concurrent scenarios.
    /// </summary>
    public byte[]? RowVersion { get; private set; }

    /// <summary>
    ///     Creates a new task within a project.
    /// </summary>
    public static Result<Task> Create(
        Guid id,
        Guid projectId,
        string taskId,
        string title,
        string description,
        Priority priority,
        string phase,
        int parallelGroup,
        Complexity estimatedComplexity,
        string prompt,
        string completionPromise,
        int maxIterations,
        DateTime? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Task ID cannot be empty");
        }

        if (taskId.Length > 50)
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Task ID cannot exceed 50 characters");
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Title cannot be empty");
        }

        if (title.Length > 500)
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Title cannot exceed 500 characters");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Description cannot be empty");
        }

        if (description.Length > 2000)
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Description cannot exceed 2000 characters");
        }

        if (string.IsNullOrWhiteSpace(phase))
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Phase cannot be empty");
        }

        if (phase.Length > 100)
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Phase cannot exceed 100 characters");
        }

        if (string.IsNullOrWhiteSpace(prompt))
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Prompt cannot be empty");
        }

        if (prompt.Length > 8000)
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Prompt cannot exceed 8000 characters");
        }

        if (string.IsNullOrWhiteSpace(completionPromise))
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Completion promise cannot be empty");
        }

        if (completionPromise.Length > 1000)
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Completion promise cannot exceed 1000 characters");
        }

        if (maxIterations < 1 || maxIterations > 1000)
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Max iterations must be between 1 and 1000");
        }

        if (parallelGroup < 1)
        {
            return ZeroAlloc.Results.Result<Task>.Failure("Parallel group must be at least 1");
        }

        return ZeroAlloc.Results.Result<Task>.Success(new Task
        {
            Id = id,
            ProjectId = projectId,
            TaskId = taskId.Trim(),
            Title = title.Trim(),
            Description = description.Trim(),
            Priority = priority,
            Phase = phase.Trim(),
            ParallelGroup = parallelGroup,
            EstimatedComplexity = estimatedComplexity,
            Prompt = prompt.Trim(),
            CompletionPromise = completionPromise.Trim(),
            MaxIterations = maxIterations,
            CreatedAt = createdAt ?? DateTime.UtcNow
        });
    }

    /// <summary>
    ///     Adds a dependency on another task.
    /// </summary>
    public Result AddDependency(string dependsOnTaskId)
    {
        if (string.IsNullOrWhiteSpace(dependsOnTaskId))
        {
            return ZeroAlloc.Results.Result.Failure("Dependency task ID cannot be empty");
        }

        if (string.Equals(dependsOnTaskId, TaskId, StringComparison.Ordinal))
        {
            return ZeroAlloc.Results.Result.Failure("A task cannot depend on itself");
        }

        if (_dependencies.Contains(dependsOnTaskId, StringComparer.Ordinal))
        {
            return ZeroAlloc.Results.Result.Failure($"Task already depends on {dependsOnTaskId}");
        }

        _dependencies.Add(dependsOnTaskId);
        return ZeroAlloc.Results.Result.Success();
    }

    /// <summary>
    ///     Removes a dependency.
    /// </summary>
    public Result RemoveDependency(string dependsOnTaskId)
    {
        if (string.IsNullOrWhiteSpace(dependsOnTaskId))
        {
            return ZeroAlloc.Results.Result.Failure("Dependency task ID cannot be empty");
        }

        if (!_dependencies.Contains(dependsOnTaskId, StringComparer.Ordinal))
        {
            return ZeroAlloc.Results.Result.Failure($"Task does not depend on {dependsOnTaskId}");
        }

        _dependencies.Remove(dependsOnTaskId);
        return ZeroAlloc.Results.Result.Success();
    }

    /// <summary>
    ///     Adds a file to be modified by this task.
    /// </summary>
    public Result AddFileToModify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return ZeroAlloc.Results.Result.Failure("File path cannot be empty");
        }

        if (_filesToModify.Contains(filePath, StringComparer.Ordinal))
        {
            return ZeroAlloc.Results.Result.Failure($"File {filePath} already added");
        }

        _filesToModify.Add(filePath.Trim());
        return ZeroAlloc.Results.Result.Success();
    }

    /// <summary>
    ///     Removes a file from the modification list.
    /// </summary>
    public Result RemoveFileToModify(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return ZeroAlloc.Results.Result.Failure("File path cannot be empty");
        }

        if (!_filesToModify.Contains(filePath, StringComparer.Ordinal))
        {
            return ZeroAlloc.Results.Result.Failure($"File {filePath} not found");
        }

        _filesToModify.Remove(filePath);
        return ZeroAlloc.Results.Result.Success();
    }

    /// <summary>
    ///     Updates task metadata (title, description, priority, etc).
    /// </summary>
    public Result UpdateMetadata(
        string title,
        string description,
        Priority priority,
        string phase,
        Complexity complexity)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return ZeroAlloc.Results.Result.Failure("Title cannot be empty");
        }

        if (title.Length > 500)
        {
            return ZeroAlloc.Results.Result.Failure("Title cannot exceed 500 characters");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return ZeroAlloc.Results.Result.Failure("Description cannot be empty");
        }

        if (description.Length > 2000)
        {
            return ZeroAlloc.Results.Result.Failure("Description cannot exceed 2000 characters");
        }

        if (string.IsNullOrWhiteSpace(phase))
        {
            return ZeroAlloc.Results.Result.Failure("Phase cannot be empty");
        }

        if (phase.Length > 100)
        {
            return ZeroAlloc.Results.Result.Failure("Phase cannot exceed 100 characters");
        }

        Title = title.Trim();
        Description = description.Trim();
        Priority = priority;
        Phase = phase.Trim();
        EstimatedComplexity = complexity;
        return ZeroAlloc.Results.Result.Success();
    }

    /// <summary>
    ///     Updates execution configuration (prompt, completion promise, max iterations, parallel group).
    /// </summary>
    public Result UpdateExecutionConfig(
        int? parallelGroup = null,
        string? prompt = null,
        string? completionPromise = null,
        int? maxIterations = null)
    {
        if (parallelGroup.HasValue)
        {
            if (parallelGroup.Value < 1)
            {
                return ZeroAlloc.Results.Result.Failure("Parallel group must be at least 1");
            }

            ParallelGroup = parallelGroup.Value;
        }

        if (prompt is not null)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return ZeroAlloc.Results.Result.Failure("Prompt cannot be empty");
            }

            if (prompt.Length > 8000)
            {
                return ZeroAlloc.Results.Result.Failure("Prompt cannot exceed 8000 characters");
            }

            Prompt = prompt.Trim();
        }

        if (completionPromise is not null)
        {
            if (string.IsNullOrWhiteSpace(completionPromise))
            {
                return ZeroAlloc.Results.Result.Failure("Completion promise cannot be empty");
            }

            if (completionPromise.Length > 1000)
            {
                return ZeroAlloc.Results.Result.Failure("Completion promise cannot exceed 1000 characters");
            }

            CompletionPromise = completionPromise.Trim();
        }

        if (maxIterations.HasValue)
        {
            if (maxIterations.Value < 1 || maxIterations.Value > 1000)
            {
                return ZeroAlloc.Results.Result.Failure("Max iterations must be between 1 and 1000");
            }

            MaxIterations = maxIterations.Value;
        }

        return ZeroAlloc.Results.Result.Success();
    }

    /// <summary>
    ///     Claims this task for execution by the given session.
    /// </summary>
    public Result Claim(Guid sessionId)
    {
        if (Status != TaskStatus.Pending)
        {
            return ZeroAlloc.Results.Result.Failure($"Cannot claim task with status {Status}");
        }

        CurrentSessionId = sessionId;
        Status = TaskStatus.InProgress;
        return ZeroAlloc.Results.Result.Success();
    }

    public Result RecordExecution(TaskExecution execution, DateTime? completedAt = null)
    {
        if (Status != TaskStatus.InProgress)
        {
            return ZeroAlloc.Results.Result.Failure("Task is not in progress");
        }

        if (execution.IterationNumber != IterationCount + 1)
        {
            return ZeroAlloc.Results.Result.Failure("Execution iteration number is out of sequence");
        }

        _executions.Add(execution);
        IterationCount++;

        if (execution.CompletionPromiseFound)
        {
            Status = TaskStatus.Completed;
            Result = execution.LlmResponse;
            CompletedAt = completedAt ?? DateTime.UtcNow;
        }
        else if (IterationCount >= MaxIterations)
        {
            Status = TaskStatus.Failed;
            CompletedAt = completedAt ?? DateTime.UtcNow;
        }

        return ZeroAlloc.Results.Result.Success();
    }

    /// <summary>
    ///     Marks task as abandoned (session crashed).
    /// </summary>
    public Result Abandon()
    {
        if (Status != TaskStatus.InProgress)
        {
            return ZeroAlloc.Results.Result.Failure("Only in-progress tasks can be abandoned");
        }

        Status = TaskStatus.Abandoned;
        CurrentSessionId = null;
        return ZeroAlloc.Results.Result.Success();
    }

    /// <summary>
    ///     Resumes abandoned task by resetting to Pending state.
    ///     The newSessionId parameter is ignored; the task is left unclaimed for any session to claim.
    /// </summary>
    public Result Resume(Guid newSessionId)
    {
        if (Status != TaskStatus.Abandoned)
        {
            return ZeroAlloc.Results.Result.Failure("Only abandoned tasks can be resumed");
        }

        // Reset to Pending so it can be executed
        Status = TaskStatus.Pending;

        // Always leave unclaimed - any available session can claim it
        CurrentSessionId = null;

        return ZeroAlloc.Results.Result.Success();
    }

    /// <summary>
    ///     Updates the accumulated learnings from execution history.
    ///     Learnings persist across sessions to improve future iterations.
    /// </summary>
    public Result UpdateLearnings(string newLearnings, DateTime? learningsUpdatedAt = null)
    {
        if (string.IsNullOrWhiteSpace(newLearnings))
        {
            return ZeroAlloc.Results.Result.Failure("Learnings cannot be empty");
        }

        Learnings = newLearnings.Trim();
        LearningsUpdatedAt = learningsUpdatedAt ?? DateTime.UtcNow;
        return ZeroAlloc.Results.Result.Success();
    }
}
#pragma warning restore S1144
#pragma warning restore CA1819
