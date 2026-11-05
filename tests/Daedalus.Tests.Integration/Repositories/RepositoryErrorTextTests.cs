using System.Data.Common;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging;
using Npgsql;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     A repository's failure text is shown to callers, by <c>POST /api/tasks/{id}/manufacture</c> among others, so it must
///     never carry an exception's message: the exception is logged with the entity id, and the text is generic. Every
///     connection open here throws a <see cref="DbException"/> whose message is a recognisable secret.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RepositoryErrorTextTests(PostgresFixture fixture)
{
    private const string Secret = "Password=hunter2";

    private readonly CapturingLogger<TaskRepository> _taskLog = new();
    private readonly CapturingLogger<ProjectRepository> _projectLog = new();

    public static TheoryData<string> Operations => new()
    {
        "task.GetById", "task.GetByProjectId", "task.Add", "task.Update", "task.Delete",
        "project.GetById", "project.Add", "project.Update", "project.Delete",
    };

    /// <summary>
    ///     Red, run for <c>task.GetById</c>: put <c>{ex.Message}</c> back into its failure text; the text carries the secret.
    ///     Red, run for <c>task.Add</c>: put the exception's message back; the text carries the secret.
    ///     Red, run for <c>project.GetById</c>: put <c>{ex.Message}</c> back; the text carries the secret.
    ///     Red, run for <c>task.Delete</c>: drop its log call; no Error entry carries the exception.
    /// </summary>
    [Theory]
    [MemberData(nameof(Operations))]
    public async SystemTask A_failed_command_returns_generic_text_and_logs_the_exception(string operation)
    {
        await using var failingSource = FailingDataSource();
        var tasks = new TaskRepository(new DaedalusDb(failingSource), _taskLog);
        var projects = new ProjectRepository(new DaedalusDb(failingSource), _projectLog);
        var task = IntegrationTestFactory.CreateTask();
        var project = IntegrationTestFactory.CreateProject();

        var error = operation switch
        {
            "task.GetById" => (await tasks.GetByIdAsync(Guid.NewGuid(), CancellationToken.None)).Error,
            "task.GetByProjectId" => (await tasks.GetByProjectIdAsync(Guid.NewGuid(), CancellationToken.None)).Error,
            "task.Add" => (await tasks.AddAsync(task, CancellationToken.None)).Error,
            "task.Update" => (await tasks.UpdateAsync(task, CancellationToken.None)).Error,
            "task.Delete" => (await tasks.DeleteAsync(task, CancellationToken.None)).Error,
            "project.GetById" => (await projects.GetByIdAsync(Guid.NewGuid(), CancellationToken.None)).Error,
            "project.Add" => (await projects.AddAsync(project, CancellationToken.None)).Error,
            "project.Update" => (await projects.UpdateAsync(project, CancellationToken.None)).Error,
            "project.Delete" => (await projects.DeleteAsync(Guid.NewGuid(), CancellationToken.None)).Error,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        error.Should().NotContain(Secret).And.NotContain("hunter2");
        error.Should().Contain("The cause is logged.");
        var logged = _taskLog.Entries.Concat(_projectLog.Entries).Should().ContainSingle(e => e.Level == LogLevel.Error).Subject;
        logged.Exception.Should().NotBeNull();
        logged.Exception!.GetBaseException().Should().BeOfType<SecretDbException>();
    }

    /// <summary>
    ///     Phase 3.5: the task and project repositories run on ZeroAlloc.ORM, outside EF's interceptors, so their
    ///     database fails where it opens: every physical connection throws <see cref="SecretDbException"/> before any
    ///     command runs.
    /// </summary>
    private NpgsqlDataSource FailingDataSource()
    {
        var builder = new NpgsqlDataSourceBuilder(fixture.ConnectionString);
        builder.UsePhysicalConnectionInitializer(
            _ => throw new SecretDbException(),
            _ => throw new SecretDbException());
        return builder.Build();
    }

    private sealed class SecretDbException() : DbException($"connection failed: Host=db;{Secret}");

    /// <summary>Records each entry's level and exception.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, exception));
    }
}
