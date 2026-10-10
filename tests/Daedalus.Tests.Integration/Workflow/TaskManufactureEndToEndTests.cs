using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos.Git;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.8 spec, Integration: a board task, started over <c>POST /api/tasks/{id}/manufacture</c>, becomes a real run.
///     The run's status appears on <c>GET /api/tasks/{id}</c> as it moves. Only the model and the pull-request host are
///     replaced. The project points at the scratch host's local remote, which ruling P2's exact-remote rule matches.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TaskManufactureEndToEndTests(PostgresFixture fixture)
{
    /// <summary>
    ///     Red: map a run's status from the stored value; the gate reads 0, not 5.
    ///     Red: skip persisting the attach; the task has no run, so the gate reads its stored 0, not 5.
    ///     Red: let a live run start again; the second POST is 201.
    ///     Red: drop the pull request link from the reader; <c>pullRequestUrl</c> is null.
    /// </summary>
    [Fact]
    public async Task A_task_started_from_the_board_shows_its_runs_status_and_pull_request()
    {
        var pullRequests = new FakePullRequestPublisher();
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            new ScriptedManufactureRuntime(),
            seed: [("README.md", "usage"), ("AGENT.md", "Run dotnet test.\n")],
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
                services.RemoveAll<IPullRequestPublisher>();
                services.RemoveAll<IOpenPullRequestLookup>();
                services.AddSingleton<IPullRequestPublisher>(pullRequests);
                services.AddSingleton<IOpenPullRequestLookup>(pullRequests);
            });

        var project = Project.Create(Guid.NewGuid(), "Sandbox", "The scratch remote", repositoryUrl: host.Remote.Url).Value;
        var task = IntegrationTestFactory.CreateTask(projectId: project.Id);
        project.AddTask(task).IsSuccess.Should().BeTrue();
        await using (var db = await host.Factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            db.Projects.Add(project);
            await db.SaveChangesAsync();
        }

        using var client = host.Client("a-developer", "developer");

        var start = await client.PostAsync($"/api/tasks/{task.Id}/manufacture", content: null);
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

        var atGate = await client.GetFromJsonAsync<TaskDto>($"/api/tasks/{task.Id}");
        atGate!.Status.Should().Be((int)TaskStatus.AwaitingApproval);
        atGate.WorkflowRunId.Should().Be(runId);
        atGate.PullRequestUrl.Should().BeNull();

        (await client.PostAsync($"/api/tasks/{task.Id}/manufacture", content: null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

        var resume = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{runId}/resume", new { signal = "human_approval", payload = (string?)null, applyStandingInstructions = true });
        resume.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Succeeded, "succeeded");

        var done = await client.GetFromJsonAsync<TaskDto>($"/api/tasks/{task.Id}");
        done!.Status.Should().Be((int)TaskStatus.Completed);
        done.PullRequestUrl.Should().Be(new Uri(FakePullRequestPublisher.Url));
    }
}
