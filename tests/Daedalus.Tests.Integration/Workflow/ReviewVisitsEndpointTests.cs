using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>Phase 2.13 task 3: the run view lists every review visit with implement's answer to it.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class ReviewVisitsEndpointTests(PostgresFixture fixture)
{
    /// <summary>
    ///     A run whose review rejects on every visit, and whose implement answers each rejection, is driven through the real
    ///     engine, lens runner and store to review's cap. Its view lists all five review visits newest first, each rejected by
    ///     the scripted lens with that lens's finding, and pairs each with the response the following implement visit reported:
    ///     review visit k with implement visit k + 1. The reviewer's own task text never carries a response.
    ///     Red: pass [] for ReviewVisits in ToViewAsync, and the count assertion fails.
    ///     Red: drop the recorder call from ReviewHandoffWorkflowStore.CompleteNodeAsync, and every response is null.
    ///     Red: pair each visit with the response at the latest seq after it, and the four older visits read none.
    ///     Red: add ReviewResponseKey to ReviewHandoff.ReviewReads, and the review task text carries a response.
    ///     Red: map the findings of the latest-visit Checked list to [], and its rejecting lens has none.
    /// </summary>
    [Fact]
    public async Task The_run_view_lists_review_visits()
    {
        var runtime = new ScriptedManufactureRuntime(rejectReview: true, respondToReview: true);
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime,
            seed: [("README.md", "usage"), ("AGENT.md", "Run dotnet test.\n")],
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
            });
        using var client = host.Client("a-developer", "developer");

        var started = await client.PostAsJsonAsync(
            "/api/workflow-runs", new StartWorkflowRunRequest("add a health check endpoint", ScratchWorkflowHost.Repository));
        started.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await started.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Failed, "failed at the review limit");

        var view = await client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");

        view!.ReviewVisitsError.Should().BeNull();
        view.ReviewVisits.Should().HaveCount(5);
        view.ReviewVisits.Select(v => v.Seq).Should().BeInDescendingOrder();
        var reviewSeqs = view.Steps
            .Where(s => string.Equals(s.FromNode, "review", StringComparison.Ordinal) && string.Equals(s.Outcome, "rejected", StringComparison.Ordinal)).Select(s => s.Seq).OrderDescending();
        view.ReviewVisits.Select(v => v.Seq).Should().Equal(reviewSeqs, "each visit is named by the seq its review node ran at");
        view.ReviewVisits.Should().AllSatisfy(v =>
        {
            v.Verdict.Should().Be("Rejected");
            v.RejectingLens.Should().Be(ScriptedManufactureRuntime.RejectingLens);
            v.Lenses.Should().ContainSingle().Which.Findings.Should().ContainSingle()
                .Which.Should().Be(new ReviewFindingView("src/Daedalus.Infrastructure/Persistence/TaskRepository.cs", 42, "cancelled rows are still claimed"));
        });
        view.Checked.Should().ContainSingle().Which.Findings.Should().Equal(view.ReviewVisits[0].Lenses[0].Findings);
        view.ReviewVisits.Select(v => v.ReviewResponse).Should().Equal(
            Enumerable.Range(1, 5).Reverse().Select(k => ScriptedManufactureRuntime.ResponseFor(k + 1)));
        runtime.TaskFor("review").Should().NotContain("addressed:", "the reviewer's dispatch never carries implement's answer");
    }
}
