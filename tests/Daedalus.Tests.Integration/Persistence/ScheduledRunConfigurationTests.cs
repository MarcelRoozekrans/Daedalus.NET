using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Persistence;

/// <summary>
///     Round-trip test for the <see cref="ScheduledRun"/> aggregate's EF configuration
///     (<c>ScheduledRunConfiguration</c>), against a real PostgreSQL database. This lives in the integration suite
///     rather than <c>Daedalus.Tests.Unit.Infrastructure</c> because that project's fixture runs on the EF Core
///     InMemory provider, which cannot map the <c>xmin</c> row-version shadow property — a Postgres system column
///     this configuration relies on for optimistic concurrency.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunConfigurationTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_ScheduledRun_round_trips_including_its_roles()
    {
        await using var db = fixture.CreateDbContext();
        var run = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigestSaga", "telegram", "123456",
            "schedule:daily-digest", ["reader", "digest"], ScheduleOrigin.Config,
            new DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc)).Value;

        db.ScheduledRuns.Add(run);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var loaded = await db.ScheduledRuns.SingleAsync(r => r.Name == "daily-digest");
        loaded.Roles.Should().BeEquivalentTo(["reader", "digest"]);
        loaded.Origin.Should().Be(ScheduleOrigin.Config);
    }
}
