using System.Globalization;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Git;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary><c>GET /api/workflow-runs</c>: who may list, the order, the filters, the paging, the task link, and no content.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class RunListEndpointTests(PostgresFixture fixture)
{
    private const string Starter = "a-developer";

    private const string TaskCode = "RUN-LIST-1";

    /// <summary>
    ///     No caller is 401; a caller without developer or admin is 403.
    ///     Red: remove the class-level Authorize. Red: authorize the action with TaskRead.
    /// </summary>
    [Fact]
    public async Task Only_developers_and_admins_may_list()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        using var anonymous = host.Factory.CreateClient();
        using var analyst = host.Client("an-analyst", "analyst");

        (await anonymous.GetAsync("/api/workflow-runs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await analyst.GetAsync("/api/workflow-runs")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     The newer run comes first, the task-started run carries its task's id, code and title, and the body contains
    ///     neither the sentinel work intent nor a variables or lastError property.
    ///     Red: reverse the page's rows. Red: give every row the first task link found.
    ///     Red: drop the task link lookup. Red: swap TaskCode and Title. Red: pass the task code as the title.
    ///     Red: add Variables to WorkflowRunListItem from the run. Red: add an always-null Variables property.
    ///     Red: add an always-null LastError property.
    /// </summary>
    [Fact]
    public async Task Runs_come_newest_first_with_their_task_and_no_content()
    {
        await WithTwoRunsAsync(async (host, client, runs) =>
        {
            var list = await client.GetFromJsonAsync<WorkflowRunList>("/api/workflow-runs");

            list!.Runs.Select(r => r.Id).Should().Equal(runs.IntentRunId, runs.TaskRunId);
            list.Runs[0].TaskId.Should().BeNull();
            var linked = list.Runs[1];
            linked.TaskId.Should().Be(runs.Task.Id);
            linked.TaskCode.Should().Be(TaskCode);
            linked.TaskTitle.Should().Be(runs.Task.Title);

            var body = await client.GetStringAsync("/api/workflow-runs");
            body.Should().NotContain(runs.Sentinel);
            body.Should().NotContainEquivalentOf("\"variables\"");
            body.Should().NotContainEquivalentOf("\"lastError\"");
        });
    }

    /// <summary>
    ///     status=Awaiting returns only parked runs; take=1 returns one run and a next cursor that returns the other, and
    ///     the last page has no next cursor.
    ///     Red: ignore status. Red: write Status as its number. Red: ignore take.
    ///     Red: return page.Next unencoded or always null. Red: ignore after.
    ///     Red: encode a cursor from the last row when page.Next is null.
    /// </summary>
    [Fact]
    public async Task Status_filters_and_take_pages()
    {
        await WithTwoRunsAsync(async (host, client, runs) =>
        {
            var cancel = await client.PostAsJsonAsync($"/api/workflow-runs/{runs.TaskRunId}/cancel", new CancelWorkflowRunRequest("listing test"));
            cancel.StatusCode.Should().Be(HttpStatusCode.NoContent);
            await host.WaitForAsync(runs.TaskRunId, r => r.Status == WorkflowStatus.Cancelled, "been cancelled");

            var awaiting = await client.GetFromJsonAsync<WorkflowRunList>("/api/workflow-runs?status=Awaiting");
            awaiting!.Runs.Select(r => r.Id).Should().Equal(runs.IntentRunId);
            awaiting.Runs[0].Status.Should().Be("Awaiting");

            var first = await client.GetFromJsonAsync<WorkflowRunList>("/api/workflow-runs?take=1");
            first!.Runs.Select(r => r.Id).Should().Equal(runs.IntentRunId);
            first.Next.Should().NotBeNull();

            var second = await client.GetFromJsonAsync<WorkflowRunList>($"/api/workflow-runs?take=1&after={first.Next}");
            second!.Runs.Select(r => r.Id).Should().Equal(runs.TaskRunId);
            second.Next.Should().BeNull();
        });
    }

    /// <summary>
    ///     Phase 2.13: signal and updatedSince filter on the server, against the real store. Both runs park at the human
    ///     gate, so signal=human_approval lists both and another signal none, each with no cursor. Then the older run, the
    ///     task's, is cancelled: updatedSince just before that lists it alone with take=1, though the newer run comes first
    ///     in the store's order, so the list read past a page that matched nothing.
    ///     Red: ignore signal; other_signal lists both runs.
    ///     Red: filter only the first store page; updatedSince with take=1 lists nothing.
    ///     Red: compare CreatedAt for updatedSince; the cancelled run is left out.
    ///     Red: ignore updatedSince; the newer, unchanged run is listed.
    /// </summary>
    [Fact]
    public async Task Signal_and_updated_since_filter_on_the_server()
    {
        await WithTwoRunsAsync(async (host, client, runs) =>
        {
            var gate = await client.GetFromJsonAsync<WorkflowRunList>("/api/workflow-runs?status=Awaiting&signal=human_approval");
            gate!.Runs.Select(r => r.Id).Should().Equal(runs.IntentRunId, runs.TaskRunId);
            gate.Next.Should().BeNull();

            var other = await client.GetFromJsonAsync<WorkflowRunList>("/api/workflow-runs?signal=other_signal");
            other!.Runs.Should().BeEmpty();
            other.Next.Should().BeNull();

            await Task.Delay(TimeSpan.FromMilliseconds(100));
            var since = DateTimeOffset.UtcNow;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            var cancel = await client.PostAsJsonAsync($"/api/workflow-runs/{runs.TaskRunId}/cancel", new CancelWorkflowRunRequest("changed late"));
            cancel.StatusCode.Should().Be(HttpStatusCode.NoContent);
            await host.WaitForAsync(runs.TaskRunId, r => r.Status == WorkflowStatus.Cancelled, "been cancelled");

            var changed = await client.GetFromJsonAsync<WorkflowRunList>(
                $"/api/workflow-runs?take=1&updatedSince={Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture))}");
            changed!.Runs.Select(r => r.Id).Should().Equal(runs.TaskRunId);
            changed.Next.Should().BeNull();
        });
    }

    /// <summary>
    ///     An unknown status, take=0, take=101 and a cursor this endpoint did not write are each 400 with a detail.
    ///     A number and a comma list are not names, so they are 400 too.
    ///     Red: drop each guard in turn. Red: return the 400s without a detail.
    ///     Red: restore TryParse+IsDefined, so ?status=1 is 200.
    ///     Red: restore TryParse+IsDefined, so ?status=Awaiting,Succeeded is 200.
    ///     Red: drop the updatedSince guard, so ?updatedSince=last-week is 200.
    ///     Red: parse updatedSince with TryParse, so the ambiguous ?updatedSince=10/08/2026 is 200.
    /// </summary>
    [Theory]
    [InlineData("?status=Parked")]
    [InlineData("?status=1")]
    [InlineData("?status=Awaiting,Succeeded")]
    [InlineData("?take=0")]
    [InlineData("?take=101")]
    [InlineData("?after=not-a-cursor")]
    [InlineData("?updatedSince=last-week")]
    [InlineData("?updatedSince=10/08/2026")]
    public async Task Bad_queries_are_400(string query)
    {
        await using var host = await ScratchWorkflowHost.StartAsync(fixture, Substitute.For<IAgentRuntime>());
        using var client = host.Client(Starter, "developer");

        var response = await client.GetAsync($"/api/workflow-runs{query}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<ProblemDetails>())!.Detail.Should().NotBeNullOrWhiteSpace();
    }

    private sealed record StartedRuns(Daedalus.Domain.Entities.Task Task, Guid TaskRunId, Guid IntentRunId, string Sentinel);

    /// <summary>
    ///     Boots a host with the scripted runtime and starts two runs, both parked at the gate before
    ///     <paramref name="body"/> runs: first one through <c>POST /api/tasks/{id}/manufacture</c> on a board task, built the
    ///     way <c>TaskManufactureEndToEndTests</c> builds one, then one through <c>POST /api/workflow-runs</c> whose work
    ///     intent holds a sentinel no response may carry.
    /// </summary>
    private async Task WithTwoRunsAsync(Func<ScratchWorkflowHost, HttpClient, StartedRuns, Task> body)
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
        var task = IntegrationTestFactory.CreateTask(projectId: project.Id, taskId: TaskCode);
        project.AddTask(task).IsSuccess.Should().BeTrue();
        await using (var db = await host.Factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync())
        {
            db.Projects.Add(project);
            await db.SaveChangesAsync();
        }

        using var client = host.Client(Starter, "developer");

        var fromTask = await client.PostAsync($"/api/tasks/{task.Id}/manufacture", content: null);
        fromTask.StatusCode.Should().Be(HttpStatusCode.Created);
        var taskRunId = (await fromTask.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        await host.WaitForAsync(taskRunId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

        var sentinel = $"zqx-intent-{Guid.NewGuid():N}";
        var fromIntent = await client.PostAsJsonAsync(
            "/api/workflow-runs", new StartWorkflowRunRequest($"add a health check endpoint {sentinel}", ScratchWorkflowHost.Repository));
        fromIntent.StatusCode.Should().Be(HttpStatusCode.Created);
        var intentRunId = (await fromIntent.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        await host.WaitForAsync(intentRunId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

        await body(host, client, new StartedRuns(task, taskRunId, intentRunId, sentinel));
    }
}
