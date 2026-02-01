using System.Diagnostics.CodeAnalysis;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Helpers;
using Task = Daedalus.Domain.Entities.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Integration.Builders;

/// <summary>
///     Fluent builder for creating test Task entities.
///     Provides a clean, readable way to set up test data.
/// </summary>
/// <example>
///     var task = new TaskTestBuilder()
///     .WithPrompt("Find the answer")
///     .WithCompletionPromise("Answer: 42")
///     .WithMaxIterations(10)
///     .Build();
/// </example>
[SuppressMessage("CodeQuality", "S2933:Fields that are only set in the constructor should be \"readonly\"")]
public sealed class TaskTestBuilder
{
    private readonly int _parallelGroup = 1;
    private readonly string _phase = "Test";
    private Guid _projectId = Guid.NewGuid();
    private readonly string _taskId = $"TASK-{Guid.NewGuid():N}".Substring(0, 10);
    private string _completionPromise = "DONE";
    private Complexity _complexity = Complexity.Medium;
    private Guid? _currentSessionId;
    private string _description = "A test task";
    private Guid _id = Guid.NewGuid();
    private int _maxIterations = 10;
    private Priority _priority = Priority.Medium;
    private string _prompt = "Default test prompt";
    private TaskStatus _status = TaskStatus.Pending;
    private string _title = "Test task";

    /// <summary>
    ///     Sets the task ID.
    /// </summary>
    public TaskTestBuilder WithId(Guid id)
    {
        _id = id;
        return this;
    }

    /// <summary>
    ///     Sets the parent project ID.
    /// </summary>
    public TaskTestBuilder WithProjectId(Guid projectId)
    {
        _projectId = projectId;
        return this;
    }

    /// <summary>
    ///     Sets the prompt for the task.
    /// </summary>
    public TaskTestBuilder WithPrompt(string prompt)
    {
        _prompt = prompt;
        return this;
    }

    /// <summary>
    ///     Sets the completion promise/expectation.
    /// </summary>
    public TaskTestBuilder WithCompletionPromise(string promise)
    {
        _completionPromise = promise;
        return this;
    }

    /// <summary>
    ///     Sets the maximum number of iterations.
    /// </summary>
    public TaskTestBuilder WithMaxIterations(int maxIterations)
    {
        _maxIterations = maxIterations;
        return this;
    }

    /// <summary>
    ///     Sets the task status.
    /// </summary>
    public TaskTestBuilder WithStatus(TaskStatus status)
    {
        _status = status;
        return this;
    }

    /// <summary>
    ///     Marks the task as claimed by a session (sets status to InProgress).
    /// </summary>
    public TaskTestBuilder ClaimedBy(Guid sessionId)
    {
        _currentSessionId = sessionId;
        _status = TaskStatus.InProgress;
        return this;
    }

    /// <summary>
    ///     Marks the task as abandoned by a session.
    /// </summary>
    public TaskTestBuilder AsAbandoned()
    {
        _status = TaskStatus.Abandoned;
        _currentSessionId = Guid.NewGuid(); // Create temporary session for abandoned state
        return this;
    }

    /// <summary>
    ///     Sets the title for the task.
    /// </summary>
    public TaskTestBuilder WithTitle(string title)
    {
        _title = title;
        return this;
    }

    /// <summary>
    ///     Sets the description for the task.
    /// </summary>
    public TaskTestBuilder WithDescription(string description)
    {
        _description = description;
        return this;
    }

    /// <summary>
    ///     Sets the priority for the task.
    /// </summary>
    public TaskTestBuilder WithPriority(Priority priority)
    {
        _priority = priority;
        return this;
    }

    /// <summary>
    ///     Sets the complexity for the task.
    /// </summary>
    public TaskTestBuilder WithComplexity(Complexity complexity)
    {
        _complexity = complexity;
        return this;
    }

    /// <summary>
    ///     Builds the Task entity with configured values.
    /// </summary>
    /// <returns>A fully configured Task entity</returns>
    /// <exception cref="InvalidOperationException">If task creation fails</exception>
    public Task Build()
    {
        var result = Task.Create(
            _id,
            _projectId,
            _taskId,
            _title,
            _description,
            _priority,
            _phase,
            _parallelGroup,
            _complexity,
            _prompt,
            _completionPromise,
            _maxIterations);

        var task = result.MustSucceed("Failed to build task for testing");

        // Apply status if not default (Pending)
        if (_status == TaskStatus.InProgress)
        {
            if (_currentSessionId.HasValue)
            {
                task.Claim(_currentSessionId.Value).EnsureSuccess("Failed to claim task during build");
            }
        }
        else if (_status == TaskStatus.Abandoned)
        {
            if (_currentSessionId.HasValue)
            {
                task.Claim(_currentSessionId.Value).EnsureSuccess("Failed to claim task during build");
            }

            task.Abandon().EnsureSuccess("Failed to abandon task during build");
        }
        // Note: Completed/Failed states require execution history, handled separately in tests

        return task;
    }
}
