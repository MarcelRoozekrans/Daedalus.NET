using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Application.DTOs;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.15, end to end in the real composed Api host with only <see cref="IAgentRuntime"/> scripted: an implement
///     turn its token ceiling stopped after spending 155 138 tokens fails the run, and that spend reaches
///     <c>/api/cost-analytics/summary</c>. Before it, a failed node recorded no usage anywhere, so its spend was missing
///     from the costs page. The scripted runtime publishes the turn's <see cref="TurnFailedNotification"/> through the host's
///     own publisher before it returns the failure, as Thalos' runtime does.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FailedNodeCostTests(PostgresFixture fixture)
{
    private static readonly TurnUsage Spent = new(150_000, 5_138, "claude-sonnet-5");

    /// <summary>
    ///     The manufacture source of the cost summary shows the failed implement turn's 155 138 tokens, as one entry, and the
    ///     run still fails once, with the error the turn failed with.
    ///     <para>Red: register the decorator nowhere, by leaving it out of <c>WorkflowNodeDispatcherFactory</c>; the summary has
    ///     no manufacture slice.</para>
    ///     <para>Red: have the decorator change the failure, by returning <c>AgentError.Validation</c> with the error's message
    ///     instead of the error itself; the failed step's error text differs.</para>
    ///     <para>Red: drop the publisher's <c>Replace</c> from <c>AddDaedalusWorkflow</c>; the host resolves Thalos'
    ///     <c>NullAgentNotificationPublisher</c>, and the type assertion fails.</para>
    /// </summary>
    [Fact]
    public async Task A_budget_stopped_implement_turn_is_counted_in_the_cost_summary()
    {
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            new ScriptedManufactureRuntime(failNode: "implement", failUsage: Spent),
            seed: [("README.md", "costs")],
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
            });
        host.Factory.Services.GetRequiredService<IAgentNotificationPublisher>().Should().BeOfType<TurnUsageNotificationPublisher>(
            "the host publishes through Daedalus's publisher, not Thalos' null one");
        using var client = host.Client("a-developer", "developer");

        var response = await client.PostAsJsonAsync(
            "/api/workflow-runs", new StartWorkflowRunRequest("add a health check endpoint", ScratchWorkflowHost.Repository));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Failed, "failed at implement");

        var summary = await client.GetFromJsonAsync<CostSummaryDto>("/api/cost-analytics/summary");
        var view = await client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");

        var manufacture = summary!.BySource.Should().ContainSingle(s => string.Equals(s.Key, CostSources.Manufacture, StringComparison.Ordinal)).Subject;
        (manufacture.InputTokens + manufacture.OutputTokens).Should().Be(155_138);
        manufacture.Entries.Should().Be(1, "one node failed, and it is one entry");
        view!.Steps.Where(s => string.Equals(s.Kind, nameof(WorkflowEventKind.Failed), StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Error.Should().Be(ScriptedManufactureRuntime.BudgetStop.ToString());
    }
}
