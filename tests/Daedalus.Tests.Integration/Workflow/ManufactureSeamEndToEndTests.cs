using System.Collections.Concurrent;
using System.Data.Async.Adapters;
using System.Text.Json;
using Daedalus.Agents.Tools;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Thalos;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.Authorization;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Final review finding I1: the B3 to B4 to B5 seam, end to end, in one booted host. A run is started over
///     <c>POST /api/workflow-runs</c>, so through the registered <see cref="ManufactureRunStarter"/>, with a
///     non-empty standing-instructions file under the host's content root. The host's own outbox poller then
///     walks the real <c>processes/manufacture.yaml</c> through implement, review, retrospect and the gate. Every
///     component is the shipped one except <see cref="IAgentRuntime"/>, which <see cref="ScriptedRuntime"/>
///     replaces so each node reports a scripted outcome. Nothing else in the suite walked version 5 to
///     <c>Succeeded</c>, asserted the pinned <c>standing_instructions</c> document, or reached the resume
///     endpoint's stale-file 409 or write-failure 500 with a real pinned text.
/// </summary>
/// <remarks>
///     Each test gets its own database and host, the same shape as <c>StartRunEndpointTests</c>. The outbox poll
///     interval is cut from five seconds to a quarter of one, so a walk of five dispatches takes seconds rather
///     than half a minute. The standing-instructions file lives in a <see cref="TempDirectory"/> under the Api
///     project's git-ignored <c>obj</c> folder, because <c>AddDaedalusAgents</c> refuses a path outside the
///     content root.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ManufactureSeamEndToEndTests(PostgresFixture fixture)
{
    private const string Signal = "human_approval";

    private const string StandingInstructions = "Run dotnet build before dotnet test.\n";

    private const string LearnedLine = "Integration tests need Docker running.";

    private static readonly TimeSpan WalkTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    ///     The whole seam, happy path: the file's text is pinned at start and handed to implement and retrospect,
    ///     GET shows retrospect's proposal as a diff against it, and a resume with
    ///     <c>applyStandingInstructions</c> writes the proposal and lets the run finish.
    /// </summary>
    [Fact]
    public async Task A_run_pins_the_standing_instructions_and_an_approved_proposal_is_written_on_the_way_to_success()
    {
        await WithHostAsync(squadEnabled: null, async (host) =>
        {
            await File.WriteAllTextAsync(host.Dir.Path("AGENT.md"), StandingInstructions);

            var runId = await StartAsync(host);
            var parked = await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

            parked.Manifest.Should().NotBeNull("a run started through ManufactureRunStarter is always pinned");
            parked.Manifest!.Documents.Should().ContainKey(ManufactureRunStarter.StandingInstructionsDocument)
                .WhoseValue.Should().Be(StandingInstructions, "the file's text at start is what the run pins");
            host.Runtime.TaskFor("implement").Should().Contain(StandingInstructions.TrimEnd(),
                "implement is meant to follow the pinned instructions");
            host.Runtime.TaskFor("retrospect").Should().Contain(StandingInstructions.TrimEnd(),
                "retrospect proposes against the pinned instructions");

            var view = await host.Client.GetFromJsonAsync<WorkflowRunView>($"/api/workflow-runs/{runId}");
            view!.StandingInstructionsDiff.Should().Contain("+" + LearnedLine,
                "GET must show a human what the apply would write, before they decide");

            var resume = await ResumeWithApplyAsync(host, runId);
            resume.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await File.ReadAllTextAsync(host.Dir.Path("AGENT.md"))).Should().Be(StandingInstructions + LearnedLine + "\n");

            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Succeeded, "succeeded");
            host.Runtime.Nodes.Should().Contain("publish", "the run must have walked through publish to reach done");
        });
    }

    /// <summary>
    ///     A human edits the file after the run started. The apply must refuse with 409 rather than overwrite that
    ///     edit with a proposal made against the older text, and the run must stay at the gate.
    /// </summary>
    [Fact]
    public async Task An_apply_after_the_file_was_edited_since_the_run_started_is_refused_with_409()
    {
        await WithHostAsync(squadEnabled: null, async (host) =>
        {
            await File.WriteAllTextAsync(host.Dir.Path("AGENT.md"), StandingInstructions);

            var runId = await StartAsync(host);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

            const string humanEdit = "Run dotnet build before dotnet test.\nA human added this line.\n";
            await File.WriteAllTextAsync(host.Dir.Path("AGENT.md"), humanEdit);

            var resume = await ResumeWithApplyAsync(host, runId);

            resume.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await File.ReadAllTextAsync(host.Dir.Path("AGENT.md"))).Should().Be(humanEdit, "the human's edit must survive");
            var run = await host.Store.FindAsync(runId, CancellationToken.None);
            run!.Status.Should().Be(WorkflowStatus.Awaiting, "a refused apply must never reach the engine's resume");
        });
    }

    /// <summary>
    ///     The write itself fails: a directory now sits where the file should go. The run started with no file,
    ///     so the pinned text is empty and the staleness check passes. The rename then fails, and the endpoint
    ///     must answer 500, a filesystem fault, not 409.
    /// </summary>
    [Fact]
    public async Task An_apply_whose_write_fails_answers_500_and_leaves_the_run_at_the_gate()
    {
        await WithHostAsync(squadEnabled: null, async (host) =>
        {
            var runId = await StartAsync(host);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

            Directory.CreateDirectory(host.Dir.Path("AGENT.md"));

            var resume = await ResumeWithApplyAsync(host, runId);

            resume.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var run = await host.Store.FindAsync(runId, CancellationToken.None);
            run!.Status.Should().Be(WorkflowStatus.Awaiting, "a failed write must never reach the engine's resume");
            Directory.GetFiles(host.Dir.Root, "*.tmp").Should().BeEmpty("the writer must clean up its temp file");
        });
    }

    /// <summary>
    ///     Task B4 relaxed the squad-off test from <c>Succeeded</c> to <c>Awaiting</c>. This restores the full
    ///     walk: with the squad off, every node runs as the fallback agent and the run still reaches
    ///     <c>Succeeded</c> once a human resumes the gate without applying anything.
    /// </summary>
    [Fact]
    public async Task With_the_squad_off_the_run_still_walks_to_success_as_the_fallback_agent()
    {
        await WithHostAsync(squadEnabled: false, async (host) =>
        {
            var runId = await StartAsync(host);
            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Awaiting, "parked at the gate");

            var resume = await host.Client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume", new { signal = Signal, payload = (string?)null });
            resume.StatusCode.Should().Be(HttpStatusCode.NoContent);

            await WaitForAsync(host, runId, r => r.Status == WorkflowStatus.Succeeded, "succeeded");

            var catalog = host.Factory.Services.GetRequiredService<IAgentCatalog>();
            var fallback = catalog.Agents.Single(a => string.Equals(a.Name, "Daedalus Architect", StringComparison.Ordinal)).Id;
            host.Runtime.AgentsByNode.Keys.Should().BeEquivalentTo(["implement", "review", "retrospect", "publish"]);
            host.Runtime.AgentsByNode.Values.Should().OnlyContain(id => id == fallback,
                "with the squad off, implement, review and retrospect all run as the fallback agent");
        });
    }

    private sealed record Host(
        ApiWebApplicationFactory Factory, HttpClient Client, IWorkflowStore Store, ScriptedRuntime Runtime, TempDirectory Dir);

    private static async Task<Guid> StartAsync(Host host)
    {
        var response = await host.Client.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "add a health check endpoint" });
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>();
        return body!.RunId;
    }

    private static Task<HttpResponseMessage> ResumeWithApplyAsync(Host host, Guid runId) =>
        host.Client.PostAsJsonAsync(
            $"/api/workflow-runs/{runId}/resume",
            new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });

    /// <summary>
    ///     Polls the run until <paramref name="until"/> holds, or it fails, or <see cref="WalkTimeout"/> passes.
    ///     The assertion names where the run stopped, so a timeout says which node it stuck on and why.
    /// </summary>
    private static async Task<WorkflowRun> WaitForAsync(Host host, Guid runId, Func<WorkflowRun, bool> until, string what)
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
        return run;
    }

    /// <summary>
    ///     Creates a throwaway, fully migrated database, boots the Api against it with the workflow engine on, a
    ///     scripted runtime, a fast outbox poll and the standing-instructions file under the content root, runs
    ///     <paramref name="body"/>, and tears everything down.
    /// </summary>
    private async Task WithHostAsync(bool? squadEnabled, Func<Host, Task> body)
    {
        var dbName = $"manufacture_seam_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;

        var relative = TempDirectory.NewContentRootRelative();
        ApiWebApplicationFactory? factory = null;
        try
        {
            await using (var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString)))
            {
                await db.Database.ExecuteSqlRawAsync("CREATE EXTENSION IF NOT EXISTS vector");
                await db.Database.MigrateAsync();
            }

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                var asyncConnection = connection.AsAsync();
                var dialect = new PostgresMigrationDialect();
                await new MigrationRunner(asyncConnection, OutboxOrmMigrations.Postgres, dialect).RunAsync();
                await new MigrationRunner(asyncConnection, WorkflowOrmMigrations.Postgres, dialect).RunAsync();
            }

            var runtime = new ScriptedRuntime();
            factory = new ApiWebApplicationFactory(
                connectionString, runtime, workflowEnabled: true,
                standingInstructionsPath: Path.Combine(relative, "AGENT.md"),
                squadEnabled: squadEnabled,
                configureServices: services =>
                {
                    services.RemoveAll<WorkflowOutboxDispatchOptions>();
                    services.AddSingleton(new WorkflowOutboxDispatchOptions { PollingInterval = TimeSpan.FromMilliseconds(250) });
                });

            // Force the host to build and start now, so the process and skill syncs have run before any request.
            _ = factory.Services;

            var contentRoot = factory.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
            using var dir = new TempDirectory(Path.Combine(contentRoot, relative));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, "a-developer");
            client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, "developer");

            await body(new Host(factory, client, factory.Services.GetRequiredService<IWorkflowStore>(), runtime, dir));
        }
        finally
        {
            if (factory is not null)
            {
                await factory.DisposeAsync();
            }

            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)");
        }
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    ///     Stands in for the model. Each workflow turn answers the way its shipped skill instructs, through the
    ///     outcome tool the engine offered: implement reports <c>changed</c> with a <c>learnings</c> entry, each
    ///     review lens pass reports evidence and <c>approved</c>, retrospect proposes the pinned text plus one
    ///     learned line, and publish reports <c>published</c>. It records each node's task text and agent.
    /// </summary>
    private sealed class ScriptedRuntime : IAgentRuntime
    {
        private static readonly string[] Lenses = ["correctness", "falsifiability", "mechanism"];

        private readonly ConcurrentDictionary<SessionId, AgentId> _sessions = new();
        private readonly ConcurrentDictionary<string, string> _tasks = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AgentId> _agents = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<Guid, int> _lensPasses = new();

        public IReadOnlyDictionary<string, AgentId> AgentsByNode => _agents;

        public IReadOnlyCollection<string> Nodes => [.. _tasks.Keys];

        public string TaskFor(string node) => _tasks.TryGetValue(node, out var task) ? task : "";

        public ValueTask<Result<SessionId, AgentError>> CreateSessionAsync(AgentId agentId, ISecurityContext caller, CancellationToken ct = default)
        {
            var session = new SessionId(Guid.NewGuid());
            _sessions[session] = agentId;
            return ValueTask.FromResult(Result<SessionId, AgentError>.Success(session));
        }

        public ValueTask<UnitResult<AgentError>> CloseSessionAsync(SessionId sessionId, ISecurityContext caller, CancellationToken ct = default)
        {
            _sessions.TryRemove(sessionId, out _);
            return ValueTask.FromResult(UnitResult<AgentError>.Success());
        }

        public IAsyncEnumerable<AgentEvent> RunTurnStreamingAsync(AgentTurnRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException("The workflow path runs buffered turns only.");

        public ValueTask<Result<AgentTurnResult, AgentError>> RunTurnAsync(AgentTurnRequest request, CancellationToken ct = default)
        {
            if (request.Caller is not WorkflowCaller caller || request.RequiredOutcome is null)
            {
                return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Failure(
                    AgentError.Validation("ScriptedRuntime only answers workflow turns that require an outcome.")));
            }

            var run = caller.Run;
            var node = run.CurrentNode;
            _tasks[node] = request.Text;
            if (_sessions.TryGetValue(request.SessionId, out var agent))
            {
                _agents[node] = agent;
            }

            var tool = request.RequiredOutcome.ToolName;
            IReadOnlyList<ToolCallSummary> calls = node switch
            {
                "implement" => [Outcome(tool, "changed", new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [ReviewHandoff.SummaryKey] = "filtered cancelled tasks",
                    [ReviewHandoff.FilesTouchedKey] = "src/Daedalus.Infrastructure/Persistence/TaskRepository.cs",
                    [ReviewHandoff.RationaleKey] = "the claim query ignored status",
                    [ReviewHandoff.LearningsKey] = new[] { LearnedLine },
                })],
                "review" => ReviewPass(tool, Lenses[_lensPasses.AddOrUpdate(run.Id, 0, (_, n) => n + 1) % Lenses.Length]),
                "retrospect" => [Outcome(tool, ReviewHandoff.RetrospectProposedOutcome, new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [ReviewHandoff.ProposedStandingInstructionsKey] = Pinned(run) + LearnedLine + "\n",
                })],
                "publish" => [Outcome(tool, "published", null)],
                _ => [],
            };

            return ValueTask.FromResult(Result<AgentTurnResult, AgentError>.Success(
                new AgentTurnResult(TurnId.New(), request.SessionId, $"{node} done", default, calls, TimeSpan.FromMilliseconds(5))));
        }

        private static string Pinned(WorkflowRun run) =>
            run.Manifest is not null && run.Manifest.Documents.TryGetValue(ManufactureRunStarter.StandingInstructionsDocument, out var text)
                ? text
                : "";

        private static ToolCallSummary[] ReviewPass(string tool, string lens) =>
        [
            new ToolCallSummary(
                ToolCallId.New(),
                DaedalusReviewTools.QualifiedReportReviewOutcomeToolName,
                JsonSerializer.Serialize(new
                {
                    lens,
                    verdict = "approved",
                    @checked = """["TaskRepository.ClaimNextAsync now filters cancelled rows"]""",
                }),
                Succeeded: true, "Recorded", TimeSpan.FromMilliseconds(1)),
            Outcome(tool, "approved", null),
        ];

        private static ToolCallSummary Outcome(string tool, string outcome, Dictionary<string, object?>? variables) =>
            new(
                ToolCallId.New(),
                tool,
                variables is null
                    ? JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal) { [OutcomeToolSchema.ArgumentName] = outcome })
                    : JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        [OutcomeToolSchema.ArgumentName] = outcome,
                        ["variables"] = variables,
                    }),
                Succeeded: true, "ok", TimeSpan.FromMilliseconds(1));
    }
}
