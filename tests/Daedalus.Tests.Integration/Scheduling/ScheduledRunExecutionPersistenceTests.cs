using Daedalus.Domain.Entities;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Integration tests proving <see cref="ScheduledRunExecution"/> round-trips through Postgres and that the
///     unique <c>(ScheduleId, OccurrenceAt)</c> index — the saga's old correlation key, expressed as a database
///     constraint — actually rejects a second row for the same occurrence.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunExecutionPersistenceTests(PostgresFixture fixture) : IAsyncLifetime
{
    // Same value as Guid.Parse("0f1d8a2c-5e6b-4a71-9c3d-8b2f4e6a1c07"), spelled as byte components: this
    // project runs MA0176 ("optimize guid creation") as an error, unlike the sibling unit-test project.
    private static readonly Guid ScheduleId =
        new(0x0f1d8a2c, 0x5e6b, 0x4a71, 0x9c, 0x3d, 0x8b, 0x2f, 0x4e, 0x6a, 0x1c, 0x07);
    private static readonly DateTime Occurrence = new(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 17, 7, 0, 5, DateTimeKind.Utc);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_execution_round_trips_including_its_roles_and_its_step()
    {
        await using (var db = fixture.CreateContext())
        {
            db.ScheduledRunExecutions.Add(ScheduledRunExecution.Create(
                ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus",
                ["reader", "digest"], Now).Value);
            await db.SaveChangesAsync();
        }

        await using var read = fixture.CreateContext();
        var loaded = await read.ScheduledRunExecutions.SingleAsync(e => e.ScheduleId == ScheduleId);

        loaded.Roles.Should().BeEquivalentTo(["reader", "digest"]);
        loaded.Step.Should().Be(RunStep.Pending);
        loaded.OccurrenceAt.Should().Be(Occurrence);
    }

    [Fact]
    public async Task A_second_row_for_the_same_schedule_and_occurrence_is_rejected_by_the_database()
    {
        // the idempotency guarantee, enforced where no handler can forget it
        await using var db = fixture.CreateContext();
        db.ScheduledRunExecutions.Add(Execution());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        db.ScheduledRunExecutions.Add(Execution());
        var act = async () => await db.SaveChangesAsync();

        (await act.Should().ThrowAsync<DbUpdateException>()
                .WithInnerException<DbUpdateException, PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task A_different_occurrence_of_the_same_schedule_is_allowed()
    {
        await using var db = fixture.CreateContext();
        db.ScheduledRunExecutions.Add(Execution());
        db.ScheduledRunExecutions.Add(ScheduledRunExecution.Create(
            ScheduleId, Occurrence.AddDays(1), "telegram", "123456", "schedule:daedalus", ["reader"], Now).Value);

        await db.SaveChangesAsync();

        (await db.ScheduledRunExecutions.CountAsync()).Should().Be(2);
    }

    private static ScheduledRunExecution Execution() =>
        ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus", ["reader"], Now).Value;
}
