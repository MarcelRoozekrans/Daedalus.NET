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
///     Boots a real host with the shipped API configuration and asserts the skill sync ran: both starter procedures are
///     in the database, active, with their bodies verbatim. This is the only test that exercises the whole path —
///     Content copy → content root → resolved root → SkillSyncService → PostgresSkillStore — and it is the path that
///     fails silently if any link breaks.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SkillsStartupTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Host_start_syncs_both_starter_skills_into_postgres()
    {
        using var host = BuildHost();
        await host.StartAsync();
        try
        {
            var store = host.Services.GetRequiredService<ISkillStore>();

            var all = await store.ListAsync(new SkillQuery(), CancellationToken.None);
            all.IsSuccess.Should().BeTrue();
            all.Value.Select(s => s.Name.Value).Should().Equal("daedalus-migrations", "thalos-release");
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
            rows.Should().HaveCount(2);
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
        });

        builder.Services.AddPooledDbContextFactory<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        builder.Services.AddDaedalusAgents(builder.Configuration, builder.Environment);
        return builder.Build();
    }
}
