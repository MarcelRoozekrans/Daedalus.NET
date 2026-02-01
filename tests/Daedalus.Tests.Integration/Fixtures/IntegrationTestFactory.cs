using Daedalus.Domain.Entities;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Tests.Integration.Fixtures;

/// <summary>
///     Test factory for creating domain entities in integration tests.
/// </summary>
public static class IntegrationTestFactory
{
    private static readonly Guid _defaultProjectId = Guid.CreateVersion7();

    /// <summary>
    ///     Creates a Project with minimal parameters for testing.
    /// </summary>
    public static Project CreateProject(
        Guid? id = null,
        string name = "Test Project",
        string description = "Test project for integration tests")
    {
        return Project.Create(
            id ?? Guid.NewGuid(),
            name,
            description
        ).Value;
    }

    /// <summary>
    ///     Gets the default project ID used in test factories.
    /// </summary>
    public static Guid GetDefaultProjectId() => _defaultProjectId;

    /// <summary>
    ///     Creates a RepositoryConfiguration with minimal parameters for testing.
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
    ///     Creates a Task with minimal parameters for testing.
    /// </summary>
    public static Task CreateTask(
        Guid? id = null,
        Guid? projectId = null,
        string? taskId = null,
        string prompt = "Test prompt",
        string completionPromise = "DONE",
        int maxIterations = 10)
    {
        return Task.Create(
            id ?? Guid.NewGuid(),
            projectId ?? _defaultProjectId,
            taskId ?? "TASK-001",
            "Test Task",
            "Test description",
            Priority.Medium,
            "Testing",
            1,
            Complexity.Medium,
            prompt,
            completionPromise,
            maxIterations
        ).Value;
    }
}
