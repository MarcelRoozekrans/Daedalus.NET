using System.Data.Common;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Repositories;
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
    private readonly CapturingLogger<BrainstormRepository> _brainstormLog = new();
    private readonly CapturingLogger<RepositoryConfigurationRepository> _repositoryLog = new();
    private readonly CapturingLogger<CodeAnalysisRepository> _analysisLog = new();

    public static TheoryData<string> Operations => new()
    {
        "task.GetById", "task.GetByProjectId", "task.Add", "task.Update", "task.Delete",
        "project.GetById", "project.Add", "project.Update", "project.Delete",
        "brainstorm.GetById", "brainstorm.GetByProjectId", "brainstorm.Add", "brainstorm.Update",
        "repository.GetAll", "repository.GetById", "repository.Add", "repository.Update", "repository.Delete",
        "analysis.GetById", "analysis.GetPending", "analysis.GetByStatus", "analysis.GetByRepository",
        "analysis.Create", "analysis.UpdateStatus", "analysis.UpdateIteration", "analysis.RecordValidation",
        "analysis.Complete", "analysis.Fail", "analysis.Cancel", "analysis.UpdateWorkTreePath",
        "analysis.UpdateLastPrompt",
    };

    /// <summary>
    ///     Red, run for <c>task.GetById</c>: put <c>{ex.Message}</c> back into its failure text; the text carries the secret.
    ///     Red, run for <c>task.Add</c>: put the exception's message back; the text carries the secret.
    ///     Red, run for <c>project.GetById</c>: put <c>{ex.Message}</c> back; the text carries the secret.
    ///     Red, run for <c>task.Delete</c>: drop its log call; no Error entry carries the exception.
    ///     Red, run for <c>repository.Update</c>: put the exception's message back into the failure text, as before the
    ///     port; the text carries the secret (ruling R84).
    ///     Red, run for <c>brainstorm.Update</c>: put <c>{ex.Message}</c> back, as before the port; the text carries the
    ///     secret (ruling R79).
    ///     Red, run for <c>analysis.GetById</c> and the six guarded analysis writes: put the exception's message back, as
    ///     before the port; the text carries the secret (ruling R94).
    /// </summary>
    [Theory]
    [MemberData(nameof(Operations))]
    public async SystemTask A_failed_command_returns_generic_text_and_logs_the_exception(string operation)
    {
        await using var failingSource = FailingDataSource();
        var tasks = new TaskRepository(new DaedalusDb(failingSource), _taskLog);
        var projects = new ProjectRepository(new DaedalusDb(failingSource), _projectLog);
        var brainstorms = new BrainstormRepository(new DaedalusDb(failingSource), _brainstormLog);
        var repositories = new RepositoryConfigurationRepository(new DaedalusDb(failingSource), _repositoryLog);
        var analyses = new CodeAnalysisRepository(new DaedalusDb(failingSource), _analysisLog);
        var analysis = Daedalus.Domain.CodeAnalysis.CodeAnalysisRequest.Create(
            Guid.NewGuid(), "Title", "Description", "https://github.com/org/repo", 3, requirements: "[]").Value;
        var id = Guid.NewGuid();
        var repository = IntegrationTestFactory.CreateRepositoryConfiguration();
        var session = Daedalus.Domain.Entities.BrainstormSession.Create(Guid.NewGuid()).Value;
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
            "brainstorm.GetById" => (await brainstorms.GetByIdAsync(Guid.NewGuid(), CancellationToken.None)).Error,
            "brainstorm.GetByProjectId" => (await brainstorms.GetByProjectIdAsync(Guid.NewGuid(), CancellationToken.None)).Error,
            "brainstorm.Add" => (await brainstorms.AddAsync(session, CancellationToken.None)).Error,
            "brainstorm.Update" => (await brainstorms.UpdateAsync(session, CancellationToken.None)).Error,
            "repository.GetAll" => (await repositories.GetAllAsync(CancellationToken.None)).Error,
            "repository.GetById" => (await repositories.GetByIdAsync(Guid.NewGuid(), CancellationToken.None)).Error,
            "repository.Add" => (await repositories.AddAsync(repository, CancellationToken.None)).Error,
            "repository.Update" => (await repositories.UpdateAsync(repository, CancellationToken.None)).Error,
            "repository.Delete" => (await repositories.DeleteAsync(Guid.NewGuid(), CancellationToken.None)).Error,
            "analysis.GetById" => (await analyses.GetByIdAsync(id, CancellationToken.None)).Error,
            "analysis.GetPending" => (await analyses.GetPendingAsync(1, CancellationToken.None)).Error,
            "analysis.GetByStatus" => (await analyses.GetByStatusAsync(
                Daedalus.Domain.CodeAnalysis.AnalysisStatus.Pending, CancellationToken.None)).Error,
            "analysis.GetByRepository" => (await analyses.GetByRepositoryAsync("x", CancellationToken.None)).Error,
            "analysis.Create" => (await analyses.CreateAsync(analysis, CancellationToken.None)).Error,
            "analysis.UpdateStatus" => (await analyses.UpdateStatusAsync(
                id, Daedalus.Domain.CodeAnalysis.AnalysisStatus.Cancelled, CancellationToken.None)).Error,
            "analysis.UpdateIteration" => (await analyses.UpdateIterationAsync(id, 1, "p", "r", CancellationToken.None)).Error,
            "analysis.RecordValidation" => (await analyses.RecordValidationAsync(id, "{}", true, CancellationToken.None)).Error,
            "analysis.Complete" => (await analyses.CompleteAsync(id, null, null, CancellationToken.None)).Error,
            "analysis.Fail" => (await analyses.FailAsync(id, "reason", CancellationToken.None)).Error,
            "analysis.Cancel" => (await analyses.CancelAsync(id, CancellationToken.None)).Error,
            "analysis.UpdateWorkTreePath" => (await analyses.UpdateWorkTreePathAsync(id, "/tmp", CancellationToken.None)).Error,
            "analysis.UpdateLastPrompt" => (await analyses.UpdateLastPromptAsync(id, "p", "r", CancellationToken.None)).Error,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        error.Should().NotContain(Secret).And.NotContain("hunter2");
        error.Should().Contain("The cause is logged.");
        var logged = _taskLog.Entries.Concat(_projectLog.Entries).Concat(_brainstormLog.Entries).Concat(_repositoryLog.Entries)
            .Concat(_analysisLog.Entries).Should()
            .ContainSingle(e => e.Level == LogLevel.Error).Subject;
        logged.Exception.Should().NotBeNull();
        logged.Exception!.GetBaseException().Should().BeOfType<SecretDbException>();
    }

    /// <summary>
    ///     Phase 3.5: the task, project and brainstorm repositories run on ZeroAlloc.ORM, outside EF's interceptors, so
    ///     their database fails where it opens: every physical connection throws <see cref="SecretDbException"/> before
    ///     any command runs.
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
