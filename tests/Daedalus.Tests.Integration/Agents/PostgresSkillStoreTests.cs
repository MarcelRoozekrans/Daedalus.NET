using Daedalus.Agents.Skills;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Thalos;
using Thalos.Skills;
using Thalos.Testing;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Runs Thalos.NET's <see cref="ISkillStore"/> contract suite against the Postgres-backed store. Tests in the
///     database collection run sequentially; the database is reset before each test and the base class creates one
///     store per test through <see cref="CreateStoreAsync"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresSkillStoreTests(PostgresFixture fixture) : SkillStoreContractTests, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // Upserts carry their own UpdatedAt (they are synced from files), but DeactivateMissingAsync stamps the clock:
    // the row records when its file stopped existing, so the store needs the contract's clock.
    protected override ValueTask<ISkillStore> CreateStoreAsync(TimeProvider clock) =>
        new(new PostgresSkillStore(new FixtureDbContextFactory(fixture), clock));

    /// <summary>Daedalus-specific: the tag filter is AND over normalised tags, translated to per-tag <c>= ANY("Tags")</c>.</summary>
    [Fact]
    public async Task List_tag_filter_requires_every_tag()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewSkill(clock, "xy", tags: ["x", "y"]), CancellationToken.None);
        await store.UpsertAsync(NewSkill(clock, "x-only", tags: ["x"]), CancellationToken.None);
        await store.UpsertAsync(NewSkill(clock, "none", tags: []), CancellationToken.None);

        (await store.ListAsync(new SkillQuery { Tags = ["x"] }, CancellationToken.None)).Value
            .Select(s => s.Name.Value).Should().BeEquivalentTo(["xy", "x-only"]);
        (await store.ListAsync(new SkillQuery { Tags = ["x", "y"] }, CancellationToken.None)).Value
            .Select(s => s.Name.Value).Should().BeEquivalentTo(["xy"]);
        (await store.ListAsync(new SkillQuery { Tags = [" "] }, CancellationToken.None)).Value
            .Should().BeEmpty("a blank query tag matches nothing");
    }

    /// <summary>
    ///     Daedalus-specific: a document that violates the aggregate's rules comes back as SkillValidationFailed, not as
    ///     a database constraint failure — the whole point of mirroring the Thalos limits in the Domain layer.
    /// </summary>
    [Fact]
    public async Task Upsert_of_an_over_size_body_is_a_validation_error()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);

        var result = await store.UpsertAsync(NewSkill(clock, "huge", body: new string('x', (64 * 1024) + 1)), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SkillValidationFailed);
    }

    /// <summary>
    ///     Daedalus-specific: <see cref="SkillDocument.IsActive"/> on a pinned version is not stored on the version row
    ///     itself (a version has no activity of its own) — it is read off the skill's current <c>Skills</c> row at
    ///     query time, so it reflects deactivation even for a hash that is no longer current.
    /// </summary>
    [Fact]
    public async Task GetVersion_reports_IsActive_from_the_current_skills_row_not_the_version_row()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        await store.UpsertAsync(NewSkill(clock, "release", hash: "hash-v1"), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.UpsertAsync(NewSkill(clock, "release", hash: "hash-v2"), CancellationToken.None);

        var beforeDeactivation = await store.GetVersionAsync(SkillName.Parse("release"), "hash-v1", CancellationToken.None);
        beforeDeactivation.Value.IsActive.Should().BeTrue("the skill is still current when hash-v1 is loaded");

        await store.DeactivateMissingAsync([], CancellationToken.None);

        var afterDeactivation = await store.GetVersionAsync(SkillName.Parse("release"), "hash-v1", CancellationToken.None);
        afterDeactivation.Value.IsActive.Should().BeFalse("the same superseded version now reports the skill's current, deactivated state");
    }

    /// <summary>
    ///     Final review minor: Daedalus.Api and Daedalus.Cli both sync skills into the shared tables at start. When
    ///     both see a new content hash, both check that no version row exists, both insert one, and the loser's
    ///     save hits the <c>(Name, ContentHash)</c> primary key. That used to come back as SkillStoreFailed and
    ///     could fail the losing host's startup. The race is forced here by inserting the winning row from a
    ///     second connection at the moment the store saves.
    /// </summary>
    [Fact]
    public async Task Upsert_that_loses_the_version_insert_race_still_succeeds_and_updates_the_skill()
    {
        var clock = NewClock();
        var plain = await CreateStoreAsync(clock);
        (await plain.UpsertAsync(NewSkill(clock, "raced", hash: "hash-v1"), CancellationToken.None)).IsSuccess.Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(1));

        var racer = new WinningVersionInsert(fixture.ConnectionString, "raced", "hash-v2");
        var store = new PostgresSkillStore(new InterceptedDbContextFactory(fixture, racer), clock);

        var result = await store.UpsertAsync(NewSkill(clock, "raced", hash: "hash-v2"), CancellationToken.None);

        racer.Fired.Should().BeTrue("otherwise no race happened and this test proves nothing");
        result.IsSuccess.Should().BeTrue(
            result.IsFailure ? $"a lost insert of an identical version row is success, not {result.Error.Code}" : "");
        (await plain.GetAsync(SkillName.Parse("raced"), CancellationToken.None)).Value.ContentHash.Should().Be("hash-v2",
            "losing the version insert must not also lose the update to the Skills row, which the failed save rolled back");
    }

    /// <summary>
    ///     Inserts the version row for <paramref name="name"/> at <paramref name="hash"/> from a second connection,
    ///     once, just before the store's first save runs: the other host winning the race.
    /// </summary>
    private sealed class WinningVersionInsert(string connectionString, string name, string hash) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var insert = new NpgsqlCommand(
                    """
                    INSERT INTO "SkillVersions" ("Name", "ContentHash", "Description", "Body", "Tags", "SourcePath", "CreatedAt")
                    VALUES (@name, @hash, 'won by the other host', 'body', '{}', 'raced/SKILL.md', now())
                    """,
                    connection);
                insert.Parameters.AddWithValue("name", name);
                insert.Parameters.AddWithValue("hash", hash);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class InterceptedDbContextFactory(PostgresFixture fixture, IInterceptor interceptor) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(fixture.ConnectionString)
                .AddInterceptors(interceptor)
                .Options);
    }

    /// <summary>The store disposes every context it creates, so the factory needs no tracking.</summary>
    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
