using Daedalus.Agents.Workflow;
using Daedalus.Application.DTOs.WorkflowRuns;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.15: in the real composed Api host, with only <see cref="IAgentRuntime"/> scripted, each workflow agent's
///     turn reaches the model call carrying its own ceiling from <c>Thalos:Workflow:TurnBudgets</c>. The
///     unit tests build <c>BudgetedSubagentRunner</c> by hand; this proves the dispatcher the host composes wraps the real
///     <c>SubagentRunner</c> with it, and that the real <c>SubagentRunner</c> forwards the stamped budget as
///     <see cref="AgentTurnRequest.MaxTotalTokens"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class TurnBudgetStampTests(PostgresFixture fixture)
{
    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    ///     With the squad on, implement runs as the implementer under its own ceiling and review as the reviewer under its
    ///     own. The shipped values are equal, so the host overrides them with distinct ones: a mapping that gave every
    ///     agent the same ceiling would pass on the shipped values alone.
    ///     <para>
    ///     Red: build the dispatcher with the raw <see cref="ISubagentRunner"/> in <c>WorkflowNodeDispatcherFactory</c>; both
    ///     read Thalos' default of 50000. Also red: have <c>TurnBudgetsConfig.For</c> always return <c>Default</c>; review
    ///     then reads 2900000, not 2875000.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Implement_and_review_turns_carry_their_own_ceilings()
    {
        var runtime = new ScriptedManufactureRuntime();
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            runtime,
            seed: [("README.md", "budgets")],
            settings: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["Thalos:Workflow:TurnBudgets:Default"] = "2900000",
                ["Thalos:Workflow:TurnBudgets:Agents:implementer"] = "2900000",
                ["Thalos:Workflow:TurnBudgets:Agents:reviewer"] = "2875000",
            },
            squadEnabled: true,
            configureServices: services =>
            {
                services.RemoveAll<WorkflowOutboxDispatchOptions>();
                services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
            });
        using var client = host.Client("a-developer", "developer");

        var response = await client.PostAsJsonAsync(
            "/api/workflow-runs", new { workIntent = "add a health check endpoint", repository = ScratchWorkflowHost.Repository });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;

        await WaitForGateAsync(host, runId);

        runtime.MaxTotalTokensByNode.Should().ContainKey("implement").WhoseValue.Should().Be(2_900_000);
        runtime.MaxTotalTokensByNode.Should().ContainKey("review").WhoseValue.Should().Be(2_875_000);
    }

    private static async Task WaitForGateAsync(ScratchWorkflowHost host, Guid runId)
    {
        var deadline = DateTime.UtcNow + WalkTimeout;
        WorkflowRun? run;
        do
        {
            run = await host.Store.FindAsync(runId, CancellationToken.None);
            if (run is not null && run.Status is WorkflowStatus.Awaiting or WorkflowStatus.Failed or WorkflowStatus.Cancelled)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        while (DateTime.UtcNow < deadline);

        run.Should().NotBeNull();
        run!.Status.Should().Be(WorkflowStatus.Awaiting,
            $"the run should have parked at the gate, but stopped at '{run.CurrentNode}', last error: {run.LastError}");
    }
}
