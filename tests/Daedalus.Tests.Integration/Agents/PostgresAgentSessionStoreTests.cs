using Daedalus.Agents.Sessions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Thalos;
using Thalos.Testing;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Runs Thalos.NET's <see cref="IAgentSessionStore"/> contract suite against the real Postgres-backed store.
///     Tests in the database collection run sequentially; the database is reset before each test, and the base
///     class creates one store per test through <see cref="CreateStoreAsync"/> with a fake clock it advances itself.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresAgentSessionStoreTests(PostgresFixture fixture) : SessionStoreContractTests, IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    protected override ValueTask<IAgentSessionStore> CreateStoreAsync(TimeProvider clock)
    {
        // The store disposes every context it creates, so the factory needs no tracking.
        var factory = new TestDbContextFactory(fixture.ConnectionString);
        return new(new PostgresAgentSessionStore(factory, clock));
    }

    /// <summary>
    ///     The adapter converts between the two enums by integer cast; a drift in either enum must break loudly.
    /// </summary>
    [Fact]
    public void Domain_AgentSessionState_mirrors_Thalos_SessionState()
    {
        var thalos = Enum.GetValues<SessionState>().Select(v => ((int)v, v.ToString())).ToList();
        var domain = Enum.GetValues<AgentSessionState>().Select(v => ((int)v, v.ToString())).ToList();

        domain.Should().Equal(thalos);
    }

    private sealed class TestDbContextFactory(string connectionString) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseVector())
                .Options);
    }
}
