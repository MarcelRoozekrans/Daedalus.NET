using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Persistence.Orm.Tasks;
using Daedalus.Tests.Integration.Fixtures;
using Daedalus.Tests.Shared;
using Microsoft.Extensions.Logging;
using Npgsql;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Repositories;

/// <summary>
///     A save of a stale task copy must not detach a run that was attached since: the task's <c>xmin</c> is enforced
///     on PostgreSQL, unlike a <c>byte[]</c> row version.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TaskConcurrencyTests(PostgresFixture fixture) : IAsyncLifetime
{
    private readonly ILogger<TaskRepository> _logger = Substitute.For<ILogger<TaskRepository>>();
    private Guid _taskId;
    private Guid _runId;

    public async SystemTask InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        var project = IntegrationTestFactory.CreateProject();
        var task = IntegrationTestFactory.CreateTask(projectId: project.Id);
        _taskId = task.Id;
        await StoreSeed.AddAsync(fixture.Db, [project], [task]);
    }

    public SystemTask DisposeAsync() => SystemTask.CompletedTask;

    private async SystemTask AttachRunUnderneathAsync()
    {
        _runId = Guid.NewGuid();
        await using var other = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new TaskSql(other);
        var current = (await sql.FindAsync(_taskId, CancellationToken.None))!;
        current.AttachRun(_runId).IsSuccess.Should().BeTrue();
        await sql.UpdateAsync(current, CancellationToken.None);
    }

    private async System.Threading.Tasks.Task<Guid?> StoredRunAsync()
    {
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        return (await new TaskSql(connection).FindAsync(_taskId, CancellationToken.None))!.WorkflowRunId;
    }

    private static void Rename(Daedalus.Domain.Entities.Task task) =>
        task.UpdateMetadata("Renamed", "d", Priority.Low, "p", Complexity.Low).IsSuccess.Should().BeTrue();

    /// <summary>
    ///     Red: in <c>TaskSql.UpdateAsync</c>, pass the row version re-read just before the write instead of the one the
    ///     task carries; the stale save wins and the run is null.
    /// </summary>
    [Fact]
    public async SystemTask A_stale_update_is_a_conflict_and_keeps_the_attached_run()
    {
        var repository = new TaskRepository(fixture.Db, _logger);
        var stale = (await repository.GetByIdAsync(_taskId, CancellationToken.None)).Value;
        Rename(stale);

        await AttachRunUnderneathAsync();
        var result = await repository.UpdateAsync(stale, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        TaskRunGuard.IsConflict(result.Error).Should().BeTrue();
        (await StoredRunAsync()).Should().Be(_runId);
    }

    /// <summary>
    ///     Reads through <c>GetByIdAsync</c>, as the delete handler does, then attaches a run underneath.
    ///     Red: in <c>TaskSql.DeleteAsync</c>, pass the row version re-read just before the delete; the row and its run
    ///     are deleted although the guard saw a task without one.
    /// </summary>
    [Fact]
    public async SystemTask A_delete_after_a_run_was_attached_is_a_conflict_and_keeps_the_task()
    {
        var repository = new TaskRepository(fixture.Db, _logger);
        var read = (await repository.GetByIdAsync(_taskId, CancellationToken.None)).Value;

        await AttachRunUnderneathAsync();
        var result = await repository.DeleteAsync(read, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        TaskRunGuard.IsConflict(result.Error).Should().BeTrue();
        (await StoredRunAsync()).Should().Be(_runId);
    }

    /// <summary>
    ///     Phase 3.5: a database failure of the update or the delete is not a lost race, so it is not reported as a
    ///     conflict. A trigger fails both statements with P0001. Red: catch every exception as
    ///     <c>ChangedUnderneath</c> in <c>UpdateAsync</c> and <c>DeleteAsync</c>; both read as task conflicts.
    /// </summary>
    [Fact]
    public async SystemTask A_database_failure_is_not_reported_as_a_task_conflict()
    {
        var repository = new TaskRepository(fixture.Db, _logger);
        var task = (await repository.GetByIdAsync(_taskId, CancellationToken.None)).Value;
        Rename(task);
        await ExecuteAsync(
            """
            CREATE FUNCTION refuse_task_write() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'task write refused' USING ERRCODE = 'P0001';
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER refuse_task_write BEFORE UPDATE OR DELETE ON "Tasks"
                FOR EACH ROW EXECUTE FUNCTION refuse_task_write();
            """);
        Result updated;
        Result deleted;
        try
        {
            updated = await repository.UpdateAsync(task, CancellationToken.None);
            deleted = await repository.DeleteAsync(task, CancellationToken.None);
        }
        finally
        {
            await ExecuteAsync("""DROP TRIGGER refuse_task_write ON "Tasks"; DROP FUNCTION refuse_task_write();""");
        }

        updated.IsFailure.Should().BeTrue();
        TaskRunGuard.IsConflict(updated.Error).Should().BeFalse();
        deleted.IsFailure.Should().BeTrue();
        TaskRunGuard.IsConflict(deleted.Error).Should().BeFalse();
    }

    private async SystemTask ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    ///     Red: pass 0 as the row version to the update in <c>TaskSql.UpdateAsync</c>, as a read that loses it would; an
    ///     unchanged save conflicts.
    /// </summary>
    [Fact]
    public async SystemTask An_unchanged_task_still_saves()
    {
        var repository = new TaskRepository(fixture.Db, _logger);
        var task = (await repository.GetByIdAsync(_taskId, CancellationToken.None)).Value;
        Rename(task);

        (await repository.UpdateAsync(task, CancellationToken.None)).IsSuccess.Should().BeTrue();
    }
}
