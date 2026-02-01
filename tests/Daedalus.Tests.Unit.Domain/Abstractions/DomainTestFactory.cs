#pragma warning disable CA1054 // URI parameters should not be strings

using Daedalus.Domain.Entities;

namespace Daedalus.Tests.Unit.Domain.Abstractions;

/// <summary>
///     Test factory for creating domain entities with sensible defaults for testing.
/// </summary>
public static class DomainTestFactory
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
    ///     Creates a RepositoryConfiguration with minimal parameters, using defaults for others.
    /// </summary>
    public static RepositoryConfiguration CreateRepositoryConfiguration(
        Guid? id = null,
        string name = "Test Repository",
        string url = "https://github.com/org/repo",
        string platform = "GitHub",
        string defaultBranch = "main",
        string? authenticationMethod = null,
        string? credentialIdentifier = null,
        string? description = null)
    {
        return RepositoryConfiguration.Create(
            id ?? Guid.NewGuid(),
            name,
            url,
            platform,
            defaultBranch,
            authenticationMethod,
            credentialIdentifier,
            description
        ).Value;
    }

    /// <summary>
    ///     Creates a TaskExecution for testing.
    /// </summary>
    public static TaskExecution CreateTaskExecution(
        Guid taskId,
        Guid sessionId,
        int iterationNumber = 1,
        string llmResponse = "Response",
        bool completionPromiseFound = false)
    {
        return new TaskExecution
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            SessionId = sessionId,
            IterationNumber = iterationNumber,
            Prompt = "Test prompt",
            LlmResponse = llmResponse,
            CompletionPromiseFound = completionPromiseFound,
            ExecutionDuration = TimeSpan.FromMilliseconds(100)
        };
    }
}
