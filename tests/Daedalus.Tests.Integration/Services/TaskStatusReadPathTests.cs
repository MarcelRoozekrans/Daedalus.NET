using Daedalus.Api.Services;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Infrastructure.Services;
using Daedalus.Tests.Integration.Fixtures;
using SystemTask = System.Threading.Tasks.Task;
using Task = Daedalus.Domain.Entities.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Integration.Services;

/// <summary>Every read path shows the status derived from the task's run, with its run id and pull request.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class TaskStatusReadPathTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Uri _pr = new("https://github.com/o/r/pull/7");
    private readonly IWorkflowRunStatusReader _runs = Substitute.For<IWorkflowRunStatusReader>();
    private ApplicationDbContext _db = null!;
    private Task _running = null!;
    private Task _awaiting = null!;
    private Task _runless = null!;
    private Guid _projectId;

    public async SystemTask InitializeAsync()
    {
        await fixture.DatabaseResetter.ResetAsync();
        _db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(fixture.ConnectionString));

        var project = IntegrationTestFactory.CreateProject();
        _projectId = project.Id;
        _running = IntegrationTestFactory.CreateTask(projectId: _projectId, taskId: "T-1");
        _awaiting = IntegrationTestFactory.CreateTask(projectId: _projectId, taskId: "T-2");
        _runless = IntegrationTestFactory.CreateTask(projectId: _projectId, taskId: "T-3");
        _running.AttachRun(Guid.NewGuid()).IsSuccess.Should().BeTrue();
        _awaiting.AttachRun(Guid.NewGuid()).IsSuccess.Should().BeTrue();
        _db.Projects.Add(project);
        _db.Tasks.AddRange(_running, _awaiting, _runless);
        await _db.SaveChangesAsync();

        _runs.ReadAsync(_running.WorkflowRunId!.Value, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<WorkflowRunStatus>(new WorkflowRunStatus(WorkflowRunState.Running, null)));
        _runs.ReadAsync(_awaiting.WorkflowRunId!.Value, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<WorkflowRunStatus>(new WorkflowRunStatus(WorkflowRunState.Awaiting, _pr)));
    }

    public async SystemTask DisposeAsync() => await _db.DisposeAsync();

    private void AssertDerived(IEnumerable<TaskDto> dtos)
    {
        var list = dtos.ToDictionary(d => d.Id);
        list[_running.Id].Status.Should().Be((int)TaskStatus.InProgress);
        list[_running.Id].WorkflowRunId.Should().Be(_running.WorkflowRunId);
        list[_running.Id].PullRequestUrl.Should().BeNull();
        list[_awaiting.Id].Status.Should().Be((int)TaskStatus.AwaitingApproval);
        list[_awaiting.Id].WorkflowRunId.Should().Be(_awaiting.WorkflowRunId);
        list[_awaiting.Id].PullRequestUrl.Should().Be(_pr);
    }

    /// <summary>Red: pass <c>WorkflowRunStatus.Unknown</c> in <c>TaskQueryService.GetAllAsync</c> instead of reading; the status reads Pending.</summary>
    [Fact]
    public async SystemTask Task_query_GetAll_derives_the_status_and_skips_runless_tasks()
    {
        var page = await new TaskQueryService(_db, _runs).GetAllAsync();

        AssertDerived(page.Items);
        page.Items.Single(d => d.Id == _runless.Id).WorkflowRunId.Should().BeNull();
        await _runs.Received(2).ReadAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Red: pass <c>WorkflowRunStatus.Unknown</c> in <c>GetByIdAsync</c> instead of reading.</summary>
    [Fact]
    public async SystemTask Task_query_GetById_derives_the_status()
    {
        var dto = await new TaskQueryService(_db, _runs).GetByIdAsync(_awaiting.Id);

        dto!.Status.Should().Be((int)TaskStatus.AwaitingApproval);
        dto.WorkflowRunId.Should().Be(_awaiting.WorkflowRunId);
        dto.PullRequestUrl.Should().Be(_pr);
    }

    /// <summary>Red: pass <c>WorkflowRunStatus.Unknown</c> in <c>ProjectQueryService.GetAllAsync</c> instead of reading.</summary>
    [Fact]
    public async SystemTask Project_query_GetAll_derives_the_status_and_skips_runless_tasks()
    {
        var page = await new ProjectQueryService(_db, _runs).GetAllAsync(1, 10);

        AssertDerived(page.Items.Single().Tasks);
        await _runs.Received(2).ReadAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    /// <summary>Red: pass <c>WorkflowRunStatus.Unknown</c> in <c>ProjectQueryService.GetWithTasksAsync</c> instead of reading.</summary>
    [Fact]
    public async SystemTask Project_query_GetWithTasks_derives_the_status()
    {
        var project = await new ProjectQueryService(_db, _runs).GetWithTasksAsync(_projectId);

        AssertDerived(project!.Tasks);
    }
}
