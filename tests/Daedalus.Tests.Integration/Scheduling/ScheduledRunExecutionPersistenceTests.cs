using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm.ScheduledRunExecutions;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Integration tests proving <see cref="ScheduledRunExecution"/> round-trips through Postgres and that the
///     unique <c>(ScheduleId, OccurrenceAt)</c> index — the saga's old correlation key, expressed as a database
///     constraint — actually rejects a second row for the same occurrence. Ported from EF Core to
///     <see cref="ScheduledRunExecutionSql"/> in Task 21 (ruling R125); the assertions are the ones EF's tests made.
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
        await ScheduleTables.InsertAsync(fixture, ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus",
            ["reader", "digest"], Now).Value);

        var loaded = await ScheduleTables.SingleExecutionAsync(fixture);

        loaded.Roles.Should().BeEquivalentTo(["reader", "digest"]);
        loaded.Step.Should().Be(RunStep.Pending);
        loaded.OccurrenceAt.Should().Be(Occurrence);
    }

    [Fact]
    public async Task A_failed_execution_round_trips_the_step_it_failed_at()
    {
        var execution = Execution();
        execution.BeginScout(Now);
        execution.Fail("scout timed out", Now);
        await ScheduleTables.InsertAsync(fixture, execution);

        var loaded = await ScheduleTables.SingleExecutionAsync(fixture);

        loaded.Step.Should().Be(RunStep.Failed);
        loaded.FailedAtStep.Should().Be(RunStep.Scout);
    }

    [Fact]
    public async Task A_second_row_for_the_same_schedule_and_occurrence_is_rejected_by_the_database()
    {
        // The idempotency guarantee, enforced where no handler can forget it. The claim's insert absorbs the
        // conflict (it answers false), so the index itself is pinned with a plain INSERT that has no ON CONFLICT.
        await ScheduleTables.InsertAsync(fixture, Execution());

        var act = async () => await InsertWithoutConflictClauseAsync(Execution());

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        (await ScheduleTables.ExecutionCountAsync(fixture)).Should().Be(1);
    }

    [Fact]
    public async Task The_claim_insert_answers_false_for_an_occurrence_that_is_already_taken()
    {
        await ScheduleTables.InsertAsync(fixture, Execution());

        await using var connection = await fixture.Db.OpenAsync(CancellationToken.None);
        await using var tx = await connection.BeginTransactionAsync(CancellationToken.None);
        var inserted = await new ScheduledRunExecutionSql(connection).TryInsertAsync(Execution(), tx, CancellationToken.None);
        await tx.CommitAsync(CancellationToken.None);

        inserted.Should().BeFalse();
        (await ScheduleTables.ExecutionCountAsync(fixture)).Should().Be(1);
    }

    [Fact]
    public async Task A_different_occurrence_of_the_same_schedule_is_allowed()
    {
        await ScheduleTables.InsertAsync(
            fixture,
            Execution(),
            ScheduledRunExecution.Create(
                ScheduleId, Occurrence.AddDays(1), "telegram", "123456", "schedule:daedalus", ["reader"], Now).Value);

        (await ScheduleTables.ExecutionCountAsync(fixture)).Should().Be(2);
    }

    private async Task InsertWithoutConflictClauseAsync(ScheduledRunExecution execution)
    {
        await using var command = fixture.DataSource.CreateCommand("""
            INSERT INTO "ScheduledRunExecutions"
                ("Id", "ScheduleId", "OccurrenceAt", "Step", "Findings", "Digest", "ChannelId", "ConversationId",
                 "PrincipalId", "Roles", "Attempts", "CreatedAt", "UpdatedAt")
            VALUES (@id, @scheduleId, @occurrenceAt, 'Pending', NULL, NULL, 'telegram', '123456',
                    'schedule:daedalus', 'reader', 0, @now, @now)
            """);
        command.Parameters.AddWithValue("id", execution.Id);
        command.Parameters.AddWithValue("scheduleId", execution.ScheduleId);
        command.Parameters.AddWithValue("occurrenceAt", execution.OccurrenceAt);
        command.Parameters.AddWithValue("now", Now);
        await command.ExecuteNonQueryAsync();
    }

    private static ScheduledRunExecution Execution() =>
        ScheduledRunExecution.Create(
            ScheduleId, Occurrence, "telegram", "123456", "schedule:daedalus", ["reader"], Now).Value;
}
