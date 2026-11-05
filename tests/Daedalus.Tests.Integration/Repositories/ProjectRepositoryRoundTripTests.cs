using AwesomeAssertions.Equivalency;
using Daedalus.Application.DTOs;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Orm.Projects;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using DomainTask = Daedalus.Domain.Entities.Task;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Phase 3.5: the project repository and query service on ZeroAlloc.ORM store and load every column of a project,
///     load a row whatever <c>Project.Create</c> would say of it today, page projects with their own tasks, refuse a
///     guarded update that lost a race with "modified by another operation", and keep today's results for a project
///     deleted under an update or a delete.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ProjectRepositoryRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);

    public async SystemTask InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public SystemTask DisposeAsync() => SystemTask.CompletedTask;

    private ProjectRepository Repository() => new(fixture.Db, NullLogger<ProjectRepository>.Instance);

    private TaskRepository Tasks() => new(fixture.Db, NullLogger<TaskRepository>.Instance);

    private ProjectQueryService Queries() => new(fixture.Db, new DisabledWorkflowRunStatusReader());

    /// <summary>
    ///     A project stored through the repository, with a repository URL and branch, comes back equal in every property
    ///     with a UTC creation time: through the get, and with its two tasks, oldest first, through the read with tasks
    ///     and the page. Red, shown once: read <c>"Description"</c> into <c>ProjectName</c> in <c>FindRowQuery</c>, and
    ///     the comparison names <c>ProjectName</c>.
    /// </summary>
    [Fact]
    public async SystemTask Stored_project_loads_back_whole()
    {
        var project = Project.Create(
            Guid.NewGuid(), "Name", "Description", "1.2", "https://example.com/r.git", "dev", _at).Value;
        (await Repository().AddAsync(project, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var newer = await AddTaskAsync(project.Id, "TASK-2", _at.AddMinutes(2));
        var older = await AddTaskAsync(project.Id, "TASK-1", _at.AddMinutes(1));

        var loaded = (await Repository().GetByIdAsync(project.Id, CancellationToken.None)).Value;
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new ProjectSql(connection);
        var withTasks = await sql.FindWithTasksAsync(project.Id, CancellationToken.None);
        var (total, page) = await sql.PageWithTasksAsync(0, 10, CancellationToken.None);

        loaded.Should().BeEquivalentTo(Expected(project, []), Members);
        loaded.RowVersion.Should().NotBe(0u);
        loaded.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        withTasks.Should().BeEquivalentTo(Expected(project, [older, newer]), Members);
        withTasks!.Tasks.Select(t => t.TaskId).Should().Equal("TASK-1", "TASK-2");
        total.Should().Be(1);
        page.Should().ContainSingle().Which.Should().BeEquivalentTo(Expected(project, [older, newer]), Members);
        page[0].Tasks.Select(t => t.TaskId).Should().Equal("TASK-1", "TASK-2");
    }

    /// <summary>
    ///     Review focus 1: a row <c>Create</c> refuses, a blank name, description and version, with a modification time
    ///     and an untrimmed URL and branch, still loads as written, through the repository and the query service. Red:
    ///     build the loaded row through <c>Create</c>, and the load fails.
    /// </summary>
    [Fact]
    public async SystemTask A_row_Create_rejects_still_loads()
    {
        var id = Guid.NewGuid();
        Project.Create(id, " ", " ", " ").IsFailure.Should().BeTrue("the row below is one Create refuses");
        await ExecuteAsync(
            """
            INSERT INTO "Projects"
                ("Id", "Version", "ProjectName", "Description", "CreatedAt", "ModifiedAt", "DefaultBranch",
                 "RepositoryUrl")
            VALUES (@id, ' ', ' ', ' ', @at, @modifiedAt, ' dev ', ' https://example.com/r.git ')
            """,
            ("id", id), ("at", _at), ("modifiedAt", _at.AddHours(1)));
        var expected = Project.Rehydrate(
            id, " ", " ", " ", _at, _at.AddHours(1), " dev ", " https://example.com/r.git ", 0, []);

        var loaded = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        var dto = await Queries().GetByIdAsync(id);

        loaded.Should().BeEquivalentTo(expected, Members);
        dto!.ProjectName.Should().Be(" ");
        dto.ModifiedAt.Should().Be(_at.AddHours(1));
        dto.RepositoryUrl.Should().Be(" https://example.com/r.git ");
    }

    /// <summary>
    ///     Projects with one name page by id after the name, and each page carries the tasks of its own project only.
    ///     The tie order has no reliable red: without <c>ThenBy(p => p.Id)</c> the order of tied rows is undefined, so
    ///     the test pins the order the tie-breaker defines. The ownership half has none through the public surface
    ///     either: <c>PageWithTasksAsync</c> groups the tasks by project id, so with the <c>IN</c> filter dropped the
    ///     strays are read and then dropped; the filter only limits the rows read.
    /// </summary>
    [Fact]
    public async SystemTask A_page_carries_the_tasks_of_its_own_projects()
    {
        var owners = new Dictionary<Guid, Guid>();
        for (var i = 0; i < 3; i++)
        {
            var project = Project.Create(Guid.NewGuid(), "Same", "D", createdAt: _at).Value;
            (await Repository().AddAsync(project, CancellationToken.None)).IsSuccess.Should().BeTrue();
            var task = await AddTaskAsync(project.Id, $"TASK-{i}", _at);
            owners[project.Id] = task.Id;
        }

        var pages = new List<ProjectDto>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await Queries().GetAllAsync(page, 1);
            result.Total.Should().Be(3);
            pages.Add(result.Items.Should().ContainSingle().Subject);
        }

        pages.Select(p => p.Id).Should().Equal(owners.Keys.Order());
        pages.Should().AllSatisfy(p => p.Tasks.Should().ContainSingle().Which.Id.Should().Be(owners[p.Id]));
    }

    /// <summary>
    ///     Review focus 2, #290: two reads of one project, the first saved, the second refused with "modified by another
    ///     operation"; the first save's values stand. Red, shown against EF Core's save before the port, whose
    ///     <c>bytea</c> token was never set: the second save succeeds and overwrites the first. Red on the port: in
    ///     <c>ProjectSql.UpdateAsync</c>, pass the row version re-read just before the write instead of the one the
    ///     project carries; the second save succeeds.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_loses_the_race_with_ModifiedByAnotherOperation()
    {
        var id = await AddProjectAsync();
        var first = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        var second = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        first.UpdateMetadata("First", "First description").IsSuccess.Should().BeTrue();
        first.UpdateVersion("2.0").IsSuccess.Should().BeTrue();
        second.UpdateMetadata("Second", "Second description").IsSuccess.Should().BeTrue();
        second.UpdateVersion("3.0").IsSuccess.Should().BeTrue();

        (await Repository().UpdateAsync(first, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var result = await Repository().UpdateAsync(second, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be("The project was modified by another operation. Please retry.");
        var stored = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        stored.ProjectName.Should().Be("First");
        stored.Description.Should().Be("First description");
        stored.Version.Should().Be("2.0");
        stored.ModifiedAt.Should().BeCloseTo(first.ModifiedAt!.Value, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    ///     Review focus 3, ruling R68: a project deleted after its load keeps EF Core's answer to its update, the logged
    ///     "Error updating project {id}", where EF's update matched no row; shown green against EF Core before the
    ///     port. Red: answer every <c>ZeroAllocOrmConcurrencyException</c> as modified by another operation; the deleted
    ///     project answers that instead.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_of_a_project_deleted_since_its_load_keeps_todays_error()
    {
        var id = await AddProjectAsync();
        var read = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        read.UpdateMetadata("Renamed", "Described").IsSuccess.Should().BeTrue();
        await ExecuteAsync("""DELETE FROM "Projects" WHERE "Id" = @id""", ("id", id));

        var result = await Repository().UpdateAsync(read, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Error updating project {id}. The cause is logged.");
        (await Repository().GetByIdAsync(id, CancellationToken.None)).Error.Should().Be($"Project {id} not found");
    }

    /// <summary>
    ///     The update writes the name, description, version and modification time and nothing else: a repository URL
    ///     and branch changed in memory are not stored. Red: also set <c>"RepositoryUrl"</c> in <c>UpdateQuery</c>; the
    ///     stored URL changes.
    /// </summary>
    [Fact]
    public async SystemTask Update_writes_only_the_columns_its_domain_methods_change()
    {
        var id = await AddProjectAsync();
        var project = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        project.UpdateMetadata("Renamed", "New").IsSuccess.Should().BeTrue();
        project.UpdateVersion("4.0").IsSuccess.Should().BeTrue();
        project.UpdateRepositoryUrl("https://example.com/other.git", "other").IsSuccess.Should().BeTrue();

        (await Repository().UpdateAsync(project, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var stored = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        stored.ProjectName.Should().Be("Renamed");
        stored.Description.Should().Be("New");
        stored.Version.Should().Be("4.0");
        stored.ModifiedAt.Should().BeCloseTo(project.ModifiedAt!.Value, TimeSpan.FromMilliseconds(1));
        stored.RepositoryUrl.Should().Be("https://example.com/r.git");
        stored.DefaultBranch.Should().Be("dev");
        stored.CreatedAt.Should().Be(_at);
    }

    /// <summary>
    ///     The delete removes the project and, by the database's cascade, its tasks. Red: return success without running
    ///     the delete; the project still loads.
    /// </summary>
    [Fact]
    public async SystemTask DeleteAsync_removes_the_project_and_its_tasks()
    {
        var id = await AddProjectAsync();
        var task = await AddTaskAsync(id, "TASK-1", _at);

        (await Repository().DeleteAsync(id, CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await Repository().GetByIdAsync(id, CancellationToken.None)).Error.Should().Be($"Project {id} not found");
        (await Tasks().GetByIdAsync(task.Id, CancellationToken.None)).Error.Should().Be($"Task {task.Id} not found");
    }

    /// <summary>
    ///     Ruling R69: a delete that removes no row answers "Project {id} not found", as EF's load did for a project not
    ///     stored. Red: answer success whatever the rows affected; the delete of a missing project succeeds.
    /// </summary>
    [Fact]
    public async SystemTask DeleteAsync_of_a_missing_project_is_not_found()
    {
        var id = Guid.NewGuid();

        var result = await Repository().DeleteAsync(id, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Project {id} not found");
    }

    private static Func<EquivalencyOptions<Project>, EquivalencyOptions<Project>> Members =>
        o => o.ComparingByMembers<Project>().ComparingByMembers<DomainTask>()
            .Excluding((IMemberInfo m) => m.Name == nameof(Project.RowVersion));

    private static Project Expected(Project project, IEnumerable<DomainTask> tasks) =>
        Project.Rehydrate(
            project.Id, project.Version, project.ProjectName, project.Description, project.CreatedAt,
            project.ModifiedAt, project.DefaultBranch, project.RepositoryUrl, 0, tasks);

    private async System.Threading.Tasks.Task<Guid> AddProjectAsync()
    {
        var project = Project.Create(
            Guid.NewGuid(), "Name", "Description", "1.0", "https://example.com/r.git", "dev", _at).Value;
        (await Repository().AddAsync(project, CancellationToken.None)).IsSuccess.Should().BeTrue();
        return project.Id;
    }

    private async System.Threading.Tasks.Task<DomainTask> AddTaskAsync(Guid projectId, string taskId, DateTime at)
    {
        var task = DomainTask.Create(
            Guid.NewGuid(), projectId, taskId, "T", "D", Priority.Low, "P", 1, Complexity.Low, "P", at).Value;
        (await Tasks().AddAsync(task, CancellationToken.None)).IsSuccess.Should().BeTrue();
        return task;
    }

    private async SystemTask ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}
