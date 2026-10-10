using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Application.Abstractions;

/// <summary>
///     Test factory for creating domain entities with sensible defaults for Application tests.
/// </summary>
public static class ApplicationTestFactory
{
    private static readonly Guid _defaultProjectId = Guid.CreateVersion7();

    /// <summary>
    ///     Creates a Project with minimal parameters, using defaults for others.
    /// </summary>
    public static Project CreateProject(
        Guid? id = null,
        string projectName = "Test Project",
        string description = "Test project description",
        string version = "1.0")
    {
        return Project.Create(
            id ?? Guid.NewGuid(),
            projectName,
            description,
            version
        ).Value;
    }

    /// <summary>
    ///     Creates a Task with minimal parameters, using defaults for others.
    /// </summary>
    public static DomainTask CreateTask(
        Guid? id = null,
        Guid? projectId = null,
        string? taskId = null,
        string? title = null,
        string? description = null,
        Priority priority = Priority.Medium,
        string? phase = null,
        string prompt = "Test prompt",
        Complexity complexity = Complexity.Medium,
        int parallelGroup = 1)
    {
        return DomainTask.Create(
            id ?? Guid.NewGuid(),
            projectId ?? _defaultProjectId,
            taskId ?? "TASK-001",
            title ?? "Test Task",
            description ?? "Test description",
            priority,
            phase ?? "Testing",
            parallelGroup,
            complexity,
            prompt
        ).Value;
    }

    /// <summary>
    ///     Creates a task that carries the loop settings of an old Ralph task. Phase 2.8 removed them from
    ///     <c>Task.Create</c>, so a test of the retired pipeline sets them on the private setters, as EF does when it
    ///     reads an old row. Task 13 deletes the pipeline tests that need this.
    /// </summary>
    public static DomainTask CreateRalphLoopTask(
        Guid? id = null,
        string prompt = "Test prompt",
        string completionPromise = "DONE",
        int maxIterations = 10)
    {
        var task = CreateTask(id: id, prompt: prompt);
        typeof(DomainTask).GetProperty(nameof(DomainTask.CompletionPromise))!.SetValue(task, completionPromise);
        typeof(DomainTask).GetProperty(nameof(DomainTask.MaxIterations))!.SetValue(task, maxIterations);
        return task;
    }
}
