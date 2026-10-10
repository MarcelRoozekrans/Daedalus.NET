using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Thalos;
using Thalos.Git;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.8, amendment A4: a workflow-enabled host writes the missing <c>node-usage</c> records at startup, from the
///     usage the real Thalos store recorded on each completion event. The run is walked to the gate by the host's own
///     outbox poller over the real <c>processes/manufacture.yaml</c>. Only the model, <see cref="ScriptedManufactureRuntime"/>,
///     is replaced.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class NodeUsageBackfillTests(PostgresFixture fixture)
{
    private static readonly TurnUsage ImplementUsage =
        new(InputTokens: 1_200, OutputTokens: 80, ModelId: "scripted-model") { CacheReadTokens = 900, CacheWriteTokens = 150 };

    /// <summary>
    ///     A run from before phase 2.8 has events with usage but no records. One restart writes one record per
    ///     completion with usage, with the event's own counts and time. A second restart writes nothing.
    ///     Red: unregister <c>NodeUsageBackfill</c>; the first restart leaves no records.
    ///     Red: drop the <c>recorded.Add(seq)</c> skip and the unique index together; the second restart doubles the records.
    ///     The index alone is pinned by <c>NodeUsageUniqueIndexTests</c>, since the skip masks it here.
    ///     Red: stamp records with <c>recorder.UtcNow</c> instead of the event's <c>CreatedAt</c>; the time assertion fails.
    ///     Red: pass <c>usage with { CacheReadTokens = 0 }</c>; the payload assertion fails.
    /// </summary>
    [Fact]
    public async Task A_restart_backfills_each_completion_once_from_the_runs_events()
    {
        await using var host = await StartHostAsync();
        using var client = host.Client("a-developer", "developer");
        var start = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("add a health check endpoint", ScratchWorkflowHost.Repository));
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

        var completions = (await host.Factory.Services.GetRequiredService<IWorkflowRunHistory>().ListEventsAsync(runId, CancellationToken.None))
            .Where(e => e.Usage is not null && e.FromNode is not null)
            .ToList();
        completions.Should().Contain(e => string.Equals(e.FromNode, "implement", StringComparison.Ordinal), "the scripted implement turn reports usage");

        // What a run that finished before phase 2.8 looks like: events with usage, and no node-usage records.
        await ExecuteAsync(host.ConnectionString, $"DELETE FROM \"WorkflowRunRecords\" WHERE \"Kind\" = '{WorkflowRunRecord.NodeUsageKind}'");
        (await host.RecordsAsync(runId, WorkflowRunRecord.NodeUsageKind)).Should().BeEmpty();

        await host.RestartAsync(configureServices: null);
        var backfilled = await host.RecordsAsync(runId, WorkflowRunRecord.NodeUsageKind);

        backfilled.Select(r => (r.Seq, r.Node)).Should().BeEquivalentTo(completions.Select(e => (e.Seq, e.FromNode!)));
        var implement = backfilled.Single(r => string.Equals(r.Node, "implement", StringComparison.Ordinal));
        NodeUsage.FromPayloadJson(implement.PayloadJson).Value.Should().Be(new NodeUsage("scripted-model", 1_200, 80, 900, 150));
        implement.CreatedAt.Should().BeCloseTo(completions.Single(e => string.Equals(e.FromNode, "implement", StringComparison.Ordinal)).CreatedAt.UtcDateTime, TimeSpan.FromMilliseconds(1));
        implement.StartedById.Should().Be("a-developer");

        await host.RestartAsync(configureServices: null);
        (await host.RecordsAsync(runId, WorkflowRunRecord.NodeUsageKind)).Should().HaveCount(backfilled.Count, "every completion is already recorded");

        var backfill = host.Factory.Services.GetServices<IHostedService>().OfType<NodeUsageBackfill>().Single();
        (await backfill.BackfillAsync(CancellationToken.None)).Should().Be(0, "a further pass finds every completion recorded");
    }

    /// <summary>
    ///     The live recorder and the backfill key a record on the same (run, seq): the dispatch's seq is the seq of the
    ///     completion event the backfill reads. So a restart after a live run writes nothing.
    ///     Red: pass <c>seq + 1000</c> to <c>RecordAsync</c> in <c>ReviewHandoffWorkflowStore.CompleteNodeAsync</c>; the
    ///     seq assertion fails. With that assertion removed, the same change makes the restart backfill a second record
    ///     per completion, and the count assertion fails: 3 expected, 6 found.
    ///     The final <c>Be(0)</c> has no single-change red: the backfill's <c>recorded.Add(seq)</c> skip and the
    ///     recorder's unique-index catch each keep it at 0 alone. It documents the expected outcome; the seq assertion
    ///     is the one that guards the shared key.
    /// </summary>
    [Fact]
    public async Task A_restart_after_a_live_run_backfills_nothing()
    {
        await using var host = await StartHostAsync();
        using var client = host.Client("a-developer", "developer");
        var start = await client.PostAsJsonAsync("/api/workflow-runs", new StartWorkflowRunRequest("add a health check endpoint", ScratchWorkflowHost.Repository));
        start.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = (await start.Content.ReadFromJsonAsync<StartWorkflowRunResponse>())!.RunId;
        await host.WaitForAsync(runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

        var completions = (await host.Factory.Services.GetRequiredService<IWorkflowRunHistory>().ListEventsAsync(runId, CancellationToken.None))
            .Where(e => e.Usage is not null && e.FromNode is not null)
            .Select(e => e.Seq)
            .ToList();
        var live = await host.RecordsAsync(runId, WorkflowRunRecord.NodeUsageKind);
        live.Should().NotBeEmpty("the live recorder wrote a record for each completion with usage");
        live.Select(r => r.Seq).Should().BeEquivalentTo(completions, "a live record carries its completion event's seq");

        await host.RestartAsync(configureServices: null);

        (await host.RecordsAsync(runId, WorkflowRunRecord.NodeUsageKind)).Should().HaveCount(live.Count, "the backfill found every completion recorded");
        var backfill = host.Factory.Services.GetServices<IHostedService>().OfType<NodeUsageBackfill>().Single();
        (await backfill.BackfillAsync(CancellationToken.None)).Should().Be(0);
    }

    /// <summary>
    ///     Hosted services start in registration order, so the backfill finishes before the outbox poller can complete a
    ///     node and append the same (run, seq). Red: register <c>NodeUsageBackfill</c> after <c>WorkflowOutboxDispatchService</c>.
    ///     Red: set <c>HostOptions.ServicesStartConcurrently</c> to true in the host's configuration.
    /// </summary>
    [Fact]
    public async Task The_backfill_starts_before_the_outbox_dispatcher()
    {
        await using var host = await StartHostAsync();

        var started = host.Factory.Services.GetServices<IHostedService>().Select(s => s.GetType()).ToList();

        started.IndexOf(typeof(NodeUsageBackfill)).Should().BeGreaterThanOrEqualTo(0)
            .And.BeLessThan(started.IndexOf(typeof(WorkflowOutboxDispatchService)));
        host.Factory.Services.GetRequiredService<IOptions<HostOptions>>().Value.ServicesStartConcurrently
            .Should().BeFalse("registration order is start order only while services start one after another");
    }

    /// <summary>
    ///     A host with the engine off has no workflow tables to read, so it registers no backfill.
    ///     Red: register <c>NodeUsageBackfill</c> in <c>AddDaedalusAgents</c> outside the <c>Enabled</c> block.
    /// </summary>
    [Fact]
    public async Task A_host_with_the_engine_off_registers_no_backfill()
    {
        await using var factory = new ApiWebApplicationFactory(fixture.ConnectionString, Substitute.For<IAgentRuntime>());

        factory.Services.GetServices<IHostedService>().OfType<NodeUsageBackfill>().Should().BeEmpty();
    }

    private Task<ScratchWorkflowHost> StartHostAsync()
    {
        var pullRequests = new FakePullRequestPublisher();
        return ScratchWorkflowHost.StartAsync(
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
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
