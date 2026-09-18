using System.Data.Common;
using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     The eight verdicts of <see cref="ScheduleDiagnostics"/>, against a real PostgreSQL database and the real,
///     DI-resolved <see cref="IOutboxSerializer"/> — never an assumed wire format, the same rule
///     <see cref="ScheduledRunExecutionStoreTests"/> follows.
/// </summary>
/// <remarks>
///     <para>
///     Three boundaries here are where a wrong answer is actively misleading rather than merely absent, and each
///     is tested on both sides:
///     </para>
///     <list type="bullet">
///         <item>
///         <b><see cref="RunVerdict.Delivered"/> versus <see cref="RunVerdict.Undelivered"/>.</b> Dispatched
///         outbox rows may be pruned, so "no row found" and "no dead letter found" are different questions.
///         An implementation that conflated them would report every old successful run as undelivered, so two
///         tests pin it: one where the row survives in a non-dead-letter status, and one where it is gone
///         entirely. Both must read <see cref="RunVerdict.Delivered"/>.
///         </item>
///         <item>
///         <b><see cref="RunVerdict.Running"/> versus <see cref="RunVerdict.Stranded"/>.</b> A threshold driven
///         by the injected <see cref="TimeProvider"/>, exercised exactly at the boundary and one second past it
///         rather than only in the middle, where an off-by-a-comparison implementation would still pass.
///         </item>
///         <item>
///         <b><see cref="RunVerdict.DeliveryUnknown"/> versus <see cref="RunVerdict.Delivered"/>.</b> A page must
///         never claim a delivery it could not confirm, so a failing outbox read has to degrade rather than fall
///         through to the happy answer.
///         </item>
///     </list>
///     <para>
///     <see cref="_strandedAfter"/> and <see cref="_deadLetterLookback"/> are deliberately far from the option
///     defaults, so a test that silently fell back to a production default would still be visibly wrong.
///     </para>
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduleDiagnosticsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _now = new(2026, 9, 18, 7, 30, 0, DateTimeKind.Utc);

    // Nowhere near ScheduleDiagnosticsOptions' own 15-minute and 30-day defaults, on purpose.
    private static readonly TimeSpan _strandedAfter = TimeSpan.FromMinutes(11);
    private static readonly TimeSpan _deadLetterLookback = TimeSpan.FromHours(37);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(_now));

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_schedule_not_yet_due_with_no_execution_is_NotYetDue()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.NotYetDue);
        diagnosis.ScheduleId.Should().Be(schedule.Id);
        diagnosis.ScheduleName.Should().Be("morning-digest");
        diagnosis.OccurrenceAtUtc.Should().Be(_now.AddHours(1), "the occurrence being diagnosed is the one that has not happened yet");
        diagnosis.ExecutionId.Should().Be(Guid.Empty, "no execution row exists to identify");
        diagnosis.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task A_schedule_due_in_the_past_with_no_execution_is_Overdue()
    {
        await SeedScheduleAsync("morning-digest", _now.AddHours(-1));

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Overdue,
            "the sweeper should have claimed this occurrence and no execution row exists at all");
        diagnosis.OccurrenceAtUtc.Should().Be(_now.AddHours(-1));
        diagnosis.ExecutionId.Should().Be(Guid.Empty);
    }

    [Fact]
    public async Task A_recently_updated_non_terminal_execution_is_Running()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));

        // Exactly ON the threshold, not comfortably inside it: the classifier's comparison is strictly greater
        // than, so an implementation that used >= instead would flip this row to Stranded and fail here.
        var execution = NewExecution(schedule, _now.AddHours(-2));
        execution.BeginScout(_now - _strandedAfter);
        await InsertAsync(execution);

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Running);
        diagnosis.StepReached.Should().Be((int)RunStep.Scout);
        diagnosis.UpdatedAtUtc.Should().Be(_now - _strandedAfter);
        diagnosis.ExecutionId.Should().Be(execution.Id);
    }

    [Fact]
    public async Task A_stale_non_terminal_execution_is_Stranded()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));

        // One second past the same threshold the Running test sits exactly on. The pair brackets the boundary,
        // and neither alone would catch a comparison that is off by one side.
        var execution = NewExecution(schedule, _now.AddHours(-2));
        execution.BeginScout(_now - _strandedAfter - TimeSpan.FromSeconds(1));
        await InsertAsync(execution);

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Stranded);
        diagnosis.StepReached.Should().Be((int)RunStep.Scout);
    }

    [Fact]
    public async Task A_failed_execution_is_Failed_and_carries_the_step_it_failed_at()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));

        // Failed at Writer, not at Scout: FailedAtStep must be the step the run actually died at, so a
        // hard-coded or Step-derived value would be visibly wrong rather than accidentally right.
        var execution = NewExecution(schedule, _now.AddHours(-2));
        execution.BeginScout(_now.AddMinutes(-40));
        execution.RecordFindings("three open PRs", _now.AddMinutes(-35));
        execution.Fail("the writer subagent exceeded its token budget", _now.AddMinutes(-30));
        await InsertAsync(execution);

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Failed);
        diagnosis.StepReached.Should().Be((int)RunStep.Failed);
        diagnosis.FailedAtStep.Should().Be((int)RunStep.Writer);
        diagnosis.LastError.Should().Be("the writer subagent exceeded its token budget");
        diagnosis.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task A_done_execution_whose_message_dead_lettered_is_Undelivered()
    {
        var dead = await SeedScheduleAsync("morning-digest", _now.AddHours(1));
        var live = await SeedScheduleAsync("weekly-digest", _now.AddHours(1));
        var deadExecution = await SeedDoneExecutionAsync(dead, _now.AddHours(-2));
        var liveExecution = await SeedDoneExecutionAsync(live, _now.AddHours(-2));

        await using var provider = BuildProvider();
        await QueueMessageAsync(provider, deadExecution.Id);
        await QueueMessageAsync(provider, liveExecution.Id);
        await DeadLetterAsync(provider, deadExecution.Id, "Telegram returned 429 after 8 attempts", retryCount: 7);

        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Single(d => d.ScheduleId == dead.Id);
        diagnosis.Verdict.Should().Be(RunVerdict.Undelivered, "the run completed but its channel message died in the outbox");
        diagnosis.StepReached.Should().Be((int)RunStep.Done);
        diagnosis.DeadLetterError.Should().Be("Telegram returned 429 after 8 attempts");
        diagnosis.RetryCount.Should().Be(7);

        // The other schedule's own completed run shares the table and the type name and differs only by the
        // ExecutionId inside the payload. A lookup that matched "any dead letter of this type exists" rather
        // than this execution's own would condemn it too.
        overview.Single(d => d.ScheduleId == live.Id).Verdict.Should().Be(RunVerdict.Delivered);
    }

    [Fact]
    public async Task A_done_execution_with_no_dead_letter_is_Delivered()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));
        var execution = await SeedDoneExecutionAsync(schedule, _now.AddHours(-2));

        await using var provider = BuildProvider();

        // The row is right there, in a non-dead-letter status. Absence of a DEAD LETTER is what means delivered.
        await QueueMessageAsync(provider, execution.Id);
        await SetStatusAsync(provider, execution.Id, OutboxMessageStatus.Succeeded);

        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Delivered);
        diagnosis.DeadLetterError.Should().BeNull();
        diagnosis.RetryCount.Should().BeNull();
    }

    [Fact]
    public async Task A_done_execution_is_Delivered_when_its_outbox_row_was_pruned()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));
        await SeedDoneExecutionAsync(schedule, _now.AddHours(-2));

        await using var provider = BuildProvider();

        // Nothing at all in OutboxMessages: the dispatched row has been pruned, which is the ordinary fate of an
        // old successful delivery. "No row found" is NOT "dead-lettered"; conflating the two would report every
        // old successful run as Undelivered, which is the single most misleading answer this page could give.
        (await OutboxRowCountAsync()).Should().Be(0);

        var overview = await OverviewAsync(provider);

        overview.Should().ContainSingle().Subject.Verdict.Should().Be(RunVerdict.Delivered);
    }

    [Fact]
    public async Task A_done_execution_is_DeliveryUnknown_when_the_outbox_read_throws()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));
        await SeedDoneExecutionAsync(schedule, _now.AddHours(-2));

        // Every read of OutboxMessages fails; reads of the two scheduling tables still succeed. Degrade, never
        // blank: the page keeps reporting the run, and never claims a delivery it could not confirm.
        await using var provider = BuildProvider(new ThrowOnOutboxReadInterceptor());
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().NotBe(RunVerdict.Delivered, "an unreadable outbox is not evidence of delivery");
        diagnosis.Verdict.Should().Be(RunVerdict.DeliveryUnknown);
        diagnosis.StepReached.Should().Be((int)RunStep.Done, "the rest of the diagnosis still has to be reported");
    }

    [Fact]
    public async Task A_dead_letter_whose_payload_will_not_deserialize_is_skipped_not_thrown_on()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));
        var execution = await SeedDoneExecutionAsync(schedule, _now.AddHours(-2));

        await using var provider = BuildProvider();
        await QueueMessageAsync(provider, execution.Id);
        await DeadLetterAsync(provider, execution.Id, "Telegram returned 429 after 8 attempts", retryCount: 7);

        // Newer than the real one, so a scan that aborted on the first unreadable payload would never reach the
        // row that actually answers the question.
        await InsertUnreadableDeadLetterAsync(_now.AddMinutes(-1));

        var overview = await OverviewAsync(provider);

        overview.Should().ContainSingle().Subject.Verdict.Should().Be(RunVerdict.Undelivered,
            "a payload that will not deserialize is skipped, and the scan continues");
    }

    [Fact]
    public async Task The_overview_issues_one_query_per_table_not_one_per_schedule()
    {
        var oneSchedule = await MeasureOverviewQueriesAsync(scheduleCount: 1);
        await fixture.DatabaseResetter.ResetAsync();
        var manySchedules = await MeasureOverviewQueriesAsync(scheduleCount: 5);

        manySchedules.Should().Be(oneSchedule,
            "the overview takes the latest execution per schedule and one bounded dead-letter fetch; an N+1 " +
            "would grow with the number of schedules and only become visible once there were enough to matter");
        oneSchedule.Should().BeLessThanOrEqualTo(3, "there are three tables to read: schedules, executions, outbox");
    }

    [Fact]
    public async Task The_run_history_returns_the_most_recent_runs_first_each_with_its_own_verdict()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));

        var oldest = NewExecution(schedule, _now.AddHours(-6));
        oldest.BeginScout(_now.AddHours(-6));
        oldest.Fail("the scout subagent timed out", _now.AddHours(-6));
        await InsertAsync(oldest);

        var middle = await SeedDoneExecutionAsync(schedule, _now.AddHours(-4));
        var newest = await SeedDoneExecutionAsync(schedule, _now.AddHours(-2));

        await using var provider = BuildProvider();
        await QueueMessageAsync(provider, middle.Id);
        await DeadLetterAsync(provider, middle.Id, "Telegram returned 429 after 8 attempts", retryCount: 7);

        await using var scope = provider.CreateAsyncScope();
        var history = await scope.ServiceProvider.GetRequiredService<IScheduleDiagnostics>()
            .GetRunHistoryAsync(schedule.Id, take: 10, CancellationToken.None);

        history.Select(h => h.ExecutionId).Should().Equal(newest.Id, middle.Id, oldest.Id);
        history.Select(h => h.Verdict).Should().Equal(RunVerdict.Delivered, RunVerdict.Undelivered, RunVerdict.Failed);
        history[2].FailedAtStep.Should().Be((int)RunStep.Scout);
        history.Should().OnlyContain(h => h.ScheduleName == "morning-digest");
    }

    // ---- harness ------------------------------------------------------------------------------------------

    /// <summary>Seeds <paramref name="scheduleCount"/> schedules, each with one completed run, and counts the reads one overview issues.</summary>
    private async Task<int> MeasureOverviewQueriesAsync(int scheduleCount)
    {
        for (var i = 0; i < scheduleCount; i++)
        {
            var schedule = await SeedScheduleAsync($"digest-{i}", _now.AddHours(1));
            await SeedDoneExecutionAsync(schedule, _now.AddHours(-2));
        }

        var counter = new CommandCountingInterceptor();
        await using var provider = BuildProvider(counter);
        var overview = await OverviewAsync(provider);

        overview.Should().HaveCount(scheduleCount);
        return counter.Count;
    }

    private static async Task<IReadOnlyList<RunDiagnosis>> OverviewAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        var diagnostics = scope.ServiceProvider.GetRequiredService<IScheduleDiagnostics>();
        return await diagnostics.GetOverviewAsync(CancellationToken.None);
    }

    /// <summary>
    ///     A provider wired the way production is: a scoped <see cref="ApplicationDbContext"/> shared by the
    ///     diagnostics service and the real generated <c>IOutboxWriter&lt;ChannelMessageQueued&gt;</c> the seeds
    ///     use, so the payloads under test are the ones production actually writes.
    /// </summary>
    private ServiceProvider BuildProvider(params IInterceptor[] interceptors)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContext<ApplicationDbContext>(o =>
        {
            o.UseNpgsql(fixture.ConnectionString);
            if (interceptors.Length > 0)
            {
                o.AddInterceptors(interceptors);
            }
        });
        services.AddOutbox(o => o.PollingInterval = TimeSpan.FromHours(1))
            .WithEfCore<ApplicationDbContext>()
            .AddChannelMessageQueuedOutbox();
        services.Configure<ScheduleDiagnosticsOptions>(o =>
        {
            o.StrandedAfter = _strandedAfter;
            o.DeadLetterLookback = _deadLetterLookback;
        });
        services.AddScoped<IScheduleDiagnostics, ScheduleDiagnostics>();

        return services.BuildServiceProvider();
    }

    private async Task<ScheduledRun> SeedScheduleAsync(string name, DateTime nextRunAtUtc)
    {
        var schedule = ScheduledRun.Create(
            name, "0 7 * * *", "RepoDigest", "telegram", "482910337",
            "schedule:daedalus", ["reader", "writer"], ScheduleOrigin.Config, nextRunAtUtc).Value;

        await using var db = fixture.CreateDbContext();
        db.ScheduledRuns.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    private static ScheduledRunExecution NewExecution(ScheduledRun schedule, DateTime occurrenceAtUtc) =>
        ScheduledRunExecution.Create(
            schedule.Id, occurrenceAtUtc, schedule.ChannelId, schedule.ConversationId,
            schedule.PrincipalId, schedule.Roles, occurrenceAtUtc).Value;

    /// <summary>Walks a run all the way to <see cref="RunStep.Done"/>, exactly as the four dispatchers would have.</summary>
    private async Task<ScheduledRunExecution> SeedDoneExecutionAsync(ScheduledRun schedule, DateTime occurrenceAtUtc)
    {
        var execution = NewExecution(schedule, occurrenceAtUtc);
        execution.BeginScout(occurrenceAtUtc);
        execution.RecordFindings("three open PRs, one failing CI run", occurrenceAtUtc.AddMinutes(1));
        execution.RecordDigest("Three PRs are waiting on you.", occurrenceAtUtc.AddMinutes(2));
        execution.Complete(occurrenceAtUtc.AddMinutes(3));
        await InsertAsync(execution);
        return execution;
    }

    private async Task InsertAsync(ScheduledRunExecution execution)
    {
        await using var db = fixture.CreateDbContext();
        db.ScheduledRunExecutions.Add(execution);
        await db.SaveChangesAsync();
    }

    /// <summary>Queues a real <see cref="ChannelMessageQueued"/> through the generated writer, stamped with <paramref name="executionId"/>.</summary>
    private static async Task QueueMessageAsync(ServiceProvider provider, Guid executionId)
    {
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter<ChannelMessageQueued>>();
        await writer.WriteAsync(
            new ChannelMessageQueued("telegram", "482910337", "Three PRs are waiting on you.", executionId),
            ct: CancellationToken.None);
    }

    private async Task DeadLetterAsync(ServiceProvider provider, Guid executionId, string error, int retryCount) =>
        await MutateRowAsync(provider, executionId, row =>
        {
            row.Status = OutboxMessageStatus.DeadLetter;
            row.DeadLetterError = error;
            row.RetryCount = retryCount;
            row.CreatedAt = new DateTimeOffset(_now.AddMinutes(-5));
        });

    private async Task SetStatusAsync(ServiceProvider provider, Guid executionId, OutboxMessageStatus status) =>
        await MutateRowAsync(provider, executionId, row =>
        {
            row.Status = status;
            row.CreatedAt = new DateTimeOffset(_now.AddMinutes(-5));
        });

    /// <summary>
    ///     Finds the queued row belonging to <paramref name="executionId"/> the only way anything can — by
    ///     deserializing the payload with the same <see cref="IOutboxSerializer"/> the host registers, since
    ///     <c>OutboxMessages</c> has no correlation column — and applies <paramref name="mutate"/> to it.
    /// </summary>
    private async Task MutateRowAsync(ServiceProvider provider, Guid executionId, Action<OutboxMessageEntity> mutate)
    {
        await using var scope = provider.CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IOutboxSerializer>();
        var typeName = typeof(ChannelMessageQueued).FullName;

        await using var db = fixture.CreateDbContext();
        var rows = await db.OutboxMessages.Where(m => m.TypeName == typeName).ToListAsync();
        var row = rows.Single(r => serializer.Deserialize<ChannelMessageQueued>(r.Payload).ExecutionId == executionId);
        mutate(row);
        await db.SaveChangesAsync();
    }

    /// <summary>A dead-lettered row of the right type whose payload is not JSON at all.</summary>
    private async Task InsertUnreadableDeadLetterAsync(DateTime createdAtUtc)
    {
        await using var db = fixture.CreateDbContext();
        db.OutboxMessages.Add(new OutboxMessageEntity
        {
            TypeName = typeof(ChannelMessageQueued).FullName!,
            Payload = "this was never JSON"u8.ToArray(),
            Status = OutboxMessageStatus.DeadLetter,
            DeadLetterError = "Telegram returned 500 after 8 attempts",
            RetryCount = 7,
            CreatedAt = new DateTimeOffset(createdAtUtc),
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> OutboxRowCountAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.OutboxMessages.CountAsync();
    }

    /// <summary>Counts the reads a single overview issues, so an N+1 shows up as growth with the schedule count.</summary>
    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    /// <summary>
    ///     Fails every read of <c>OutboxMessages</c> and nothing else, standing in for an outbox the page cannot
    ///     reach. Dropping the table instead would poison the shared container for the rest of the class.
    /// </summary>
    private sealed class ThrowOnOutboxReadInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("OutboxMessages", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated outbox read failure.");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
