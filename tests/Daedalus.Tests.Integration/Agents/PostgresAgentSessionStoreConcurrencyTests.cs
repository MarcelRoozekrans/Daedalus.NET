using Daedalus.Agents.Sessions;
using Daedalus.Tests.Integration.Fixtures;
using Npgsql;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Agents;

/// <summary>
///     Phase 3.5, review focus 2 and 3: two writers on one agent session. Every session save is independent, so a turn's
///     counter increment and a state save never fail each other, and a state save on a session deleted under it answers
///     <c>SessionNotFound</c>.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PostgresAgentSessionStoreConcurrencyTests(PostgresFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private PostgresAgentSessionStore Store(TimeProvider clock) => new(fixture.Db, clock);

    /// <summary>
    ///     A turn recorded while the runtime saves the session's state: both land. The clock plays the other writer: the
    ///     store asks it for the time before it writes the state, and there the hook records a turn. The state save
    ///     succeeds, and the turn's counters stand. Red: guard the state save on the row version read before the clock,
    ///     and the save fails; or write the counters read before the clock, and the turn is lost.
    /// </summary>
    [Fact]
    public async Task Turn_counter_increment_does_not_fail_a_concurrent_state_save()
    {
        var clock = new HookClock();
        var store = Store(clock);
        var session = (await store.CreateAsync(new AgentId(Guid.NewGuid()), "owner-1", CancellationToken.None)).Value;
        clock.OnNextRead = () => Store(TimeProvider.System)
            .RecordTurnAsync(session.Id, new TurnUsage(100, 20, "model"), CancellationToken.None)
            .AsTask().GetAwaiter().GetResult();

        var saved = await store.UpdateStateAsync(session.Id, SessionState.Running, CancellationToken.None);

        clock.Fired.Should().BeTrue("the turn must land between the state save's start and its write");
        saved.IsSuccess.Should().BeTrue(saved.IsFailure ? saved.Error.Code.ToString() : string.Empty);
        var stored = (await store.GetAsync(session.Id, CancellationToken.None)).Value;
        stored.State.Should().Be(SessionState.Running);
        stored.TurnCount.Should().Be(1);
        stored.TotalInputTokens.Should().Be(100);
        stored.TotalOutputTokens.Should().Be(20);
    }

    /// <summary>
    ///     Review focus 3: a session deleted after the state save started and before it writes answers
    ///     <c>SessionNotFound</c>, since only a missing row gives an update no row; <c>SessionBusy</c> would send the
    ///     caller to retry a session that is gone. Red: answer zero rows with <c>SessionBusy</c>, as the EF store did.
    /// </summary>
    [Fact]
    public async Task Deleted_session_reports_not_found()
    {
        var clock = new HookClock();
        var store = Store(clock);
        var session = (await store.CreateAsync(new AgentId(Guid.NewGuid()), "owner-1", CancellationToken.None)).Value;
        clock.OnNextRead = () => DeleteSessionAsync(session.Id.Value).GetAwaiter().GetResult();

        var saved = await store.UpdateStateAsync(session.Id, SessionState.Running, CancellationToken.None);

        clock.Fired.Should().BeTrue("the delete must land between the state save's start and its write");
        saved.IsFailure.Should().BeTrue();
        saved.Error.Code.Should().Be(AgentErrorCode.SessionNotFound);
    }

    private async Task DeleteSessionAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""DELETE FROM "AgentSessions" WHERE "Id" = @id""", connection);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>A clock that runs a hook the first time the store reads it, then reads on as the fixed time.</summary>
    private sealed class HookClock : TimeProvider
    {
        public DateTimeOffset Now { get; init; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        public Action? OnNextRead { get; set; }

        public bool Fired { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            if (OnNextRead is { } hook)
            {
                OnNextRead = null;
                Fired = true;
                hook();
            }

            return Now;
        }
    }
}
