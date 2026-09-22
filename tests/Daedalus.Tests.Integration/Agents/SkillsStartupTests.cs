using Daedalus.Agents;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Thalos.Skills;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Boots a real host with the shipped API configuration and asserts the skill sync ran: every procedure
///     authored under <c>skills/</c> is in the database, active, with its body verbatim — the two starter
///     procedures plus Task 11's <c>manufacture-*</c> trio. This is the only test that exercises the whole path —
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
                "daedalus-migrations", "manufacture-implement", "manufacture-publish", "manufacture-review", "thalos-release");
            all.Value.Should().OnlyContain(s => s.IsActive && s.Description.Length > 0 && s.ContentHash.Length > 0);

            var migrations = await store.GetAsync(SkillName.Parse("daedalus-migrations"), CancellationToken.None);
            migrations.Value.Description.Should().Be("How to add, verify and apply an EF Core migration in the Daedalus repo.");
            migrations.Value.Tags.Should().Contain("ef");
            migrations.Value.Body.Should().Contain("dotnet ef migrations add")
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
        using (var db = fixture.CreateDbContext())
        {
            var row = await db.Skills.AsNoTracking().SingleAsync(s => s.Id == "daedalus-migrations");
            firstUpdatedAt = new DateTimeOffset(row.UpdatedAt, TimeSpan.Zero);
        }

        using var second = BuildHost();
        await second.StartAsync();
        try
        {
            using var db = fixture.CreateDbContext();
            var rows = await db.Skills.AsNoTracking().ToListAsync();
            rows.Should().HaveCount(5, "the two starter skills plus Task 11's manufacture-implement/-review/-publish trio");
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
            // Same reason ApiWebApplicationFactory sets this: PostgresFixture's EnsureCreatedAsync builds only the
            // EF Core model, never Thalos.NET.Workflow.Orm's raw-SQL tables. This test is about skill sync, not the
            // workflow engine, but processes/manufacture.yaml (Task 11) is now a real file on Thalos:Workflow's
            // ProcessesRoot and flows into this host's output the same way skills/*.SKILL.md does - left enabled,
            // ProcessDefinitionSyncHostedService.StartAsync has something to sync, and unlike the periodic workflow
            // services its failure is not caught, so the host would fail to start on 42P01 rather than degrade.
            ["Thalos:Workflow:Enabled"] = "false",
        });

        builder.Services.AddPooledDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        builder.Services.AddDaedalusAgents(builder.Configuration, builder.Environment);
        return builder.Build();
    }
}
