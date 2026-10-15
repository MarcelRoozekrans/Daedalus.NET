using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Git;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>The run view and the run list say why a run failed, and the list carries the pull request link.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class RunFailureReasonEndpointTests(PostgresFixture fixture)
{
    private const string Reason = "the sandbox went away";

    /// <summary>
    ///     A failed run's reason is its host error on the view and on its list item; a run that did not fail has no reason on
    ///     either, and a published run's list item carries the pull request link its view does, while an unpublished one has none.
    ///     Red: pass null for FailureReason in ToViewAsync. Red: pass null for it in the list item.
    ///     Red: fill the list item's reason for every status, not only Failed. Red: pass null for PrUrl in the list item.
    /// </summary>
    [Fact]
    public async Task The_run_view_and_list_carry_the_failure_reason_and_pr_link()
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
        using var client = host.Client("a-developer", "developer");

        var failedId = await StartAsync(client);
        await host.WaitForAsync(failedId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");
        await host.Store.FailAsync(failedId, Reason, CancellationToken.None);

        var publishedId = await StartAsync(client);
        await host.WaitForAsync(publishedId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");
        var resume = await client.PostAsJsonAsync(
            $"/api/workflow-runs/{publishedId}/resume",
            new { signal = "human_approval", payload = (string?)null, applyStandingInstructions = true });
        resume.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await host.WaitForAsync(publishedId, r => r.Status == WorkflowStatus.Succeeded, "succeeded");

        var failedView = await client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{failedId}");
        var publishedView = await client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{publishedId}");
        var list = await client.GetFromJsonAsync<WorkflowRunList>("/api/workflow-runs");
        var failedItem = list!.Runs.Single(r => r.Id == failedId);
        var publishedItem = list.Runs.Single(r => r.Id == publishedId);

        failedView!.FailureReason.Should().Be(Reason);
        failedItem.FailureReason.Should().Be(Reason);
        failedItem.PrUrl.Should().BeNull("a run that never published has no pull request");
        publishedView!.FailureReason.Should().BeNull();
        publishedItem.FailureReason.Should().BeNull("only a failed run has a reason");
        publishedItem.PrUrl.Should().Be(new Uri(FakePullRequestPublisher.Url)).And.Be(publishedView.PrUrl);
    }

    private static async Task<Guid> StartAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/workflow-runs", new StartWorkflowRunRequest("add a health check endpoint", ScratchWorkflowHost.Repository));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
    }
}
