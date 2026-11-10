using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.Skills;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Thalos.Skills;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Boots a real host with the shipped API configuration and asserts the skill sync ran: every procedure
///     authored under <c>skills/</c> is in the database, active, with its body verbatim — the two starter
///     procedures plus the <c>manufacture-*</c> quartet. This is the only test that exercises the whole path —
///     Content copy → content root → resolved root → SkillSyncService → PostgresSkillStore — and it is the path that
///     fails silently if any link breaks.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SkillsStartupTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Host_start_syncs_every_skill_into_postgres()
    {
        using var host = BuildHost();
        await host.StartAsync();
        try
        {
            var store = host.Services.GetRequiredService<ISkillStore>();

            var all = await store.ListAsync(new SkillQuery(), CancellationToken.None);
            all.IsSuccess.Should().BeTrue();
            // Ordinal by Id (PostgresSkillStore.ListAsync's own sort) - alphabetical, not sync order.
            all.Value.Select(s => s.Name.Value).Should().Equal(
                "daedalus-migrations", "manufacture-implement", "manufacture-publish", "manufacture-retrospect", "manufacture-review", "thalos-release");
            all.Value.Should().OnlyContain(s => s.IsActive && s.Description.Length > 0 && s.ContentHash.Length > 0);

            var migrations = await store.GetAsync(SkillName.Parse("daedalus-migrations"), CancellationToken.None);
            migrations.Value.Description.Should().Be("How to add, verify and apply a schema migration in the Daedalus repo.");
            migrations.Value.Tags.Should().Contain("zeroalloc-orm");
            migrations.Value.Body.Should().Contain("dotnet run --project src/Daedalus.Migrations")
                .And.NotContain("---", "the frontmatter is parsed off; the body is everything after it");
            migrations.Value.SourcePath.Should().EndWith("SKILL.md");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    /// <summary>A second start with unchanged files is a no-op: the sync compares content hashes and skips.</summary>
    [Fact]
    public async Task A_second_start_leaves_the_rows_untouched()
    {
        using (var first = BuildHost())
        {
            await first.StartAsync();
            await first.StopAsync();
        }

        DateTimeOffset firstUpdatedAt;
        await using (var connection = await fixture.Db.OpenAsync(CancellationToken.None))
        {
            var row = await new SkillSql(connection).FindAsync("daedalus-migrations", CancellationToken.None);
            firstUpdatedAt = new DateTimeOffset(row!.UpdatedAt, TimeSpan.Zero);
        }

        using var second = BuildHost();
        await second.StartAsync();
        try
        {
            await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
            var rows = await new SkillSql(connection).ListAsync([], [], includeInactive: true, CancellationToken.None);
            rows.Should().HaveCount(6, "the two starter skills plus manufacture-implement/-review/-retrospect/-publish");
            new DateTimeOffset(rows.Single(r => string.Equals(r.Id, "daedalus-migrations", StringComparison.Ordinal)).UpdatedAt, TimeSpan.Zero)
                .Should().Be(firstUpdatedAt, "an unchanged content hash means the file is skipped entirely");
        }
        finally
        {
            await second.StopAsync();
        }
    }

    private IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.ContentRootPath = AppContext.BaseDirectory;
        builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "Daedalus.Api.appsettings.json"), optional: false);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ConnectionStrings:daedalus"] = fixture.ConnectionString,
            // Same reason ApiWebApplicationFactory sets this: this test is about skill sync, not the workflow
            // engine. processes/manufacture.yaml (Task 11) is a real file on Thalos:Workflow's ProcessesRoot and flows
            // into this host's output the same way skills/*.SKILL.md does - left enabled,
            // ProcessDefinitionSyncHostedService.StartAsync would sync it into the shared database, and unlike the
            // periodic workflow services its failure is not caught, so a failing sync would stop the host.
            ["Thalos:Workflow:Enabled"] = "false",
        });

        builder.Services.AddDaedalusAgents(builder.Configuration, builder.Environment);
        // A real host gets the data source and DaedalusDb from AddApplicationDatabase; with the workflow engine off
        // AddDaedalusAgents registers no data source, and the charter store needs both to resolve.
        builder.Services.TryAddSingleton(_ => NpgsqlDataSource.Create(fixture.ConnectionString));
        builder.Services.TryAddSingleton<DaedalusDb>();
        return builder.Build();
    }
}
