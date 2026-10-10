using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Workflow;

/// <summary>
///     Phase 2.8: at most one <c>node-usage</c> record per (run, seq) is the database's rule, a partial unique index on
///     <c>WorkflowRunRecords</c>, and <see cref="NodeUsageRecorder"/> reads a violation of it as "already recorded".
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class NodeUsageUniqueIndexTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly Guid _runId = new(0x7a1c3e55, 0x2b4d, 0x4c6e, 0x9f, 0x01, 0x3d, 0x5a, 0x7b, 0x9c, 0x1e, 0x22);
    private static readonly DateTime _at = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TurnUsage _usage = new(InputTokens: 100, OutputTokens: 10, ModelId: "m");

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    ///     Two appends for one (run, seq) leave one record, the first one's, and the second neither throws nor logs a
    ///     warning or error: it is reported as not written and logged at Debug.
    ///     Red: drop the unique index from <c>WorkflowRunRecordConfiguration</c> and the migration; the second append
    ///     writes, and the single-record assertion fails.
    ///     Red: remove the <c>IsAlreadyRecorded</c> catch in <c>NodeUsageRecorder</c>; the violation then logs at Error,
    ///     and the no-warning assertion fails.
    /// </summary>
    [Fact]
    public async Task A_second_append_for_the_same_run_and_seq_is_already_recorded_not_an_error()
    {
        var (recorder, store, log) = NewRecorder();

        var first = await recorder.RecordAsync(_runId, 4, "implement", "starter", _usage, _at, CancellationToken.None);
        var second = await recorder.RecordAsync(_runId, 4, "implement", "starter", _usage with { InputTokens = 999 }, _at, CancellationToken.None);

        first.Should().BeTrue();
        second.Should().BeFalse("the completion is already recorded");
        var records = await store.ListAsync(_runId, WorkflowRunRecord.NodeUsageKind, CancellationToken.None);
        records.Should().ContainSingle().Which.PayloadJson.Should().Contain("100", "the first record stands");
        log.Levels.Should().NotContain(l => l >= LogLevel.Warning, "a duplicate is the expected outcome of a race, not a fault");
        log.Levels.Should().Contain(LogLevel.Debug);
    }

    /// <summary>
    ///     The index is partial: other kinds may share a (run, seq), and another seq of the same run is free.
    ///     Red: remove <c>HasFilter</c> from the index; the second workspace-write at the same seq violates it and throws.
    /// </summary>
    [Fact]
    public async Task The_index_binds_only_node_usage_records()
    {
        var (recorder, store, _) = NewRecorder();
        static WorkflowRunRecord Write() =>
            WorkflowRunRecord.Create(_runId, 4, "implement", WorkflowRunRecord.WorkspaceWriteKind, "p", null, "{}", _at).Value;
        await recorder.RecordAsync(_runId, 4, "implement", null, _usage, _at, CancellationToken.None);

        await store.AppendAsync(Write(), CancellationToken.None);
        await store.AppendAsync(Write(), CancellationToken.None);
        var otherSeq = await recorder.RecordAsync(_runId, 5, "implement", null, _usage, _at, CancellationToken.None);

        otherSeq.Should().BeTrue();
        (await store.ListAsync(_runId, WorkflowRunRecord.WorkspaceWriteKind, CancellationToken.None)).Should().HaveCount(2);
        (await store.ListAsync(_runId, WorkflowRunRecord.NodeUsageKind, CancellationToken.None)).Should().HaveCount(2);
    }

    private (NodeUsageRecorder Recorder, IWorkflowRunRecordStore Store, CapturingLogger<NodeUsageRecorder> Log) NewRecorder()
    {
        IWorkflowRunRecordStore store = new WorkflowRunRecordStore(new FixtureDbContextFactory(fixture));
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        var log = new CapturingLogger<NodeUsageRecorder>();
        return (new NodeUsageRecorder(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, log), store, log);
    }

    private sealed class FixtureDbContextFactory(PostgresFixture fixture) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => fixture.CreateDbContext();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
