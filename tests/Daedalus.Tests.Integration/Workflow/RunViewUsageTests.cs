using System.Text.Json;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Thalos;
using Thalos.Git;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;
using WorkflowRunRecord = Daedalus.Domain.Entities.WorkflowRunRecord;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B15: <c>GET /api/workflow-runs/{id}</c> shows each node's token and cache usage, the run's starter, its
///     pull request URL and its write audit. Each run is started over <c>POST /api/workflow-runs</c> as
///     <c>a-developer</c> on a <see cref="ScratchWorkflowHost"/>, and the host's own outbox poller walks the real
///     <c>processes/manufacture.yaml</c>. Only the model, <see cref="ScriptedManufactureRuntime"/>, and the pull
///     request host, <see cref="FakePullRequestPublisher"/>, are replaced.
/// </summary>
/// <remarks>
///     Every test waits for its run to park at the gate before it reads the view, so no host is torn down while its
///     poller is still dispatching the run.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class RunViewUsageTests(PostgresFixture fixture)
{
    private const string Signal = "human_approval";

    private const string Starter = "a-developer";

    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(90);

    /// <summary>What the scripted provider reports for the implement turn: distinct, non-zero counts in every field.</summary>
    private static readonly TurnUsage ImplementUsage =
        new(InputTokens: 1_200, OutputTokens: 80, ModelId: "scripted-model") { CacheReadTokens = 900, CacheWriteTokens = 150 };

    /// <summary>
    ///     A run that has passed implement shows one usage entry for it, with the counts the provider reported, read
    ///     off the node's completion event. Red per assertion: mapping <c>NodeUsage</c> from the run's variables
    ///     instead of its events gives an empty list, which fails the single-entry assertion; reverting B1's
    ///     <c>{ Usage = result.Usage }</c> in <c>WorkflowRunModeStore.CompleteNodeAsync</c> stores the implement event
    ///     with no usage, so it has no entry either; and dropping the cache counts in the mapping fails the equality.
    /// </summary>
    [Fact]
    public async Task A_run_past_implement_shows_implements_usage_with_the_providers_cache_counts()
    {
        await WithHostAsync(async host =>
        {
            var runId = await StartAsync(host);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

            var view = await host.Client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");

            var implement = view!.NodeUsage.Should().ContainSingle(u => u.Node == "implement").Subject;
            implement.Should().BeEquivalentTo(new
            {
                InputTokens = 1_200,
                OutputTokens = 80,
                CacheReadTokens = 900,
                CacheWriteTokens = 150,
                ModelId = "scripted-model",
            });
        });
    }

    /// <summary>
    ///     The view names who started the run. Red (ruling R16): mapping <c>StartedBy</c> from
    ///     <c>run.Variables["started_by"]</c>, or leaving it null, reads null, because the run has no such variable.
    /// </summary>
    [Fact]
    public async Task The_view_names_the_developer_who_started_the_run()
    {
        await WithHostAsync(async host =>
        {
            var runId = await StartAsync(host);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

            var view = await host.Client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");

            view!.StartedBy.Should().Be(Starter);
        });
    }

    /// <summary>
    ///     The pull request URL is the run's <c>pr_url</c> variable: absent at the gate, before the
    ///     <c>open-pull-request</c> action has run, and the URL the host reported once it has. Red per assertion:
    ///     falling back to any URL when <c>pr_url</c> is absent fails the null assertion; reading a variable other
    ///     than <c>pr_url</c>, or leaving <c>PrUrl</c> null, fails the second.
    /// </summary>
    [Fact]
    public async Task The_pull_request_url_is_null_before_publish_and_the_published_url_after()
    {
        await WithHostAsync(async host =>
        {
            var runId = await StartAsync(host);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

            var atGate = await host.Client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");
            atGate!.PrUrl.Should().BeNull("nothing has been published yet");

            // Applying retrospect's proposal changes AGENT.md, which gives the action a diff to publish.
            var resume = await host.Client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume", new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });
            resume.StatusCode.Should().Be(HttpStatusCode.NoContent);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Succeeded, "succeeded");

            var published = await host.Client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");
            published!.PrUrl.Should().Be(new Uri(FakePullRequestPublisher.Url));
        });
    }

    /// <summary>
    ///     The write audit is the run's workspace-write records, as seq, node, tool, path and starter, in seq order.
    ///     The records are appended through the host's own record store, out of seq order and with a review-evidence
    ///     record between them, because the scripted model calls no tools. Red per assertion: listing records of every
    ///     kind adds the review evidence; ordering by append order instead of seq swaps the two writes; and mapping the
    ///     principal instead of the starter, or dropping the path, fails the field values.
    /// </summary>
    [Fact]
    public async Task The_write_audit_lists_the_runs_write_records_in_seq_order()
    {
        await WithHostAsync(async host =>
        {
            var runId = await StartAsync(host);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");
            var records = host.Factory.Services.GetRequiredService<IWorkflowRunRecordStore>();
            var principal = $"workflow:manufacture:{runId}";

            await AppendAsync(records, runId, 5, "implement", WorkflowRunRecord.WorkspaceWriteKind, principal, new { tool = "workspace__write_file", path = "src/B.cs" });
            await AppendAsync(records, runId, 3, "review", WorkflowRunRecord.ReviewEvidenceKind, principal, new { lens = "correctness", verdict = "approved" });
            await AppendAsync(records, runId, 2, "implement", WorkflowRunRecord.WorkspaceWriteKind, principal, new { tool = "workspace__delete_file", path = (string?)null });

            var view = await host.Client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");

            view!.WriteAudit.Should().Equal(
                new WriteAuditView(2, "implement", "workspace__delete_file", null, Starter),
                new WriteAuditView(5, "implement", "workspace__write_file", "src/B.cs", Starter));
        });
    }

    private static async Task AppendAsync(
        IWorkflowRunRecordStore records, Guid runId, long seq, string node, string kind, string principal, object payload)
    {
        var record = WorkflowRunRecord.Create(
            runId, seq, node, kind, principal, Starter, JsonSerializer.Serialize(payload), DateTime.UtcNow);
        record.IsSuccess.Should().BeTrue(record.IsFailure ? record.Error : null);
        await records.AppendAsync(record.Value, CancellationToken.None);
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
