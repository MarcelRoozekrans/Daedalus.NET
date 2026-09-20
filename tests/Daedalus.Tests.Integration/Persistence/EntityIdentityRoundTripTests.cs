using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Persistence;

/// <summary>
///     Pins the part of <c>Entity&lt;TId&gt;</c> equality that actually protects EF Core change tracking:
///     loading the same row twice within one <c>ApplicationDbContext</c> must return the identity-mapped
///     same instance, that instance must still compare equal to itself, and its tracked
///     <see cref="EntityState"/> must be <see cref="EntityState.Unchanged"/> — i.e. nothing about a second
///     load is mistaken for a change. Companion to
///     <c>Daedalus.Tests.Unit.Domain.Entities.EntityEqualityCharacterisationTests</c>, which pins
///     the equality contract in isolation; this test exercises it against a real PostgreSQL database via
///     the same fixture pattern as <c>SchedulesControllerTests</c> and
///     <c>ScheduledRunConfigurationTests</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class EntityIdentityRoundTripTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Would break if: a replacement <c>Entity&lt;TId&gt;</c> compares by reference instead of by Id
    ///     (Equal(first, second) would still pass here since they're the same instance, but see the unit
    ///     characterisation tests for that distinction), hardcodes <see cref="Guid"/> so a differently-keyed
    ///     entity fails to compile, or changes <c>GetHashCode</c>/<c>Equals</c> in a way that makes EF Core's
    ///     materialisation or change-detection throw or misclassify the entry's state.
    /// </summary>
    [Fact]
    public async Task An_entity_loaded_twice_in_one_context_is_the_same_tracked_instance()
    {
        Guid id;
        await using (var seedContext = fixture.CreateDbContext())
        {
            var run = ScheduledRun.Create(
                "identity-map-check", "0 7 * * *", "RepoDigest", "telegram", "482910337",
                "schedule:identity-map-check", ["reader"], ScheduleOrigin.Config,
                new DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc)).Value;

            seedContext.ScheduledRuns.Add(run);
            await seedContext.SaveChangesAsync();
            id = run.Id;
        }

        await using var context = fixture.CreateDbContext();
        var first = await context.ScheduledRuns.FindAsync(id);
        var second = await context.ScheduledRuns.FindAsync(id);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(first, second);
        Assert.Equal(EntityState.Unchanged, context.Entry(first!).State);
    }
}
