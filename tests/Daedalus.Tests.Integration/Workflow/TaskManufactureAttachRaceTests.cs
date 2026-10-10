using Daedalus.Agents;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Thalos.Workflow;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     The attach of a started run is saved on the task instance the service read, over the real repositories, so the
///     task's <c>xmin</c> guards it. A task changed while the run was starting refuses the attach: the run exists, the
///     start answers AttachConflict naming it, and no second run is started.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TaskManufactureAttachRaceTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Remote = "https://github.com/o/daedalus-sandbox";

    private readonly IWorkflowRunStatusReader _runs = Substitute.For<IWorkflowRunStatusReader>();
    private readonly IManufactureRunStarter _starter = Substitute.For<IManufactureRunStarter>();
    private readonly Guid _started = Guid.NewGuid();
    private Guid _taskId;

    public async SystemTask InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        var project = Project.Create(Guid.NewGuid(), "Sandbox", "The sandbox", repositoryUrl: Remote).Value;
        var task = IntegrationTestFactory.CreateTask(projectId: project.Id);
        _taskId = task.Id;
        await using var db = NewContext();
        db.Projects.Add(project);
        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        _runs.ReadAsync(Guid.Empty, default).ReturnsForAnyArgs(new ValueTask<WorkflowRunStatus>(WorkflowRunStatus.Unknown));
    }

    public SystemTask DisposeAsync() => SystemTask.CompletedTask;

    private ApplicationDbContext NewContext() => new(PostgresFixture.CreateDbContextOptions(fixture.ConnectionString));

    /// <summary>
    ///     Another request renames the task while its run is starting.
    ///     Red: in <c>TaskManufactureService.StartAsync</c>, re-read the task through <c>GetByIdAsync</c> after the start
    ///     and attach the run to that fresh copy; the attach is saved, so the start succeeds and the failure assertion
    ///     fails.
    /// </summary>
    [Fact]
    public async SystemTask A_task_changed_while_its_run_starts_refuses_the_attach_and_starts_no_second_run()
    {
        _starter.StartAsync(default!, default).ReturnsForAnyArgs(_ => RenameThenStartAsync());

        await using var db = NewContext();
        var workflow = new WorkflowConfig();
        workflow.Repositories.Add(new RepositoryConfig { Name = "daedalus-sandbox", Remote = Remote });
        var service = new TaskManufactureService(
            new TaskRepository(db, Substitute.For<ILogger<TaskRepository>>()),
            new ProjectRepository(db, Substitute.For<ILogger<ProjectRepository>>()),
            _runs,
            _starter,
            workflow);

        var result = await service.StartAsync(_taskId, new RunPrincipal("a-dev", ["developer"]), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Kind.Should().Be(TaskManufactureFailureKind.AttachConflict);
        result.Error.RunId.Should().Be(_started);
        await _starter.ReceivedWithAnyArgs(1).StartAsync(default!, default);
        await using var check = NewContext();
        var stored = await check.Tasks.SingleAsync(t => t.Id == _taskId);
        stored.WorkflowRunId.Should().BeNull("the lost attach must not be saved");
        stored.Title.Should().Be("Renamed", "the other request's change stands");
    }

    /// <summary>Renames the task through another context, then reports <see cref="_started"/> as the run.</summary>
    private async ValueTask<Result<Guid, ManufactureStartFailure>> RenameThenStartAsync()
    {
        await using var other = NewContext();
        var current = await other.Tasks.SingleAsync(t => t.Id == _taskId);
        current.UpdateMetadata("Renamed", "d", Priority.Low, "p", Complexity.Low).IsSuccess.Should().BeTrue();
        await other.SaveChangesAsync();
        return Result<Guid, ManufactureStartFailure>.Success(_started);
    }
}
