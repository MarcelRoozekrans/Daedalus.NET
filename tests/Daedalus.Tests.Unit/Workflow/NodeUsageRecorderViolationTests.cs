using Daedalus.Agents.Workflow;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Thalos;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Unit.Workflow;

/// <summary>
///     Only the node-usage unique index means "already recorded". A unique violation on any other constraint is a fault
///     the recorder must still report as one.
/// </summary>
public sealed class NodeUsageRecorderViolationTests
{
    private static readonly TurnUsage Usage = new(100, 10, "m");

    /// <summary>
    ///     Red: drop the <c>ConstraintName</c> comparison from <c>IsAlreadyRecorded</c>; the other constraint reads as
    ///     already recorded, logs at Debug, and the Error assertion fails.
    /// </summary>
    [Fact]
    public async Task A_unique_violation_on_another_constraint_is_a_failure_not_already_recorded()
    {
        var violation = new PostgresException(
            "duplicate key", "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation, constraintName: "PK_WorkflowRunRecords");
        var records = Substitute.For<IWorkflowRunRecordStore>();
        records.AppendAsync(Arg.Any<WorkflowRunRecord>(), Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new DbUpdateException("save failed", violation));
        var scopes = new ServiceCollection().AddSingleton(records).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var logger = new LevelLogger();
        var recorder = new NodeUsageRecorder(scopes, TimeProvider.System, logger);

        var written = await recorder.RecordAsync(Guid.NewGuid(), 4, "implement", null, Usage, DateTime.UtcNow, CancellationToken.None);

        written.Should().BeFalse();
        logger.Levels.Should().Contain(LogLevel.Error, "a violation of another constraint is a fault, not a duplicate");
    }

    private sealed class LevelLogger : ILogger<NodeUsageRecorder>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
