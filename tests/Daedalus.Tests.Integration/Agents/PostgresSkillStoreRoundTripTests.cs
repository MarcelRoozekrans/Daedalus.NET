using Daedalus.Agents.Skills;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.Skills;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Thalos;
using Thalos.Skills;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Phase 3.5: <see cref="PostgresSkillStore"/> on ZeroAlloc.ORM stores every column of a skill and its version,
///     loads a row whatever <c>Skill.Create</c> would say of it today, and answers a lost race as it always did.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresSkillStoreRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset _at = new(2026, 11, 5, 9, 15, 30, TimeSpan.Zero);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static SkillDocument Document(string name, string hash, params string[] tags) => new()
    {
        Name = SkillName.Parse(name),
        Description = "How to release",
        Body = "Step 1.\nStep 2.",
        Tags = tags,
        SourcePath = $"skills/{name}/SKILL.md",
        ContentHash = hash,
        IsActive = true,
        UpdatedAt = _at,
    };

    /// <summary>
    ///     Every field of a skill document: stored through the store, read back identical, and the pinned version too.
    ///     Red: leave any column out of an insert or a read and the comparison names it.
    /// </summary>
    [Fact]
    public async Task Stored_skill_loads_back_whole()
    {
        var store = new PostgresSkillStore(fixture.Db, TimeProvider.System);
        var document = Document("release", "hash-1", "ef", "ops");

        (await store.UpsertAsync(document, CancellationToken.None)).IsSuccess.Should().BeTrue();

        (await store.GetAsync(document.Name, CancellationToken.None)).Value.Should().BeEquivalentTo(document);
        (await store.GetVersionAsync(document.Name, "hash-1", CancellationToken.None)).Value.Should().BeEquivalentTo(document);
    }

    /// <summary>
    ///     The two aggregates themselves: a stored skill and a stored version come back equal in every property.
    ///     Red: read any column wrongly, and the comparison names it.
    /// </summary>
    [Fact]
    public async Task Stored_aggregates_load_back_whole()
    {
        var skill = Skill.Create("release", "Releases", "Body.", ["ef", "ops"], "skills/release/SKILL.md", "hash-1", true, _at.UtcDateTime).Value;
        var version = SkillVersion.Create("release", "hash-1", "Releases", "Body.", ["ef", "ops"], "skills/release/SKILL.md", _at.UtcDateTime).Value;
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new SkillSql(connection);

        await sql.StoreAsync(skill, skillExists: false, version, CancellationToken.None);
        var loaded = await sql.FindAsync("release", CancellationToken.None);
        var loadedVersion = await sql.FindVersionAsync("release", "hash-1", CancellationToken.None);

        loaded.Should().BeEquivalentTo(skill, options => options.ComparingByMembers<Skill>());
        loadedVersion.Should().BeEquivalentTo(version, options => options.ComparingByMembers<SkillVersion>());
        loaded!.UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
        loadedVersion!.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        (await sql.VersionExistsAsync("release", "hash-1", CancellationToken.None)).Should().BeTrue();
        (await sql.VersionExistsAsync("release", "hash-2", CancellationToken.None)).Should().BeFalse();
    }

    /// <summary>
    ///     Review focus 1: a skill and version <c>Create</c> refuse, an empty description, body and source path and
    ///     uppercase tags here, still load, as written. Red: build the loaded row through <c>Create</c>, and the load
    ///     fails.
    /// </summary>
    [Fact]
    public async Task A_row_Create_rejects_still_loads()
    {
        Skill.Create("legacy", "", "", ["A"], "", "h", true, _at.UtcDateTime).IsFailure
            .Should().BeTrue("the rows below are ones Create refuses");
        await ExecuteAsync(
            """
            INSERT INTO "Skills" ("Name", "Description", "Body", "Tags", "SourcePath", "ContentHash", "IsActive", "UpdatedAt")
            VALUES ('legacy', '', '', '{A,B}', '', 'h', false, @at);
            INSERT INTO "SkillVersions" ("Name", "ContentHash", "Description", "Body", "Tags", "SourcePath", "CreatedAt")
            VALUES ('legacy', 'h', '', '', '{A,B}', '', @at);
            """);
        var store = new PostgresSkillStore(fixture.Db, TimeProvider.System);

        var skill = (await store.GetAsync(SkillName.Parse("legacy"), CancellationToken.None)).Value;
        var version = (await store.GetVersionAsync(SkillName.Parse("legacy"), "h", CancellationToken.None)).Value;

        skill.Description.Should().BeEmpty();
        skill.Body.Should().BeEmpty();
        skill.SourcePath.Should().BeEmpty();
        skill.Tags.Should().Equal("A", "B");
        skill.IsActive.Should().BeFalse();
        skill.UpdatedAt.Should().Be(_at);
        version.Tags.Should().Equal("A", "B");
        version.IsActive.Should().BeFalse();
        version.UpdatedAt.Should().Be(_at);
    }

    /// <summary>
    ///     Daedalus.Api and Daedalus.Cli both sync skills at start: both see a new hash, both insert the version, and
    ///     the loser used to hit the primary key and retry. Now the insert is ON CONFLICT DO NOTHING. A trigger plays
    ///     the winner, inserting the same version row just before the store's own insert. The upsert succeeds and the
    ///     update to the Skills row stands. Red: drop the ON CONFLICT clause and the upsert answers SkillStoreFailed.
    /// </summary>
    [Fact]
    public async Task Upsert_that_loses_the_version_insert_race_still_succeeds_and_updates_the_skill()
    {
        var store = new PostgresSkillStore(fixture.Db, TimeProvider.System);
        (await store.UpsertAsync(Document("raced", "hash-v1"), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await ExecuteAsync(
            """
            CREATE FUNCTION race_version() RETURNS trigger AS $$
            BEGIN
                IF pg_trigger_depth() < 2 THEN
                    INSERT INTO "SkillVersions" ("Name", "ContentHash", "Description", "Body", "Tags", "SourcePath", "CreatedAt")
                    VALUES (NEW."Name", NEW."ContentHash", 'won by the other host', 'body', '{}', 'raced/SKILL.md', now())
                    ON CONFLICT DO NOTHING;
                END IF;
                RETURN NEW;
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER race_version BEFORE INSERT ON "SkillVersions"
                FOR EACH ROW EXECUTE FUNCTION race_version();
            """);
        try
        {
            var result = await store.UpsertAsync(Document("raced", "hash-v2"), CancellationToken.None);

            result.IsSuccess.Should().BeTrue(
                result.IsFailure ? $"a lost insert of an identical version row is success, not {result.Error.Code}" : "");
        }
        finally
        {
            await ExecuteAsync("""DROP TRIGGER race_version ON "SkillVersions"; DROP FUNCTION race_version();""");
        }

        (await store.GetAsync(SkillName.Parse("raced"), CancellationToken.None)).Value.ContentHash.Should().Be("hash-v2");
        (await store.GetVersionAsync(SkillName.Parse("raced"), "hash-v2", CancellationToken.None)).Value.Description
            .Should().Be("won by the other host", "the winner's row stands; ON CONFLICT DO NOTHING overwrites nothing");
    }

    /// <summary>
    ///     Two hosts creating the same Skills row: the loser's insert breaks the primary key and the upsert answers
    ///     SkillStoreFailed, as it did when EF's SaveChanges threw, and the version row goes with it. A trigger plays
    ///     the winner. Red: write the two rows outside one transaction and the version survives; catch nothing and
    ///     the Npgsql exception escapes.
    /// </summary>
    [Fact]
    public async Task Upsert_loses_the_skill_insert_race_with_SkillStoreFailed()
    {
        await ExecuteAsync(
            """
            CREATE FUNCTION race_skill() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'duplicate key value violates unique constraint "PK_Skills"'
                    USING ERRCODE = '23505';
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER race_skill BEFORE INSERT ON "Skills"
                FOR EACH ROW EXECUTE FUNCTION race_skill();
            """);
        try
        {
            var store = new PostgresSkillStore(fixture.Db, TimeProvider.System);

            var result = await store.UpsertAsync(Document("loser", "loser-hash"), CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be(AgentErrorCode.SkillStoreFailed);
            result.Error.Message.Should().Be("Could not store the skill.");
        }
        finally
        {
            await ExecuteAsync("""DROP TRIGGER race_skill ON "Skills"; DROP FUNCTION race_skill();""");
        }

        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new SkillSql(connection);
        (await sql.FindAsync("loser", CancellationToken.None)).Should().BeNull();
        (await sql.VersionExistsAsync("loser", "loser-hash", CancellationToken.None)).Should().BeFalse();
    }

    /// <summary>
    ///     The list filters in one statement: names, every tag (an empty tag list is no filter, as EF applied none)
    ///     and the active flag. Red: make the tag filter any-of instead of all-of, or drop the empty-list case.
    /// </summary>
    [Fact]
    public async Task List_filters_by_names_tags_and_activity_in_one_statement()
    {
        var store = new PostgresSkillStore(fixture.Db, TimeProvider.System);
        await store.UpsertAsync(Document("a-both", "h", "x", "y"), CancellationToken.None);
        await store.UpsertAsync(Document("b-x", "h", "x"), CancellationToken.None);
        await store.UpsertAsync(Document("c-none", "h"), CancellationToken.None);
        await store.UpsertAsync(Document("d-off", "h", "x", "y") with { IsActive = false }, CancellationToken.None);

        async Task<string[]> List(SkillQuery query) =>
            [.. (await store.ListAsync(query, CancellationToken.None)).Value.Select(s => s.Name.Value)];

        (await List(new SkillQuery())).Should().Equal("a-both", "b-x", "c-none");
        (await List(new SkillQuery { IncludeInactive = true })).Should().Equal("a-both", "b-x", "c-none", "d-off");
        (await List(new SkillQuery { Tags = ["X", " y "] })).Should().Equal("a-both");
        (await List(new SkillQuery { Tags = ["x", "y"], IncludeInactive = true })).Should().Equal("a-both", "d-off");
        (await List(new SkillQuery { Names = [SkillName.Parse("b-x"), SkillName.Parse("c-none")], Tags = ["x"] }))
            .Should().Equal("b-x");
        (await List(new SkillQuery { Names = [SkillName.Parse("d-off")] })).Should().BeEmpty("inactive is hidden");
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("at", _at.UtcDateTime);
        await command.ExecuteNonQueryAsync();
    }
}
