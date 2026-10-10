using System.Diagnostics.CodeAnalysis;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Helpers;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Tests.Integration.Builders;

/// <summary>
///     Fluent builder for creating test Task entities.
///     Provides a clean, readable way to set up test data.
/// </summary>
/// <example>
///     var task = new TaskTestBuilder()
///     .WithPrompt("Find the answer")
///     .Build();
/// </example>
[SuppressMessage("CodeQuality", "S2933:Fields that are only set in the constructor should be \"readonly\"")]
public sealed class TaskTestBuilder
{
    private readonly int _parallelGroup = 1;
    private readonly string _phase = "Test";
    private Guid _projectId = Guid.NewGuid();
    private readonly string _taskId = $"TASK-{Guid.NewGuid():N}".Substring(0, 10);
    private Complexity _complexity = Complexity.Medium;
    private string _description = "A test task";
    private Guid _id = Guid.NewGuid();
    private Priority _priority = Priority.Medium;
    private string _prompt = "Default test prompt";
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
            _prompt);

        return result.MustSucceed("Failed to build task for testing");
    }
}
