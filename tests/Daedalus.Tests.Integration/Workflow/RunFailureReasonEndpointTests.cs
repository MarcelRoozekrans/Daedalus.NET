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
    ///     Red: pass null for PrUrl in the list item. Red: derive the list item's reason with the literal status Failed for
    ///     every row, which gives the published run a reason.
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

    /// <summary>
    ///     A run whose review rejects on every visit is capped by the engine at review's five visits and fails at adjudicate.
    ///     Its view and its list item both carry the review-limit reason with the count and the rejecting lens, read off
    ///     the steps and the review-evidence the real engine and lens runner wrote.
    ///     Red: match the cap step by FromNode == review instead of its ToNode, as the first version did.
    ///     Red: pass a null lens to Derive in ToViewAsync, or in ToListItemAsync, for the two assertions respectively.
    /// </summary>
    [Fact]
    public async Task A_run_the_review_limit_stopped_says_so_on_its_view_and_its_list_item()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            new ScriptedManufactureRuntime(rejectReview: true),
            seed: [("README.md", "usage"), ("AGENT.md", "Run dotnet test.\n")],
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
            });
        using var client = host.Client("a-developer", "developer");

        var runId = await StartAsync(client);
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Failed, "failed at the review limit");

        const string expected =
            $"Review rejected the change 5 times, the review limit. Last rejecting lens: {ScriptedManufactureRuntime.RejectingLens}.";
        var view = await client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");
        var list = await client.GetFromJsonAsync<WorkflowRunList>("/api/workflow-runs?status=Failed");

        view!.Steps.Should().Contain(s => s.Kind == "CapExceeded" && s.ToNode == "adjudicate");
        view.FailureReason.Should().Be(expected);
        list!.Runs.Single(r => r.Id == runId).FailureReason.Should().Be(expected);
    }

    private static async Task<Guid> StartAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync(
            "/api/workflow-runs", new StartWorkflowRunRequest("add a health check endpoint", ScratchWorkflowHost.Repository));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
    }
}
