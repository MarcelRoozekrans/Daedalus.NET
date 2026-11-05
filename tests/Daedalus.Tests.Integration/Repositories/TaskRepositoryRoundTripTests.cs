using AwesomeAssertions.Equivalency;
using Daedalus.Api.Services;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Orm.Tasks;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using DomainTask = Daedalus.Domain.Entities.Task;
using SystemTask = System.Threading.Tasks.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     Phase 3.5: the task family on ZeroAlloc.ORM stores and loads every column of a task and its executions, loads a
///     row whatever <c>Task.Create</c> would say of it today, pages tasks with their own executions, and refuses a
///     guarded save that lost a race with today's results.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TaskRepositoryRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _at = new(2026, 11, 5, 9, 15, 30, DateTimeKind.Utc);
    private Guid _projectId;

    public async SystemTask InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _projectId = Guid.NewGuid();
        await ExecuteAsync(
            """INSERT INTO "Projects" ("Id", "ProjectName", "Description", "CreatedAt", "Version") VALUES (@id, 'P', 'D', @at, '1.0')""",
            ("id", _projectId), ("at", _at));
    }

    public SystemTask DisposeAsync() => SystemTask.CompletedTask;

    private TaskRepository Repository() => new(fixture.Db, NullLogger<TaskRepository>.Instance);

    /// <summary>
    ///     A task stored through the repository, with both lists, and two executions beside it come back equal in every
    ///     property, with UTC times and the interval as a <see cref="TimeSpan"/>: through the get, through the page with
    ///     its executions, and through the project list without them. Red, shown once: read <c>"Phase"</c> into
    ///     <c>Title</c> in <c>FindRowsQuery</c>; the comparison names <c>Title</c>.
    /// </summary>
    [Fact]
    public async SystemTask Stored_task_loads_back_whole()
    {
        var task = DomainTask.Create(
            Guid.NewGuid(), _projectId, "TASK-001", "Title", "Description", Priority.High, "Backend", 2,
            Complexity.Low, "Prompt", _at).Value;
        task.AddDependency("TASK-000").IsSuccess.Should().BeTrue();
        task.AddFileToModify("src/a.cs").IsSuccess.Should().BeTrue();
        task.AddFileToModify("src/b.cs").IsSuccess.Should().BeTrue();
        (await Repository().AddAsync(task, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var first = TaskExecution.Rehydrate(
            Guid.NewGuid(), task.Id, Guid.NewGuid(), 1, "p1", "r1", false, _at.AddMinutes(1),
            TimeSpan.FromMilliseconds(1500), null, 10, null, 20);
        var second = TaskExecution.Rehydrate(
            Guid.NewGuid(), task.Id, Guid.NewGuid(), 2, "p2", "r2", true, _at.AddMinutes(2),
            TimeSpan.FromMinutes(3), "boom", 30, "model", 40);
        await InsertExecutionAsync(first);
        await InsertExecutionAsync(second);
        var expected = Expected(task, [first, second]);

        var loaded = (await Repository().GetByIdAsync(task.Id, CancellationToken.None)).Value;
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var (total, page) = await new TaskSql(connection).PageAsync(0, 10, CancellationToken.None);
        var listed = (await Repository().GetByProjectIdAsync(_projectId, CancellationToken.None)).Value;

        loaded.Should().BeEquivalentTo(expected, Members);
        loaded.RowVersion.Should().NotBe(0u);
        loaded.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        loaded.Executions[0].ExecutedAt.Kind.Should().Be(DateTimeKind.Utc);
        total.Should().Be(1);
        page.Should().ContainSingle().Which.Should().BeEquivalentTo(expected, Members);
        listed.Should().ContainSingle().Which.Should().BeEquivalentTo(Expected(task, []), Members);
    }

    /// <summary>
    ///     Review focus 1: a row <c>Create</c> refuses, an empty title and phase and a parallel group of 0, with every
    ///     other column set, still loads as written. Red: build the loaded row through <c>Create</c>, and the load fails.
    /// </summary>
    [Fact]
    public async SystemTask A_row_Create_rejects_still_loads()
    {
        var id = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        DomainTask.Create(id, _projectId, "T", "", "D", Priority.Low, "", 0, Complexity.High, "P")
            .IsFailure.Should().BeTrue("the row below is one Create refuses");
        await ExecuteAsync(
            """
            INSERT INTO "Tasks"
                ("Id", "TaskId", "ProjectId", "Title", "Description", "Priority", "Status", "Phase", "ParallelGroup",
                 "EstimatedComplexity", "Prompt", "CompletionPromise", "MaxIterations", "CurrentSessionId", "Result",
                 "IterationCount", "CreatedAt", "CompletedAt", "Learnings", "LearningsUpdatedAt", "Dependencies",
                 "FilesToModify", "WorkflowRunId")
            VALUES (@id, 'T', @projectId, '', 'D', 0, 3, '', 0, 2, 'P', 'promise', 7, @sessionId, 'result', 8, @at,
                    @completedAt, 'learned', @learnedAt, ARRAY['A', 'B'], ARRAY[]::text[], @runId)
            """,
            ("id", id), ("projectId", _projectId), ("sessionId", sessionId), ("at", _at),
            ("completedAt", _at.AddHours(1)), ("learnedAt", _at.AddHours(2)), ("runId", runId));
        var expected = DomainTask.Rehydrate(
            id, "T", _projectId, "", "D", Priority.Low, TaskStatus.Failed, "", 0, Complexity.High, "P", "promise", 7,
            sessionId, "result", 8, _at, _at.AddHours(1), "learned", _at.AddHours(2), ["A", "B"], [], runId, 0, []);

        var loaded = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;

        loaded.Should().BeEquivalentTo(expected, Members);
    }

    /// <summary>
    ///     Three tasks created at the same instant page by id after <c>CreatedAt</c>, and each page carries the
    ///     executions of its own task only. Without the <c>ThenBy(t => t.Id)</c> tie-breaker the order of tied rows is
    ///     undefined, so no reliable red exists; the test pins the order the tie-breaker defines.
    /// </summary>
    [Fact]
    public async SystemTask A_page_carries_the_executions_of_its_own_tasks()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var task = DomainTask.Create(
                Guid.NewGuid(), _projectId, $"TASK-{i}", "T", "D", Priority.Low, "P", 1, Complexity.Low, "P", _at).Value;
            (await Repository().AddAsync(task, CancellationToken.None)).IsSuccess.Should().BeTrue();
            await InsertExecutionAsync(TaskExecution.Rehydrate(
                Guid.NewGuid(), task.Id, Guid.NewGuid(), 1, "p", "r", false, _at, TimeSpan.Zero, null, 0, null, 0));
            ids.Add(task.Id);
        }

        ids.Sort();
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new TaskSql(connection);
        var pages = new List<DomainTask>();
        for (var skip = 0; skip < 3; skip++)
        {
            var (total, page) = await sql.PageAsync(skip, 1, CancellationToken.None);
            total.Should().Be(3);
            pages.Add(page.Should().ContainSingle().Subject);
        }

        pages.Select(t => t.Id).Should().Equal(ids);
        pages.Should().AllSatisfy(t => t.Executions.Should().ContainSingle().Which.TaskId.Should().Be(t.Id));
    }

    /// <summary>
    ///     Review focus 2: two reads of one task, the first saved, the second refused with today's
    ///     <c>ChangedUnderneath</c>; the first save's values stand. Red: in <c>TaskSql.UpdateAsync</c>, pass the row version
    ///     re-read just before the write instead of the one the task carries; the second save succeeds.
    /// </summary>
    [Fact]
    public async SystemTask UpdateAsync_loses_the_race_with_ChangedUnderneath()
    {
        var id = await AddTaskAsync();
        var first = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        var second = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        first.UpdateMetadata("First", "d", Priority.High, "p", Complexity.High).IsSuccess.Should().BeTrue();
        second.UpdateMetadata("Second", "d", Priority.Low, "p", Complexity.Low).IsSuccess.Should().BeTrue();

        (await Repository().UpdateAsync(first, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var result = await Repository().UpdateAsync(second, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(TaskRunGuard.ChangedUnderneath(id));
        var stored = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        stored.Title.Should().Be("First");
        stored.Priority.Should().Be(Priority.High);
    }

    /// <summary>
    ///     Ruling R57: a delete whose row a concurrent delete removes between the repository's load and its delete is a
    ///     lost race and answers today's <c>ChangedUnderneath</c>. The winner's delete holds the row lock until the
    ///     loser's statement waits on it, then commits. Red: take <c>ThrowOnNoRows</c> off the delete; it answers success
    ///     for a delete that removed nothing.
    /// </summary>
    [Fact]
    public async SystemTask DeleteAsync_loses_the_race_with_ChangedUnderneath()
    {
        var id = await AddTaskAsync();
        var read = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        await using var winner = new NpgsqlConnection(fixture.ConnectionString);
        await winner.OpenAsync();
        await using var tx = await winner.BeginTransactionAsync();
        await using (var delete = new NpgsqlCommand("""DELETE FROM "Tasks" WHERE "Id" = @id""", winner, tx))
        {
            delete.Parameters.AddWithValue("id", id);
            (await delete.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var loser = Repository().DeleteAsync(read, CancellationToken.None);
        await WaitUntilADeleteWaitsOnALockAsync();
        await tx.CommitAsync();
        var result = await loser;

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be(TaskRunGuard.ChangedUnderneath(id));
        (await Repository().GetByIdAsync(id, CancellationToken.None)).Error.Should().Be($"Task {id} not found");
    }

    /// <summary>
    ///     Ruling R57: a task already gone when the repository loads it answers today's "Task with ID {id} not found".
    ///     Red: drop the load from <c>DeleteAsync</c>; the guarded delete finds no row and answers <c>ChangedUnderneath</c>.
    /// </summary>
    [Fact]
    public async SystemTask DeleteAsync_of_a_task_missing_at_load_is_not_found()
    {
        var id = await AddTaskAsync();
        var read = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        (await Repository().DeleteAsync(read, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var result = await Repository().DeleteAsync(read, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Should().Be($"Task with ID {id} not found");
    }

    /// <summary>
    ///     The update writes the columns <c>UpdateMetadata</c>, <c>UpdateExecutionConfig</c> and <c>AttachRun</c> change
    ///     and nothing else: a dependency added in memory is not stored. Red: also set <c>"Dependencies"</c> in
    ///     <c>UpdateQuery</c>; the stored list gains it.
    /// </summary>
    [Fact]
    public async SystemTask Update_writes_only_the_columns_its_domain_methods_change()
    {
        var id = await AddTaskAsync();
        var task = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        var runId = Guid.NewGuid();
        task.UpdateMetadata("Renamed", "New", Priority.High, "Phase", Complexity.High).IsSuccess.Should().BeTrue();
        task.UpdateExecutionConfig(3, "New prompt").IsSuccess.Should().BeTrue();
        task.AttachRun(runId).IsSuccess.Should().BeTrue();
        task.AddDependency("TASK-999").IsSuccess.Should().BeTrue();

        (await Repository().UpdateAsync(task, CancellationToken.None)).IsSuccess.Should().BeTrue();

        var stored = (await Repository().GetByIdAsync(id, CancellationToken.None)).Value;
        stored.Title.Should().Be("Renamed");
        stored.Description.Should().Be("New");
        stored.Priority.Should().Be(Priority.High);
        stored.Phase.Should().Be("Phase");
        stored.EstimatedComplexity.Should().Be(Complexity.High);
        stored.ParallelGroup.Should().Be(3);
        stored.Prompt.Should().Be("New prompt");
        stored.WorkflowRunId.Should().Be(runId);
        stored.Dependencies.Should().BeEmpty();
    }

    /// <summary>
    ///     Ruling R59: the run links read only the tasks attached to one of the asked runs; a task without a run, whose
    ///     <c>"WorkflowRunId"</c> is NULL, and one attached to another run are left out. Red: filter <c>RunLinks</c> on
    ///     <c>"WorkflowRunId" IS NOT NULL</c> instead; the other run's task appears.
    /// </summary>
    [Fact]
    public async SystemTask Run_links_read_only_the_tasks_of_the_asked_runs()
    {
        var asked = Guid.NewGuid();
        var other = Guid.NewGuid();
        var attached = await AddTaskAsync(asked);
        await AddTaskAsync(other);
        await AddTaskAsync();

        var links = await new TaskQueryService(fixture.Db, new DisabledWorkflowRunStatusReader())
            .GetRunLinksAsync([asked, Guid.NewGuid()]);

        links.Should().ContainSingle();
        links[asked].TaskId.Should().Be(attached);
    }

    private static Func<EquivalencyOptions<DomainTask>, EquivalencyOptions<DomainTask>> Members =>
        o => o.ComparingByMembers<DomainTask>().ComparingByMembers<TaskExecution>().Excluding(t => t.RowVersion);

    private static DomainTask Expected(DomainTask task, IEnumerable<TaskExecution> executions) =>
        DomainTask.Rehydrate(
            task.Id, task.TaskId, task.ProjectId, task.Title, task.Description, task.Priority, task.Status, task.Phase,
            task.ParallelGroup, task.EstimatedComplexity, task.Prompt, task.CompletionPromise, task.MaxIterations,
            task.CurrentSessionId, task.Result, task.IterationCount, task.CreatedAt, task.CompletedAt, task.Learnings,
            task.LearningsUpdatedAt, task.Dependencies, task.FilesToModify, task.WorkflowRunId, 0, executions);

    private async System.Threading.Tasks.Task<Guid> AddTaskAsync(Guid? runId = null)
    {
        var task = DomainTask.Create(
            Guid.NewGuid(), _projectId, "TASK-1", "T", "D", Priority.Low, "P", 1, Complexity.Low, "P", _at).Value;
        if (runId is not null)
        {
            task.AttachRun(runId.Value).IsSuccess.Should().BeTrue();
        }

        (await Repository().AddAsync(task, CancellationToken.None)).IsSuccess.Should().BeTrue();
        return task.Id;
    }

    private SystemTask InsertExecutionAsync(TaskExecution e) =>
        ExecuteAsync(
            """
            INSERT INTO "TaskExecutions"
                ("Id", "TaskId", "SessionId", "IterationNumber", "Prompt", "LlmResponse", "CompletionPromiseFound",
                 "ExecutedAt", "ExecutionDuration", "Error", "InputTokens", "ModelId", "OutputTokens")
            VALUES (@id, @taskId, @sessionId, @iteration, @prompt, @response, @found, @at, @duration, @error,
                    @input, @model, @output)
            """,
            ("id", e.Id), ("taskId", e.TaskId), ("sessionId", e.SessionId), ("iteration", e.IterationNumber),
            ("prompt", e.Prompt), ("response", e.LlmResponse), ("found", e.CompletionPromiseFound),
            ("at", e.ExecutedAt), ("duration", e.ExecutionDuration), ("error", (object?)e.Error ?? DBNull.Value),
            ("input", e.InputTokens), ("model", (object?)e.ModelId ?? DBNull.Value), ("output", e.OutputTokens));

    private async SystemTask WaitUntilADeleteWaitsOnALockAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT count(*) FROM pg_stat_activity
                WHERE wait_event_type = 'Lock' AND query LIKE 'DELETE FROM "Tasks"%'
                """,
                connection);
            if ((long)(await command.ExecuteScalarAsync())! > 0)
            {
                return;
            }

            await SystemTask.Delay(20);
        }

        throw new TimeoutException("The repository's delete never waited on the row lock.");
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
