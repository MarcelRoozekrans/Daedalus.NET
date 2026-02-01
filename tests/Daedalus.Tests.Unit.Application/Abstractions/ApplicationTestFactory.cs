using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Application.Abstractions;

/// <summary>
///     Test factory for creating domain entities with sensible defaults for Application tests.
/// </summary>
public static class ApplicationTestFactory
{
    private static readonly Guid _defaultProjectId = Guid.CreateVersion7();

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
        string completionPromise = "DONE",
        int maxIterations = 10,
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
            prompt,
            completionPromise,
            maxIterations
        ).Value;
    }
}
