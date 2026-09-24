using System.Data.Async.Adapters;
using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Thalos.Workflow;
using Thalos.Workflow.Orm;
using ZeroAlloc.ORM.Migrations;
using ZeroAlloc.Outbox.Orm;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Migrations;

/// <summary>
///     Boots a real host with the shipped API configuration, <b>with the workflow engine left enabled</b> — the
///     only test in the suite that does, and only because this is the one place a database with the raw workflow
///     tables already exists (a throwaway one, migrated the same way <see cref="WorkflowOrmMigrationTests"/>
///     does) — and asserts <c>processes/manufacture.yaml</c> (Task 11) actually synced and activated.
/// </summary>
/// <remarks>
///     `Daedalus.Api.csproj`'s <c>Content Include</c> for <c>processes/**/*.yaml</c>
///     is the only thing that makes <c>Thalos:Workflow:ProcessesRoot</c> resolve a real file under <c>dotnet run</c>
///     or in a test host at all (see `SkillsStartupTests`'s own remarks for the identical story with
///     <c>skills/**/SKILL.md</c>). Before Task 11, that glob resolved to zero files on every host, silently, and
///     every other Integration test that boots a full host now deliberately sets
///     <c>Thalos:Workflow:Enabled=false</c> specifically so it does <em>not</em> exercise this path (see
///     those tests' own remarks) — which means nothing else in this suite would go red if the glob, the folder
///     name, or the process name were broken. This test is that guard.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ProcessDefinitionSyncEndToEndTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Host_start_with_the_workflow_engine_enabled_syncs_and_activates_manufacture_v3()
    {
        var dbName = $"process_sync_e2e_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{dbName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = dbName }.ConnectionString;
        try
        {
            // Same migration order Daedalus.Migrations/Program.cs runs in production, and
            // WorkflowOrmMigrationTests.RunAsync mirrors for the same reason: the host under test must see a
            // schema that actually has process_definition, workflow_run, workflow_run_event and both outbox
            // tables, or ProcessDefinitionSyncHostedService.StartAsync throws on 42P01 instead of exercising the
            // path this test exists to guard.
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

            var builder = Host.CreateApplicationBuilder();
            builder.Environment.ContentRootPath = AppContext.BaseDirectory;
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.appsettings.json"), optional: false);
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["ConnectionStrings:daedalus"] = connectionString,
                // Deliberately NOT set here - Thalos:Workflow:Enabled stays at its real appsettings.json/code
                // default (true), which is the entire point of this test.
            });

            builder.Services.AddPooledDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(connectionString));
            builder.Services.AddDaedalusAgents(builder.Configuration, builder.Environment);

            using var host = builder.Build();
            await host.StartAsync();
            try
            {
                var definitions = host.Services.GetRequiredService<IProcessDefinitionStore>();

                var activeVersion = await definitions.GetActiveVersionAsync("manufacture", CancellationToken.None);
                activeVersion.Should().Be(5, "processes/manufacture.yaml declares version: 5 - phase 2.4 task B4 added a `retrospect` node between `review` and the human gate, which content-hash immutability makes a version change rather than an edit in place - and it must activate on a clean sync");

                var definition = await definitions.GetAsync("manufacture", activeVersion!.Value, CancellationToken.None);
                definition.IsSuccess.Should().BeTrue(definition.IsFailure ? definition.Error : null);
                definition.Value.StartNode.Should().Be("implement", "'implement' is the first key under 'nodes' in the real file");
                definition.Value.Nodes.Keys.Should().Contain(["implement", "review", "retrospect", "adjudicate", "gate", "publish", "done"]);

                // Activation is the load-bearing part of the three assertions below, not the equality. A
                // definition only reaches this store after ProcessValidator.ValidateAsync has accepted it against
                // this host's real IWorkflowReferenceResolver, which resolves every `agent:` over the live
                // IAgentCatalog and every `skill:` over the live ISkillStore. So a v5 that is active at all is a
                // v5 whose 'implementer' and 'reviewer' both exist here - which is the one thing the unit-level
                // guards over the YAML text cannot show, because they read a configuration file rather than a
                // booted host. Falsifiable: renaming either agent in Thalos:Agents leaves the previous version active and
                // the assertion above red.
                definition.Value.Nodes["implement"].Agent.Should().Be("implementer");
                definition.Value.Nodes["review"].Agent.Should().Be("reviewer");
                definition.Value.Nodes["review"].Lenses.Should().Equal("correctness", "falsifiability", "mechanism");
            }
            finally
            {
                await host.StopAsync();
            }
        }
        finally
        {
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
