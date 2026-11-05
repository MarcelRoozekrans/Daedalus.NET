using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Thalos;
using Thalos.Workflow;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     The backfill's run query and its per-run isolation, against the real <c>workflow_run_event</c> the fixture's
///     migrations create. The rows hold only the columns the query reads and the table requires; the event bodies come
///     from a fake history.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class NodeUsageBackfillIsolationTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly TurnUsage Usage = new(100, 10, "m");
    private static readonly DateTimeOffset At = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public async Task DisposeAsync() => await fixture.DatabaseResetter.ResetAsync();

    /// <summary>
    ///     A run whose history cannot be read is skipped, and the runs after it are still backfilled.
    ///     Red: remove the per-run catch in <c>BackfillAsync</c>; the exception escapes and the later runs get no record.
    /// </summary>
    [Fact]
    public async Task A_run_that_cannot_be_read_does_not_stop_the_runs_after_it()
    {
        var runs = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var run in runs)
        {
            await AddCompletionAsync(run);
        }

        var history = Substitute.For<IWorkflowRunHistory>();
        var requested = new List<Guid>();
        history.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var id = call.Arg<Guid>();
            requested.Add(id);
            return requested.Count == 1
                ? throw new InvalidOperationException("malformed event")
                : new ValueTask<IReadOnlyList<WorkflowRunEvent>>([Completion()]);
        });
        var (backfill, store) = NewBackfill(history);

        var written = await backfill.BackfillAsync(CancellationToken.None);

        written.Should().Be(2);
        requested.Should().HaveCount(3);
        foreach (var id in requested.Skip(1))
        {
            (await store.ListAsync(id, WorkflowRunRecord.NodeUsageKind, CancellationToken.None)).Should().ContainSingle();
        }

        (await store.ListAsync(requested[0], WorkflowRunRecord.NodeUsageKind, CancellationToken.None)).Should().BeEmpty();
    }

    /// <summary>
    ///     The run query names only runs with a completion that lacks its record, so a finished backfill leaves nothing to scan.
    ///     Red: drop the <c>NOT EXISTS</c> clause; the fully recorded run is listed and its history is read.
    /// </summary>
    [Fact]
    public async Task A_fully_recorded_run_is_not_read_again()
    {
        var recorded = Guid.NewGuid();
        var pending = Guid.NewGuid();
        await AddCompletionAsync(recorded);
        await AddCompletionAsync(pending);
        await ExecuteAsync(
            "INSERT INTO \"WorkflowRunRecords\" (\"RunId\", \"Seq\", \"Node\", \"Kind\", \"PrincipalId\", \"PayloadJson\", \"CreatedAt\") " +
            $"VALUES ('{recorded}', 2, 'implement', '{WorkflowRunRecord.NodeUsageKind}', 'host', '{{}}', now())");
        var history = Substitute.For<IWorkflowRunHistory>();
        history.ListEventsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<WorkflowRunEvent>>([Completion()]));
        var (backfill, _) = NewBackfill(history);

        await backfill.BackfillAsync(CancellationToken.None);

        await history.DidNotReceive().ListEventsAsync(recorded, Arg.Any<CancellationToken>());
        await history.Received(1).ListEventsAsync(pending, Arg.Any<CancellationToken>());
    }

    /// <summary>A run and one completion event of it, with the columns the real tables require; the event's usage is empty.</summary>
    private async Task AddCompletionAsync(Guid run)
    {
        await ExecuteAsync(
            "INSERT INTO workflow_run (id, process, process_version, correlation_key, current_node, current_seq, status, visits) " +
            $"VALUES ('{run}', 'manufacture', 1, '{run}', 'implement', 2, 'Running', '{{}}')");
        await ExecuteAsync(
            "INSERT INTO workflow_run_event (run_id, seq, kind, from_node, to_node, status, usage) " +
            $"VALUES ('{run}', 2, 'NodeCompleted', 'implement', 'review', 'Running', '{{}}')");
    }

    private static WorkflowRunEvent Completion() =>
        new(2, "NodeCompleted", "implement", "review", "Running", null, null, Usage, At, null);

    private (NodeUsageBackfill Backfill, IWorkflowRunRecordStore Store) NewBackfill(IWorkflowRunHistory history)
    {
        IWorkflowRunRecordStore store = new WorkflowRunRecordStore(new FixtureDbContextFactory(fixture));
        var scopes = new ServiceCollection().AddSingleton(store).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var recorder = new NodeUsageRecorder(scopes, TimeProvider.System, NullLogger<NodeUsageRecorder>.Instance);
        var backfill = new NodeUsageBackfill(
            NpgsqlDataSource.Create(fixture.ConnectionString), Substitute.For<IWorkflowStore>(), history, scopes, recorder,
            NullLogger<NodeUsageBackfill>.Instance);
        return (backfill, store);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }
}
