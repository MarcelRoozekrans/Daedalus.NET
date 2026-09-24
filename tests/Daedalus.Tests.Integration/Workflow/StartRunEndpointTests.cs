using System.Data.Async.Adapters;
using Daedalus.Api.Controllers;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Thalos;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     End-to-end coverage of <c>POST</c>/<c>GET /api/workflow-runs</c> against a real, migrated Postgres database
///     with the workflow engine <b>enabled</b> — every other Integration test that boots
///     <see cref="ApiWebApplicationFactory"/> runs with <c>Thalos:Workflow:Enabled=false</c> (see that class's own
///     remarks); this is the one surface that needs the engine genuinely on. Follows
///     <c>ProcessDefinitionSyncEndToEndTests</c> for how to migrate a throwaway database with
///     Thalos.NET.Workflow.Orm's raw-SQL tables on top of the EF Core schema, and
///     <c>ResumeSignalMismatchTests</c> for running each test against its own scratch database rather than the
///     shared <see cref="PostgresFixture"/> one, which is built with <c>EnsureCreatedAsync</c> and has none of
///     those tables.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StartRunEndpointTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Posting_a_blank_work_intent_returns_bad_request()
    {
        await WithRunningHostAsync(async (factory, _) =>
        {
            using var client = DeveloperClient(factory);

            var response = await client.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "   " });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        });
    }

    /// <summary>
    ///     Controller ruling R5: the pinned nodes are read off the real, loaded, active process definition — never
    ///     a hardcoded <c>["implement", "review", "retrospect"]</c> list — so this stays correct at the current
    ///     process version 4 (implement, review, publish) and again once phase 2.4 task B4 adds <c>retrospect</c>
    ///     at version 5, with no edit to this test.
    /// </summary>
    [Fact]
    public async Task A_developer_starting_a_run_gets_every_task_node_of_the_active_process_pinned()
    {
        await WithRunningHostAsync(async (factory, _) =>
        {
            var definitions = factory.Services.GetRequiredService<IProcessDefinitionStore>();
            var version = await definitions.GetActiveVersionAsync("manufacture", CancellationToken.None);
            version.Should().NotBeNull("otherwise this test proves nothing about the real process");

            var definition = await definitions.GetAsync("manufacture", version!.Value, CancellationToken.None);
            definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : null);

            var expectedTaskNodes = definition.Value.Nodes
                .Where(n => n.Value.Agent is not null)
                .Select(n => n.Key)
                .ToList();
            expectedTaskNodes.Should().NotBeEmpty("otherwise this test passes vacuously");

            using var client = DeveloperClient(factory);
            const string intent = "add a health check endpoint";

            var response = await client.PostAsJsonAsync("/api/workflow-runs", new { workIntent = intent });

            response.StatusCode.Should().Be(HttpStatusCode.Created);
            var body = await response.Content.ReadFromJsonAsync<StartWorkflowRunResponse>();
            body.Should().NotBeNull();

            var store = factory.Services.GetRequiredService<IWorkflowStore>();
            var run = await store.FindAsync(body!.RunId, CancellationToken.None);

            run.Should().NotBeNull("the row must exist once the endpoint has reported 201");
            run!.Variables["work_intent"].Should().Be(intent);
            run.Manifest.Should().NotBeNull();
            run.Manifest!.Nodes.Keys.Should().BeEquivalentTo(expectedTaskNodes,
                "every task node of the active process must be pinned, derived from the loaded definition");
        });
    }

    [Fact]
    public async Task A_deactivated_node_skill_fails_the_start_and_writes_no_row()
    {
        await WithRunningHostAsync(async (factory, connectionString) =>
        {
            await DeactivateSkillAsync(connectionString, "manufacture-implement");
            var before = await CountWorkflowRunsAsync(connectionString);

            using var client = DeveloperClient(factory);
            var response = await client.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "anything" });

            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
            problem.Should().NotBeNull();
            problem!.Detail.Should().Contain("implement", "the failure must name the node whose skill is not active");

            var after = await CountWorkflowRunsAsync(connectionString);
            after.Should().Be(before, "a pin failure must not create a run row");
        });
    }

    [Fact]
    public async Task Starting_a_run_without_authentication_returns_unauthorized()
    {
        await WithRunningHostAsync(async (factory, _) =>
        {
            using var client = factory.CreateClient();

            var response = await client.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "anything" });

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        });
    }

    [Fact]
    public async Task A_non_developer_cannot_start_a_run()
    {
        await WithRunningHostAsync(async (factory, _) =>
        {
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.UserHeader, "a-reader");
            client.DefaultRequestHeaders.Add(HeaderTestAuthHandler.RolesHeader, "reader");

            var response = await client.PostAsJsonAsync("/api/workflow-runs", new { workIntent = "anything" });

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        });
    }

    [Fact]
    public async Task Getting_an_unknown_run_returns_not_found()
    {
        await WithRunningHostAsync(async (factory, _) =>
        {
            using var client = DeveloperClient(factory);

            var response = await client.GetAsync($"/api/workflow-runs/{Guid.NewGuid()}");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
    ///     Flips the seeded <c>manufacture-implement</c> skill's <c>IsActive</c> to <see langword="false"/>
    ///     directly against the store, the same table <c>PostgresSkillStore</c> writes — the sync that seeded it
    ///     at host start ran once, at boot, over the real <c>skills/manufacture-implement/SKILL.md</c> file; this
    ///     simulates the file having since been retired without a second sync.
    /// </summary>
    private static async Task DeactivateSkillAsync(string connectionString, string skillName)
    {
        await using var db = new ApplicationDbContext(PostgresFixture.CreateDbContextOptions(connectionString));
        var skill = await db.Skills.SingleAsync(s => s.Id == skillName);
        skill.Update(skill.Description, skill.Body, skill.Tags, skill.SourcePath, skill.ContentHash, isActive: false, skill.UpdatedAt);
        await db.SaveChangesAsync();
    }

    private static async Task<long> CountWorkflowRunsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT COUNT(*) FROM workflow_run", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    ///     Creates a throwaway database, applies the same migrations <c>ProcessDefinitionSyncEndToEndTests</c>
    ///     does (EF Core's model, the <c>vector</c> extension, then Thalos.NET.Workflow.Orm's raw-SQL outbox and
    ///     workflow tables), boots a real <see cref="ApiWebApplicationFactory"/> against it with the workflow
    ///     engine enabled, runs <paramref name="body"/>, and tears both down afterward — mirroring
    ///     <c>ResumeSignalMismatchTests.WithScratchDatabaseAsync</c>, one throwaway database per test rather than
    ///     one shared across the class, so <see cref="A_deactivated_node_skill_fails_the_start_and_writes_no_row"/>
    ///     deactivating a skill can never leak into any other test here.
    /// </summary>
    private async Task WithRunningHostAsync(Func<ApiWebApplicationFactory, string, Task> body)
    {
        var dbName = $"start_run_e2e_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;

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

            factory = new ApiWebApplicationFactory(connectionString, Substitute.For<IAgentRuntime>(), workflowEnabled: true);

            // Forces the host to build and start now, synchronously, rather than lazily on the first client or
            // service resolution a test happens to make. ProcessDefinitionSyncHostedService and Thalos' skill
            // sync both await their initial sync inline from StartAsync, so once this returns, `manufacture` and
            // every skills/*/SKILL.md file (including manufacture-implement, which
            // A_deactivated_node_skill_fails_the_start_and_writes_no_row deactivates before any request is made)
            // are already seeded.
            _ = factory.Services;

            await body(factory, connectionString);
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
