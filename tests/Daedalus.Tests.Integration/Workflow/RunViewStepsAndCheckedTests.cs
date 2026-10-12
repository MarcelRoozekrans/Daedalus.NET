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

/// <summary>A run parked at the gate shows its event log as steps and each lens's checked items.</summary>
[Collection(DatabaseCollection.Name)]
public sealed class RunViewStepsAndCheckedTests(PostgresFixture fixture)
{
    private const string Starter = "a-developer";

    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(90);

    private static readonly TurnUsage ImplementUsage =
        new(InputTokens: 1_200, OutputTokens: 80, ModelId: "scripted-model") { CacheReadTokens = 900, CacheWriteTokens = 150 };

    /// <summary>
    ///     The steps start at seq 0, end parked at the gate, and the implement completion carries the scripted tokens.
    ///     Red: pass [] for Steps in ToViewAsync.
    ///     Red: project from the events with usage only: the start and the park are missing.
    /// </summary>
    [Fact]
    public async Task A_parked_run_shows_its_steps()
    {
        await WithHostAsync(async host =>
        {
            var runId = await StartAsync(host);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

            var view = await host.Client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");

            view!.Steps[0].Seq.Should().Be(0);
            view.Steps[^1].Status.Should().Be("Awaiting");
            view.Steps.Should().Contain(s => s.FromNode == "implement" && s.InputTokens == 1_200);
        });
    }

    /// <summary>
    ///     Each lens's checked items appear, read off the latest review visit, and are the ones the scripted lenses reported.
    ///     Red: pass [] for Checked. Red: map Checked from the deferred findings.
    /// </summary>
    [Fact]
    public async Task Each_lens_shows_what_it_checked()
    {
        await WithHostAsync(async host =>
        {
            var runId = await StartAsync(host);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

            var view = await host.Client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");

            view!.Checked.Should().NotBeEmpty();
            view.Checked.Should().OnlyContain(l => l.Checked.Contains(ScriptedManufactureRuntime.ReviewEvidence));
        });
    }

    private sealed record Host(ApiWebApplicationFactory Factory, HttpClient Client, IWorkflowStore Store);

    private static async Task<Guid> StartAsync(Host host)
    {
        var response = await host.Client.PostAsJsonAsync(
            "/api/workflow-runs", new StartWorkflowRunRequest("add a health check endpoint", ScratchWorkflowHost.Repository));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
    }

    /// <summary>Polls the run until <paramref name="until"/> holds, or it fails, or <see cref="WalkTimeout"/> passes.</summary>
    private static async Task WaitForAsync(Host host, Guid runId, Func<WorkflowRun, bool> until, string what)
    {
        var deadline = DateTime.UtcNow + WalkTimeout;
        WorkflowRun? run;
        do
        {
            run = await host.Store.FindAsync(runId, CancellationToken.None);
            if (run is not null && (until(run) || run.Status is WorkflowStatus.Failed or WorkflowStatus.Cancelled))
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
        while (DateTime.UtcNow < deadline);

        run.Should().NotBeNull();
        until(run!).Should().BeTrue(
            $"the run should have {what}, but stopped with status {run!.Status} at '{run.CurrentNode}', last error: {run.LastError}");
    }

    /// <summary>
    ///     Boots a host whose remote's <c>main</c> holds an <c>AGENT.md</c>, with the scripted runtime, a fast outbox poll
    ///     and the fake pull request host, and hands <paramref name="body"/> a client authenticated as <c>a-developer</c>.
    /// </summary>
    private async Task WithHostAsync(Func<Host, Task> body)
    {
        var pullRequests = new FakePullRequestPublisher();
        await using var host = await ScratchWorkflowHost.StartAsync(
            fixture,
            new ScriptedManufactureRuntime(ImplementUsage),
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

        using var client = host.Client(Starter, "developer");
        await body(new Host(host.Factory, client, host.Store));
    }
}
