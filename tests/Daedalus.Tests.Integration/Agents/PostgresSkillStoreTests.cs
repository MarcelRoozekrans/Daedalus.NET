using Daedalus.Agents.Skills;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
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

    /// <summary>The store disposes every context it creates, so the factory needs no tracking.</summary>
    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
