using Daedalus.Agents.Charters;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Skills.Charters;
using Thalos.Testing;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Runs Thalos.NET's <see cref="IRoleCharterStore"/> contract suite against the Postgres-backed store. Tests
///     in the database collection run sequentially; the database is reset before each test and the base class
///     creates one store per test through <see cref="CreateStoreAsync"/>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresRoleCharterStoreTests(PostgresFixture fixture) : RoleCharterStoreContractTests, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // Upserts carry their own UpdatedAt (they are synced from files), but DeactivateMissingAsync stamps the
    // clock: the row records when its role stopped being seen, so the store needs the contract's clock.
    protected override ValueTask<IRoleCharterStore> CreateStoreAsync(TimeProvider clock) =>
        new(new PostgresRoleCharterStore(new FixtureDbContextFactory(fixture), clock));

    /// <summary>
    ///     Daedalus-specific: a charter that violates the aggregate's rules comes back as SkillValidationFailed,
    ///     not as a database constraint failure - the whole point of mirroring the Thalos limits in the Domain
    ///     layer (<see cref="RoleCharterVersion"/> mirrors <c>RoleCharterFileLoader.MaxFileBytes</c>'s intent for
    ///     description/model/source path length, even though the length limits themselves are Daedalus' own).
    /// </summary>
    [Fact]
    public async Task Upsert_of_an_over_size_description_is_a_validation_error()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var charter = NewCharter("reviewer", "hash-1") with { Description = new string('x', 301) };

        var result = await store.UpsertAsync(charter, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(AgentErrorCode.SkillValidationFailed);
    }

    /// <summary>
    ///     Daedalus-specific: <see cref="RoleCharter.Skills"/> round-trips through the <c>text[]</c> backing
    ///     column, the same shape <c>PostgresSkillStore</c> uses for a skill's tags.
    /// </summary>
    [Fact]
    public async Task Skills_round_trip_through_upsert_and_list()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var charter = NewCharter("reviewer", "hash-1") with { Skills = ["manufacture-review", "manufacture-retrospect"] };

        await store.UpsertAsync(charter, CancellationToken.None);

        var all = (await store.ListVersionsAsync(CancellationToken.None)).Value;
        all.Single().Skills.Should().Equal("manufacture-review", "manufacture-retrospect");
    }

    /// <summary>Daedalus-specific: a charter with no <c>Model</c> (the host default) stores and reads back null, not an empty string.</summary>
    [Fact]
    public async Task A_charter_with_no_model_round_trips_null()
    {
        var clock = NewClock();
        var store = await CreateStoreAsync(clock);
        var charter = NewCharter("implementer", "hash-1") with { Model = null };

        await store.UpsertAsync(charter, CancellationToken.None);

        var all = (await store.ListVersionsAsync(CancellationToken.None)).Value;
        all.Single().Model.Should().BeNull();
    }

    /// <summary>The store disposes every context it creates, so the factory needs no tracking.</summary>
    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
