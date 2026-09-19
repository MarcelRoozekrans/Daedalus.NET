using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;
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

    // Comfortably above anything these tests seed, so only the saturation tests ever hit the cap.
    private const int _defaultScanLimit = 100;

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
    public async Task A_done_execution_reports_the_findings_and_digest_it_produced()
    {
        // The projection change that added these two fields must not turn into a fourth query: Findings and
        // Digest are already loaded on the same ScheduledRunExecution row GetOverviewAsync always fetches in
        // full, never a partial Select. The_overview_issues_one_query_per_table_not_one_per_schedule pins that
        // this stays true; this test pins that the values themselves come through.
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));
        await SeedDoneExecutionAsync(schedule, _now.AddHours(-2));

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Findings.Should().Be("three open PRs, one failing CI run");
        diagnosis.Digest.Should().Be("Three PRs are waiting on you.");
    }

    [Fact]
    public async Task A_schedule_with_no_execution_reports_no_findings_or_digest()
    {
        // A schedule that never fired has no scout or writer output to show — null, not "" or a placeholder
        // string that would render on the page and read as data.
        await SeedScheduleAsync("morning-digest", _now.AddHours(1));

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Findings.Should().BeNull();
        diagnosis.Digest.Should().BeNull();
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

    [Fact]
    public async Task A_disabled_schedule_is_Disabled()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(-3), enabled: false);

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Disabled,
            "the sweeper skips a disabled schedule, so its NextRunAt slides into the past and would otherwise " +
            "read as Overdue — sending an operator after a sweeper that is working perfectly");
        diagnosis.Enabled.Should().BeFalse();
        diagnosis.NextRunAtUtc.Should().Be(schedule.NextRunAt);
    }

    [Fact]
    public async Task A_disabled_schedule_with_a_completed_run_is_Disabled_not_Delivered()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(-3), enabled: false);
        var execution = await SeedDoneExecutionAsync(schedule, _now.AddHours(-26));

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Disabled,
            "Disabled outranks the last run's outcome: yesterday's digest arriving says nothing about a " +
            "schedule that will never fire again");

        // The verdict answers "what is wrong now"; these still answer "what happened last time", which is the
        // question a switched-off schedule immediately raises. The grid shows both columns.
        diagnosis.ExecutionId.Should().Be(execution.Id);
        diagnosis.StepReached.Should().Be((int)RunStep.Done);
    }

    [Fact]
    public async Task An_overdue_next_occurrence_outranks_a_completed_previous_run()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(-2));
        var execution = await SeedDoneExecutionAsync(schedule, _now.AddHours(-26));

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;

        // The sweeper dies overnight. Yesterday's run genuinely delivered, so a classifier that only looks at
        // the latest execution reports a healthy row while today's digest never fires at all — the common case
        // of death-cause 2, and invisible on exactly the page built to catch it. ScheduledRunStore advances
        // NextRunAt at CLAIM time, so NextRunAt in the past means nothing has claimed the next occurrence and
        // cannot mean a run is merely in flight.
        diagnosis.Verdict.Should().Be(RunVerdict.Overdue);
        diagnosis.NextRunAtUtc.Should().Be(_now.AddHours(-2));
        diagnosis.Enabled.Should().BeTrue();

        // Still carries the previous run, which is what the grid's "Last run" column shows.
        diagnosis.ExecutionId.Should().Be(execution.Id);
        diagnosis.OccurrenceAtUtc.Should().Be(_now.AddHours(-26), "OccurrenceAtUtc is the run that happened, not the one that did not");
    }

    [Fact]
    public async Task A_stranded_run_with_an_overdue_next_occurrence_is_Stranded_not_Overdue()
    {
        // Both things are wrong at once: a run stuck mid-flight since yesterday, AND nothing has claimed
        // today's occurrence. Overdue displacing this would hide the more specific alarm behind the vaguer one.
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(-2));
        var execution = NewExecution(schedule, _now.AddHours(-26));
        execution.BeginScout(_now - _strandedAfter - TimeSpan.FromSeconds(1));
        await InsertAsync(execution);

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Stranded,
            "Overdue outranks a verdict only when that verdict would otherwise read HEALTHY; once the row is " +
            "already an alarm the operator will look, and the alarm naming the stuck step beats the one that " +
            "says only that nothing ran");
        diagnosis.StepReached.Should().Be((int)RunStep.Scout, "which step it is stuck at is the half Overdue cannot tell anyone");
    }

    [Fact]
    public async Task A_failed_run_with_an_overdue_next_occurrence_is_Failed_not_Overdue()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(-2));
        var execution = NewExecution(schedule, _now.AddHours(-26));
        execution.BeginScout(_now.AddHours(-26));
        execution.RecordFindings("three open PRs", _now.AddHours(-26));
        execution.Fail("the writer subagent exceeded its token budget", _now.AddHours(-26));
        await InsertAsync(execution);

        await using var provider = BuildProvider();
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Failed);
        diagnosis.LastError.Should().Be("the writer subagent exceeded its token budget",
            "Overdue carries no error text, so displacing Failed would throw away the only account of what broke");
    }

    [Fact]
    public async Task An_undelivered_run_with_an_overdue_next_occurrence_is_Undelivered_not_Overdue()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(-2));
        var execution = await SeedDoneExecutionAsync(schedule, _now.AddHours(-26));

        await using var provider = BuildProvider();
        await QueueMessageAsync(provider, execution.Id);
        await DeadLetterAsync(provider, execution.Id, "Telegram returned 429 after 8 attempts", retryCount: 7);

        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;
        diagnosis.Verdict.Should().Be(RunVerdict.Undelivered);
        diagnosis.DeadLetterError.Should().Be("Telegram returned 429 after 8 attempts");
    }

    [Fact]
    public async Task A_failed_execution_is_still_Failed_when_the_outbox_read_throws()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(1));
        var execution = NewExecution(schedule, _now.AddHours(-2));
        execution.BeginScout(_now.AddMinutes(-40));
        execution.RecordFindings("three open PRs", _now.AddMinutes(-35));
        execution.Fail("the writer subagent exceeded its token budget", _now.AddMinutes(-30));
        await InsertAsync(execution);

        await using var provider = BuildProvider(new ThrowOnOutboxReadInterceptor());
        var overview = await OverviewAsync(provider);

        var diagnosis = overview.Should().ContainSingle().Subject;

        // Only the DELIVERY half is unknown. A downgrade widened to every completed-or-not run would erase
        // Failed, Stranded and Overdue — the verdicts that tell an operator what to actually do — and replace
        // them all with a shrug, precisely when the system is already degraded.
        diagnosis.Verdict.Should().Be(RunVerdict.Failed);
        diagnosis.Verdict.Should().NotBe(RunVerdict.DeliveryUnknown);
        diagnosis.FailedAtStep.Should().Be((int)RunStep.Writer);
        diagnosis.LastError.Should().Be("the writer subagent exceeded its token budget");
    }

    [Fact]
    public async Task A_run_older_than_a_saturated_dead_letter_scan_is_DeliveryUnknown_not_Delivered()
    {
        var warnings = new CapturingLoggerProvider();
        await using var provider = await SeedSaturatedScanAsync(warnings);

        var overview = await OverviewAsync(provider);
        var byName = overview.ToDictionary(d => d.ScheduleName, StringComparer.Ordinal);

        // The two newest dead letters fit inside the cap and answer for themselves.
        byName["dead-recent"].Verdict.Should().Be(RunVerdict.Undelivered);
        byName["dead-older"].Verdict.Should().Be(RunVerdict.Undelivered);

        // This one's dead letter was dropped by the cap. Absence of a row the scan never reached is not
        // evidence of delivery, and reporting Delivered here is the misleading direction.
        byName["dead-ancient"].Verdict.Should().Be(RunVerdict.DeliveryUnknown);
        byName["dead-ancient"].Verdict.Should().NotBe(RunVerdict.Delivered);

        // And the horizon must not blanket-degrade: a run newer than it, with genuinely no dead letter, is
        // still confidently Delivered. A degradation that swallowed everything would be as useless as one
        // that swallowed nothing.
        byName["clean-recent"].Verdict.Should().Be(RunVerdict.Delivered);
    }

    [Fact]
    public async Task A_saturated_dead_letter_scan_says_so_in_the_log()
    {
        var warnings = new CapturingLoggerProvider();
        await using var provider = await SeedSaturatedScanAsync(warnings);

        await OverviewAsync(provider);

        warnings.Warnings.Should().ContainSingle(w => w.Contains("Dead-letter scan", StringComparison.Ordinal))
            .Which.Should().Contain("limit", "a scan that silently answered for only part of the window is the " +
                "failure this page exists to prevent, so saturation must not be invisible");
    }

    [Fact]
    public async Task The_run_history_still_reports_what_each_run_did_when_the_schedule_is_disabled()
    {
        var schedule = await SeedScheduleAsync("morning-digest", _now.AddHours(-3), enabled: false);

        var failed = NewExecution(schedule, _now.AddHours(-6));
        failed.BeginScout(_now.AddHours(-6));
        failed.Fail("the scout subagent timed out", _now.AddHours(-6));
        await InsertAsync(failed);

        var delivered = await SeedDoneExecutionAsync(schedule, _now.AddHours(-2));

        await using var provider = BuildProvider();

        // The overview says the schedule is off...
        var overview = await OverviewAsync(provider);
        overview.Should().ContainSingle().Subject.Verdict.Should().Be(RunVerdict.Disabled);

        // ...but the drill-down must still say what actually happened. Disabling a schedule cannot rewrite the
        // history of the runs that already ran into a wall of Disabled, which is the only reason to open it.
        await using var scope = provider.CreateAsyncScope();
        var history = await scope.ServiceProvider.GetRequiredService<IScheduleDiagnostics>()
            .GetRunHistoryAsync(schedule.Id, take: 10, CancellationToken.None);

        history.Select(h => h.ExecutionId).Should().Equal(delivered.Id, failed.Id);
        history.Select(h => h.Verdict).Should().Equal(RunVerdict.Delivered, RunVerdict.Failed);
        history.Should().OnlyContain(h => !h.Enabled, "the rows still report the schedule's state, they are just not ruled by it");
    }

    // ---- harness ------------------------------------------------------------------------------------------

    /// <summary>
    ///     Four completed runs across four schedules, three of them dead-lettered, against a scan capped at two
    ///     rows — so the oldest dead letter is provably outside what the scan reached.
    /// </summary>
    /// <param name="warnings">Captures what the capped scan logs.</param>
    /// <returns>A provider whose dead-letter scan saturates.</returns>
    private async Task<ServiceProvider> SeedSaturatedScanAsync(CapturingLoggerProvider warnings)
    {
        // NextRunAt in the future on all four, so the Overdue precedence stays out of this test's way.
        var recent = await SeedScheduleAsync("dead-recent", _now.AddHours(1));
        var older = await SeedScheduleAsync("dead-older", _now.AddHours(1));
        var ancient = await SeedScheduleAsync("dead-ancient", _now.AddHours(1));
        var clean = await SeedScheduleAsync("clean-recent", _now.AddHours(1));

        var recentRun = await SeedDoneExecutionAsync(recent, _now.AddMinutes(-60));
        var olderRun = await SeedDoneExecutionAsync(older, _now.AddMinutes(-120));
        var ancientRun = await SeedDoneExecutionAsync(ancient, _now.AddMinutes(-600));
        await SeedDoneExecutionAsync(clean, _now.AddMinutes(-30));

        var provider = BuildProviderWithScanLimit(scanLimit: 2, warnings);

        await QueueMessageAsync(provider, recentRun.Id);
        await QueueMessageAsync(provider, olderRun.Id);
        await QueueMessageAsync(provider, ancientRun.Id);

        // Ordered so the cap of two keeps the first two and drops the third. The horizon lands at -115m, which
        // is newer than the ancient run's own CreatedAt of -600m and older than the clean run's of -30m.
        await DeadLetterAsync(provider, recentRun.Id, "Telegram returned 429 after 8 attempts", retryCount: 7, createdAtUtc: _now.AddMinutes(-55));
        await DeadLetterAsync(provider, olderRun.Id, "Telegram returned 429 after 8 attempts", retryCount: 7, createdAtUtc: _now.AddMinutes(-115));
        await DeadLetterAsync(provider, ancientRun.Id, "Telegram returned 500 after 8 attempts", retryCount: 7, createdAtUtc: _now.AddMinutes(-595));

        return provider;
    }

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
    private ServiceProvider BuildProvider(params IInterceptor[] interceptors) =>
        BuildProviderCore(_defaultScanLimit, interceptors, warnings: null);

    /// <summary>A provider whose dead-letter scan is capped at <paramref name="scanLimit"/> rows, capturing what it logs.</summary>
    private ServiceProvider BuildProviderWithScanLimit(int scanLimit, CapturingLoggerProvider warnings) =>
        BuildProviderCore(scanLimit, [], warnings);

    private ServiceProvider BuildProviderCore(int scanLimit, IInterceptor[] interceptors, CapturingLoggerProvider? warnings)
    {
        var services = new ServiceCollection();
        services.AddLogging(b =>
        {
            if (warnings is not null)
            {
                b.AddProvider(warnings);
            }
        });
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
            o.DeadLetterScanLimit = scanLimit;
        });
        services.AddScoped<IScheduleDiagnostics, ScheduleDiagnostics>();

        return services.BuildServiceProvider();
    }

    private async Task<ScheduledRun> SeedScheduleAsync(string name, DateTime nextRunAtUtc, bool enabled = true)
    {
        var schedule = ScheduledRun.Create(
            name, "0 7 * * *", "RepoDigest", "telegram", "482910337",
            "schedule:daedalus", ["reader", "writer"], ScheduleOrigin.Config, nextRunAtUtc).Value;

        if (!enabled)
        {
            schedule.Disable();
        }

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

    private async Task DeadLetterAsync(ServiceProvider provider, Guid executionId, string error, int retryCount, DateTime? createdAtUtc = null) =>
        await MutateRowAsync(provider, executionId, row =>
        {
            row.Status = OutboxMessageStatus.DeadLetter;
            row.DeadLetterError = error;
            row.RetryCount = retryCount;
            row.CreatedAt = new DateTimeOffset(createdAtUtc ?? _now.AddMinutes(-5));
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

    /// <summary>Captures what the service logs, so "saturation must not be invisible" is an assertion rather than a hope.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _warnings = new();

        public IReadOnlyCollection<string> Warnings => _warnings;

        public ILogger CreateLogger(string categoryName) => new Capturing(_warnings);

        public void Dispose()
        {
            // Nothing to release; the queue lives as long as the test.
        }

        private sealed class Capturing(ConcurrentQueue<string> warnings) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Warning)
                {
                    warnings.Enqueue(formatter(state, exception));
                }
            }
        }
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
