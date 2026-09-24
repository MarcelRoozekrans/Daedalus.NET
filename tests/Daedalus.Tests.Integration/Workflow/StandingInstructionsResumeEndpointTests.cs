using System.Data.Async.Adapters;
using Daedalus.Agents.Workflow;
using Daedalus.Api.Controllers;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Thalos;
using Thalos.Skills;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Task B5, end to end over the real REST surface: <c>POST /api/workflow-runs/{id}/resume</c>'s
///     <c>applyStandingInstructions</c> flag, and <c>GET /api/workflow-runs/{id}</c>'s <c>StandingInstructionsDiff</c>.
/// </summary>
/// <remarks>
///     <b>Seeding: a run parked directly at <c>gate</c>, not driven through the full manufacture graph.</b>
///     <c>SquadHandoffEndToEndTests</c> (task B4) proves the model's proposal reaches <c>WorkflowRun.Variables</c>
///     uncut; that plumbing is not this task's to re-prove. What B5 owns is what happens once a run is already
///     sitting at the gate — so each test here starts a small, throwaway two-node process
///     (<c>gate</c> → terminal <c>publish</c>) directly at <c>gate</c>, the same positional
///     <see cref="IWorkflowStore.StartAsync(string,int,string,string,System.Collections.Generic.IReadOnlyDictionary{string,object},System.Threading.CancellationToken)"/>
///     overload <c>ResumeSignalMismatchTests.ParkedAtGateAsync</c> uses, with the proposal (or its absence) set
///     directly as an opening variable. That overload never populates <see cref="WorkflowRun.Manifest"/>, so the
///     pinned standing-instructions text this suite exercises against is always <c>""</c> — every test here writes
///     no initial <c>AGENT.md</c> content (an absent file also reads back as <c>""</c>, per
///     <see cref="StandingInstructionsWriter.ApplyAsync"/>'s own rule), so the pinned/current comparison always
///     matches at the point each test resumes.
///     <para>
///     <b>Seeding uses a standalone store, never the booted host's own.</b> The real host's
///     <c>WorkflowOutboxDispatchService</c> polls continuously once <see cref="ApiWebApplicationFactory"/> exists.
///     Seeding through a throwaway <see cref="OrmWorkflowStore"/>/<see cref="WorkflowNodeDispatcher"/> pair,
///     fully parked at <c>Awaiting</c> before the factory is ever constructed, means that poller never has
///     anything of this suite's to race against.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class StandingInstructionsResumeEndpointTests(PostgresFixture fixture)
{
    private const string ProcessName = "b5-resume-test";

    private const string Signal = "human_approval";

    private const string Yaml = """
        process: b5-resume-test
        version: 1
        nodes:
          gate:
            await: human_approval
            next: publish
          publish:
            terminal: succeeded
        """;

    [Fact]
    public async Task Resuming_without_the_flag_succeeds_and_leaves_the_file_untouched()
    {
        await WithHostAsync(async (factory, connectionString, dir) =>
        {
            var runId = await SeedParkedRunAsync(connectionString, "no-flag", proposal: "Some proposal.");

            using var client = DeveloperClient(factory);
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume", new { signal = Signal, payload = (string?)null });

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            File.Exists(dir.Path("AGENT.md")).Should().BeFalse(
                "applyStandingInstructions defaults to false, so a resume without it must never touch the file");

            var store = factory.Services.GetRequiredService<IWorkflowStore>();
            var run = await store.FindAsync(runId, CancellationToken.None);
            run!.CurrentNode.Should().Be("publish", "the gate's 'next' edge must still resolve when the flag is unset");
        });
    }

    [Fact]
    public async Task Cancelling_a_parked_run_leaves_the_file_untouched()
    {
        await WithHostAsync(async (factory, connectionString, dir) =>
        {
            var runId = await SeedParkedRunAsync(connectionString, "cancel", proposal: "Some proposal.");

            using var client = DeveloperClient(factory);
            var response = await client.PostAsJsonAsync($"/api/workflow-runs/{runId}/cancel", new { reason = "test" });

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            File.Exists(dir.Path("AGENT.md")).Should().BeFalse("cancel must never write the standing-instructions file");
        });
    }

    [Fact]
    public async Task Resuming_with_the_flag_but_no_proposal_is_refused_and_the_run_stays_awaiting()
    {
        await WithHostAsync(async (factory, connectionString, dir) =>
        {
            var runId = await SeedParkedRunAsync(connectionString, "no-proposal", proposal: null);

            using var client = DeveloperClient(factory);
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "retrospect reported no proposal, so there is nothing for a human resume to apply");

            var store = factory.Services.GetRequiredService<IWorkflowStore>();
            var run = await store.FindAsync(runId, CancellationToken.None);
            run!.Status.Should().Be(WorkflowStatus.Awaiting,
                "a refused apply must never reach the engine's own resume — the run is left exactly as found");
            File.Exists(dir.Path("AGENT.md")).Should().BeFalse();
        });
    }

    /// <summary>
    ///     Fix round 1, Important 1: the gate's own status/signal check runs before <c>ApplyAsync</c>, not only
    ///     afterward inside the engine's own resume — otherwise a wrong signal would still write the file and
    ///     only then be refused.
    /// </summary>
    [Fact]
    public async Task Resuming_with_the_flag_and_a_wrong_signal_is_refused_before_writing()
    {
        await WithHostAsync(async (factory, connectionString, dir) =>
        {
            var runId = await SeedParkedRunAsync(connectionString, "wrong-signal", proposal: "Some proposal.");

            using var client = DeveloperClient(factory);
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = "not_the_real_signal", payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "a wrong signal must be refused before the engine ever sees it");
            File.Exists(dir.Path("AGENT.md")).Should().BeFalse(
                "a wrong signal must be refused before any write, not written and then refused");
        });
    }

    /// <summary>
    ///     Fix round 1, Important 1, the other half: a run that is no longer <c>Awaiting</c> at all — here,
    ///     already cancelled — must be refused the same way, before <c>ApplyAsync</c> ever runs.
    /// </summary>
    [Fact]
    public async Task Resuming_with_the_flag_against_an_already_cancelled_run_is_refused_before_writing()
    {
        await WithHostAsync(async (factory, connectionString, dir) =>
        {
            var runId = await SeedParkedRunAsync(connectionString, "already-cancelled", proposal: "Some proposal.");
            await CancelDirectlyAsync(connectionString, runId);

            using var client = DeveloperClient(factory);
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.Conflict,
                "a run that is no longer awaiting must be refused before the engine ever sees it");
            File.Exists(dir.Path("AGENT.md")).Should().BeFalse(
                "an already-cancelled run must be refused before any write, not written and then refused");
        });
    }

    [Fact]
    public async Task Resuming_with_the_flag_and_a_proposal_writes_it_and_succeeds()
    {
        await WithHostAsync(async (factory, connectionString, dir) =>
        {
            const string proposal = "Run dotnet test.\nIntegration needs Docker.";
            var runId = await SeedParkedRunAsync(connectionString, "with-proposal", proposal);

            using var client = DeveloperClient(factory);
            var response = await client.PostAsJsonAsync(
                $"/api/workflow-runs/{runId}/resume",
                new { signal = Signal, payload = (string?)null, applyStandingInstructions = true });

            response.StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await File.ReadAllTextAsync(dir.Path("AGENT.md"))).Should().Be(proposal);
        });
    }

    [Fact]
    public async Task Get_on_the_parked_run_shows_the_proposed_diff()
    {
        await WithHostAsync(async (factory, connectionString, _) =>
        {
            const string proposal = "Run dotnet test.\nIntegration needs Docker.";
            var runId = await SeedParkedRunAsync(connectionString, "diff", proposal);

            using var client = DeveloperClient(factory);
            var response = await client.GetAsync($"/api/workflow-runs/{runId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadFromJsonAsync<WorkflowRunView>();
            body.Should().NotBeNull();
            body!.StandingInstructionsDiff.Should().Contain("+Integration needs Docker.");
        });
    }

    private static HttpClient DeveloperClient(ApiWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, "a-developer");
        client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, "developer");
        return client;
    }

    /// <summary>
    ///     Starts <c>b5-resume-test</c> directly at <c>gate</c> through a standalone store (never the booted
    ///     host's), with <paramref name="proposal"/> — or its absence — set as
    ///     <see cref="ReviewHandoff.ProposedStandingInstructionsKey"/>, then dispatches <c>gate</c> once to park
    ///     the run at <see cref="WorkflowStatus.Awaiting"/>. Mirrors <c>ResumeSignalMismatchTests.ParkedAtGateAsync</c>.
    /// </summary>
    private static async Task<Guid> SeedParkedRunAsync(string connectionString, string correlationKey, string? proposal)
    {
        var options = new WorkflowOrmOptions { ConnectionString = connectionString };
        var definitions = new OrmProcessDefinitionStore(options);
        var store = new OrmWorkflowStore(options, definitions);

        if (await definitions.GetActiveVersionAsync(ProcessName, CancellationToken.None) is null)
        {
            var definition = ProcessLoader.Load(Yaml);
            definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : null);
            (await definitions.UpsertAndActivateAsync(definition.Value, Yaml, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }

        IReadOnlyDictionary<string, object?>? initialVariables = proposal is null
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal) { [ReviewHandoff.ProposedStandingInstructionsKey] = proposal };

        var runId = await store.StartAsync(
            ProcessName, 1, $"{correlationKey}:{Guid.NewGuid()}", "gate", initialVariables, CancellationToken.None);

        var run = await store.FindAsync(runId, CancellationToken.None);
        var dispatcher = new WorkflowNodeDispatcher(
            store, Substitute.For<ISubagentRunner>(), Substitute.For<IWorkflowReferenceResolver>(), definitions,
            Substitute.For<ISkillStore>(), r => new WorkflowCaller(r));

        await dispatcher.DispatchAsync(new WorkflowDispatchMessage(runId, run!.CurrentSeq, "gate"), CancellationToken.None);

        var parked = await store.FindAsync(runId, CancellationToken.None);
        parked!.Status.Should().Be(WorkflowStatus.Awaiting, "otherwise the rest of this test proves nothing");
        parked.AwaitingSignal.Should().Be(Signal);

        return runId;
    }

    /// <summary>
    ///     Cancels <paramref name="runId"/> through the same standalone store <see cref="SeedParkedRunAsync"/>
    ///     seeds with, so a test can drive a run past <see cref="WorkflowStatus.Awaiting"/> before ever touching
    ///     the REST endpoint.
    /// </summary>
    private static async Task CancelDirectlyAsync(string connectionString, Guid runId)
    {
        var options = new WorkflowOrmOptions { ConnectionString = connectionString };
        var definitions = new OrmProcessDefinitionStore(options);
        var store = new OrmWorkflowStore(options, definitions);

        await store.CancelAsync(runId, "cancelled before the resume attempt", CancellationToken.None);

        var cancelled = await store.FindAsync(runId, CancellationToken.None);
        cancelled!.Status.Should().Be(WorkflowStatus.Cancelled, "otherwise the rest of this test proves nothing");
    }

    /// <summary>
    ///     Creates a throwaway database, migrates it (EF Core's model plus Thalos.NET.Workflow.Orm's raw-SQL
    ///     outbox/workflow tables), boots a real <see cref="ApiWebApplicationFactory"/> with the workflow engine
    ///     enabled and <c>Thalos:Workflow:StandingInstructionsPath</c> pointed at a fresh <see cref="TempDirectory"/> under
    ///     the host's content root, which is where <c>AddDaedalusAgents</c> requires the file to be,
    ///     runs <paramref name="body"/>, and tears both down afterward. Mirrors <c>StartRunEndpointTests.WithRunningHostAsync</c>.
    /// </summary>
    private async Task WithHostAsync(Func<ApiWebApplicationFactory, string, TempDirectory, Task> body)
    {
        var dbName = $"b5_resume_e2e_{Guid.NewGuid():N}";
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

            factory = new ApiWebApplicationFactory(
                connectionString, Substitute.For<IAgentRuntime>(), workflowEnabled: true,
                standingInstructionsPath: Path.Combine(relative, "AGENT.md"));

            // Force the host to build and start now — see StartRunEndpointTests.WithRunningHostAsync's own remarks.
            _ = factory.Services;

            var contentRoot = factory.Services.GetRequiredService<IHostEnvironment>().ContentRootPath;
            using var dir = new TempDirectory(Path.Combine(contentRoot, relative));
            await body(factory, connectionString, dir);
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
}
