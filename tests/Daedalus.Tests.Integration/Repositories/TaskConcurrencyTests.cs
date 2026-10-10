using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
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
        await using var db = NewContext();
        db.Projects.Add(project);
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
    }

    public SystemTask DisposeAsync() => SystemTask.CompletedTask;

    private ApplicationDbContext NewContext() => new(PostgresFixture.CreateDbContextOptions(fixture.ConnectionString));

    private async SystemTask AttachRunUnderneathAsync()
    {
        _runId = Guid.NewGuid();
        await using var other = NewContext();
        var current = await other.Tasks.SingleAsync(t => t.Id == _taskId);
        current.AttachRun(_runId).IsSuccess.Should().BeTrue();
        await other.SaveChangesAsync();
    }

    private async System.Threading.Tasks.Task<Guid?> StoredRunAsync()
    {
        await using var db = NewContext();
        return await db.Tasks.Where(t => t.Id == _taskId).Select(t => t.WorkflowRunId).SingleAsync();
    }

    private static void Rename(Daedalus.Domain.Entities.Task task) =>
        task.UpdateMetadata("Renamed", "d", Priority.Low, "p", Complexity.Low).IsSuccess.Should().BeTrue();

    /// <summary>Red: remove the xmin concurrency token from <c>TaskConfiguration</c>; the stale save wins and the run is null.</summary>
    [Fact]
    public async SystemTask A_stale_update_is_a_conflict_and_keeps_the_attached_run()
    {
        await using var db = NewContext();
        var repository = new TaskRepository(db, _logger);
        var stale = (await repository.GetByIdAsync(_taskId, CancellationToken.None)).Value;
        Rename(stale);

        await AttachRunUnderneathAsync();
        var result = await repository.UpdateAsync(stale, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        TaskRunGuard.IsConflict(result.Error).Should().BeTrue();
        (await StoredRunAsync()).Should().Be(_runId);
    }

    /// <summary>
    ///     Reads through <c>GetByIdAsync</c> (no tracking), as the delete handler does, then attaches a run underneath.
    ///     Red: re-read a fresh copy in <c>DeleteAsync</c> instead of deleting the instance passed in; the row and its
    ///     run are deleted although the guard saw a task without one.
    /// </summary>
    [Fact]
    public async SystemTask A_delete_after_a_run_was_attached_is_a_conflict_and_keeps_the_task()
    {
        await using var db = NewContext();
        var repository = new TaskRepository(db, _logger);
        var read = (await repository.GetByIdAsync(_taskId, CancellationToken.None)).Value;

        await AttachRunUnderneathAsync();
        var result = await repository.DeleteAsync(read, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        TaskRunGuard.IsConflict(result.Error).Should().BeTrue();
        (await StoredRunAsync()).Should().Be(_runId);
    }

    /// <summary>Red: drop the <c>e.Entity is Task</c> filter from the catch; a conflict of another entity reads as a task conflict.</summary>
    [Fact]
    public async SystemTask A_concurrency_failure_of_another_entity_is_not_reported_as_a_task_conflict()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new ConcurrencyFailureWithoutEntries())
            .Options;
        await using var db = new ApplicationDbContext(options);
        var repository = new TaskRepository(db, _logger);
        var task = (await repository.GetByIdAsync(_taskId, CancellationToken.None)).Value;
        Rename(task);

        var updated = await repository.UpdateAsync(task, CancellationToken.None);
        var deleted = await repository.DeleteAsync(task, CancellationToken.None);

        updated.IsFailure.Should().BeTrue();
        TaskRunGuard.IsConflict(updated.Error).Should().BeFalse();
        deleted.IsFailure.Should().BeTrue();
        TaskRunGuard.IsConflict(deleted.Error).Should().BeFalse();
    }

    private sealed class ConcurrencyFailureWithoutEntries : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default) =>
            throw new DbUpdateConcurrencyException("lost the race", []);
    }

    /// <summary>Red: reset the token to 0 before <c>Update</c> in <c>UpdateAsync</c>, as a no-tracking read that loses it would; an unchanged save conflicts.</summary>
    [Fact]
    public async SystemTask An_unchanged_task_still_saves()
    {
        await using var db = NewContext();
        var repository = new TaskRepository(db, _logger);
        var task = (await repository.GetByIdAsync(_taskId, CancellationToken.None)).Value;
        Rename(task);

        (await repository.UpdateAsync(task, CancellationToken.None)).IsSuccess.Should().BeTrue();
    }
}
