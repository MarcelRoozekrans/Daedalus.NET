using Daedalus.Agents.Charters;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.RoleCharters;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Thalos;
using Thalos.Skills.Charters;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Phase 3.5: <see cref="PostgresRoleCharterStore"/> on ZeroAlloc.ORM stores every column of a charter version
///     and its head, loads a row whatever <c>RoleCharterVersion.Create</c> would say of it today, and answers a lost
///     check-then-insert race as it always did.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresRoleCharterStoreRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset _at = new(2026, 11, 5, 9, 15, 30, TimeSpan.Zero);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Every field of a charter: stored through the store, listed back identical, active, with its skill names in
    ///     order. Red: leave any column out of the insert or the read and the comparison names it.
    /// </summary>
    [Fact]
    public async Task Stored_charter_loads_back_whole()
    {
        var store = new PostgresRoleCharterStore(fixture.Db, TimeProvider.System);
        var charter = new RoleCharter
        {
            Role = "reviewer",
            Description = "Reviews a change before it merges",
            Instructions = "Read the diff.\nThen review it.",
            Model = "claude-sonnet-5-5",
            Skills = ["manufacture-review", "manufacture-retrospect"],
            SourcePath = "roles/reviewer.md",
            ContentHash = "hash-1",
            IsActive = false,
            UpdatedAt = _at,
        };

        (await store.UpsertAsync(charter, CancellationToken.None)).IsSuccess.Should().BeTrue();
        var loaded = (await store.ListVersionsAsync(CancellationToken.None)).Value;

        loaded.Should().ContainSingle().Which.Should().BeEquivalentTo(charter with { IsActive = true });
    }

    /// <summary>
    ///     The two aggregates themselves: a stored version and a stored head come back equal in every property.
    ///     Red: read any column wrongly, and the comparison names it.
    /// </summary>
    [Fact]
    public async Task Stored_aggregates_load_back_whole()
    {
        var version = RoleCharterVersion.Create(
            "reviewer", "hash-1", "Reviews", "Read the diff.", "claude-sonnet-5-5", ["a", "b"], "roles/reviewer.md",
            _at.UtcDateTime).Value;
        var head = RoleCharterHead.Create("reviewer", "hash-1", _at.UtcDateTime);
        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new RoleCharterSql(connection);

        await sql.StoreAsync(version, head, headExists: false, CancellationToken.None);
        var versions = await sql.ListVersionsAsync(CancellationToken.None);
        var loadedHead = await sql.FindHeadAsync("reviewer", CancellationToken.None);

        versions.Should().ContainSingle().Which.Should()
            .BeEquivalentTo(version, options => options.ComparingByMembers<RoleCharterVersion>());
        loadedHead.Should().BeEquivalentTo(head, options => options.ComparingByMembers<RoleCharterHead>());
        versions[0].UpdatedAt.Kind.Should().Be(DateTimeKind.Utc);
        (await sql.VersionExistsAsync("reviewer", "hash-1", CancellationToken.None)).Should().BeTrue();
        (await sql.VersionExistsAsync("reviewer", "hash-2", CancellationToken.None)).Should().BeFalse();
    }

    /// <summary>
    ///     Review focus 1: a version <c>Create</c> refuses, an uppercase role, an empty description and source path
    ///     and no skills here, still loads, as written, under a head that points at it. Red: build the loaded row
    ///     through <c>Create</c>, and the load fails.
    /// </summary>
    [Fact]
    public async Task A_row_Create_rejects_still_loads()
    {
        RoleCharterVersion.Create("Legacy Role", "legacy-hash", "", "body", null, [], "", _at.UtcDateTime).IsFailure
            .Should().BeTrue("the row below is one Create refuses");
        await ExecuteAsync(
            """
            INSERT INTO "RoleCharterVersions"
                ("Role", "ContentHash", "Description", "Instructions", "Model", "SourcePath", "UpdatedAt", "Skills")
            VALUES ('Legacy Role', 'legacy-hash', '', 'body', NULL, '', @at, '{}');
            INSERT INTO "RoleCharters" ("Role", "CurrentHash", "IsActive", "UpdatedAt")
            VALUES ('Legacy Role', 'legacy-hash', false, @at);
            """);

        var loaded = (await new PostgresRoleCharterStore(fixture.Db, TimeProvider.System)
            .ListVersionsAsync(CancellationToken.None)).Value;

        var charter = loaded.Should().ContainSingle().Subject;
        charter.Role.Should().Be("Legacy Role");
        charter.Description.Should().BeEmpty();
        charter.SourcePath.Should().BeEmpty();
        charter.Model.Should().BeNull();
        charter.Skills.Should().BeEmpty();
        charter.IsActive.Should().BeFalse();
        charter.UpdatedAt.Should().Be(_at);
    }

    /// <summary>
    ///     Two syncs can both see no head and both insert one: the loser's insert breaks the primary key and the
    ///     upsert answers SkillStoreFailed, as it did when EF's SaveChanges threw. A trigger plays the winner, failing
    ///     the store's own head insert with the primary-key violation the loser gets. The version and head commit
    ///     together or not at all, as EF's single SaveChanges did, so the loser's version row is gone too. Red: write
    ///     the two rows outside one transaction, and the version survives; catch nothing, and the Npgsql exception
    ///     escapes.
    /// </summary>
    [Fact]
    public async Task Upsert_loses_the_head_insert_race_with_SkillStoreFailed()
    {
        await ExecuteAsync(
            """
            CREATE FUNCTION race_head() RETURNS trigger AS $$
            BEGIN
                RAISE EXCEPTION 'duplicate key value violates unique constraint "PK_RoleCharters"'
                    USING ERRCODE = '23505';
            END $$ LANGUAGE plpgsql;
            CREATE TRIGGER race_head BEFORE INSERT ON "RoleCharters"
                FOR EACH ROW EXECUTE FUNCTION race_head();
            """);
        try
        {
            var store = new PostgresRoleCharterStore(fixture.Db, TimeProvider.System);
            var charter = new RoleCharter
            {
                Role = "reviewer",
                Description = "Reviews",
                Instructions = "Read.",
                SourcePath = "roles/reviewer.md",
                ContentHash = "loser-hash",
                UpdatedAt = _at,
            };

            var result = await store.UpsertAsync(charter, CancellationToken.None);

            result.IsFailure.Should().BeTrue();
            result.Error.Code.Should().Be(AgentErrorCode.SkillStoreFailed);
            result.Error.Message.Should().Be("Could not store the role charter.");
        }
        finally
        {
            await ExecuteAsync("""DROP TRIGGER race_head ON "RoleCharters"; DROP FUNCTION race_head();""");
        }

        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        var sql = new RoleCharterSql(connection);
        (await sql.FindHeadAsync("reviewer", CancellationToken.None)).Should().BeNull();
        (await sql.VersionExistsAsync("reviewer", "loser-hash", CancellationToken.None)).Should().BeFalse();
    }

    /// <summary>
    ///     The sweep's array parameter: heads outside the seen roles flip inactive and are stamped, heads inside stay,
    ///     and an empty list deactivates every active head. Red: invert the filter and the wrong heads flip.
    /// </summary>
    [Fact]
    public async Task Deactivate_missing_flips_only_active_heads_outside_the_seen_roles()
    {
        var clock = new FixedClock(_at.AddDays(1));
        var store = new PostgresRoleCharterStore(fixture.Db, clock);
        foreach (var role in new[] { "kept", "dropped", "already-off" })
        {
            await store.UpsertAsync(
                new RoleCharter
                {
                    Role = role,
                    Description = "d",
                    Instructions = "i",
                    SourcePath = $"roles/{role}.md",
                    ContentHash = $"{role}-hash",
                    UpdatedAt = _at,
                },
                CancellationToken.None);
        }

        await store.DeactivateMissingAsync(["kept", "dropped"], CancellationToken.None);
        clock.Now = _at.AddDays(2);
        await store.DeactivateMissingAsync(["kept"], CancellationToken.None);

        var byRole = (await store.ListVersionsAsync(CancellationToken.None)).Value.ToDictionary(c => c.Role, StringComparer.Ordinal);
        byRole["kept"].IsActive.Should().BeTrue();
        byRole["kept"].UpdatedAt.Should().Be(_at);
        byRole["dropped"].IsActive.Should().BeFalse();
        byRole["dropped"].UpdatedAt.Should().Be(_at.AddDays(2));
        byRole["already-off"].IsActive.Should().BeFalse();
        byRole["already-off"].UpdatedAt.Should().Be(_at.AddDays(1), "it was swept first and never stamped again");

        await store.DeactivateMissingAsync([], CancellationToken.None);
        (await store.ListVersionsAsync(CancellationToken.None)).Value.Should().OnlyContain(c => !c.IsActive);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("at", _at.UtcDateTime);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
