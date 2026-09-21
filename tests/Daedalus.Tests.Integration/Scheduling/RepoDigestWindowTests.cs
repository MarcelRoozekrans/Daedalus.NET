using Daedalus.Agents.Channels;
using Daedalus.Infrastructure.Services.GitHub;
using Daedalus.Agents.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Covers Task 7 of the scout-repository-tooling plan: the scout is finally told which repository to sweep
///     and what window to sweep it over. Two things are under test — <see cref="RepoDigestPrompts.ScoutTask"/>
///     naming the repository and the window instead of the old, unresolvable "this repository", and
///     <see cref="ScheduledRunExecutionStore.ResolveWindowStartAsync"/> resolving the window from the previous
///     completed occurrence rather than a fixed lookback, so a missed 07:00 run caught at 08:00 does not silently
///     drop the missing hour. <see cref="RepoDigestRepositoryValidator"/> is covered too: a <c>RepoDigest</c>
///     schedule with no configured repository must fail the host at boot, not run the scout blind.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RepoDigestWindowTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Repository = "owner/repo";

    // Deliberately far from GitHubOptions' real 24-hour default: a hardcoded TimeSpan.FromHours(24) fallback in
    // production would satisfy an assertion against the real default without the configured value ever being
    // read. Asserting against a distinctly different configured value makes that failure mode visible.
    private static readonly TimeSpan ConfiguredLookback = TimeSpan.FromHours(6);

    private static readonly string[] Roles = ["reader"];

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 18, 7, 0, 5, TimeSpan.Zero));

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_window_starts_at_the_previous_occurrence_not_a_fixed_lookback()
    {
        var schedule = await SeedScheduleAsync();
        await SeedCompletedExecutionAsync(schedule.Id, occurrenceAt: Now.AddHours(-25));

        var since = await ResolveWindowStartAsync(schedule.Id);

        since.Should().Be(Now.AddHours(-25),
            "a run missed at 07:00 and caught at 08:00 must not silently drop the missing hour");
    }

    [Fact]
    public async Task A_schedule_with_no_previous_occurrence_uses_the_configured_lookback()
    {
        var schedule = await SeedScheduleAsync();

        var since = await ResolveWindowStartAsync(schedule.Id);

        since.Should().Be(Now - ConfiguredLookback,
            "BuildStore configures a non-default lookback specifically so a hardcoded 24-hour fallback in " +
            "production cannot pass this test by coincidence");
    }

    [Fact]
    public async Task A_failed_previous_occurrence_does_not_count_as_completed()
    {
        // The window formula only ever anchors on a run that actually reported something. A failed execution
        // produced no digest, so counting it as "the last digest" would silently narrow the window to the point
        // of the failure instead of widening it back to the last time anything was actually delivered.
        var schedule = await SeedScheduleAsync();
        await SeedCompletedExecutionAsync(schedule.Id, occurrenceAt: Now.AddHours(-49));
        await SeedFailedExecutionAsync(schedule.Id, occurrenceAt: Now.AddHours(-25));

        var since = await ResolveWindowStartAsync(schedule.Id);

        since.Should().Be(Now.AddHours(-49),
            "the failed occurrence at -25h reported nothing; the last real digest was at -49h");
    }

    [Fact]
    public void The_scout_task_names_the_repository_and_the_window()
    {
        var task = RepoDigestPrompts.ScoutTask("owner/repo", new DateTime(2026, 9, 18, 7, 0, 0, DateTimeKind.Utc));

        task.Should().Contain("owner/repo");
        task.Should().Contain("2026-09-18");
        task.Should().NotContainEquivalentOf("this repository",
            "the old wording had nothing resolving it and left the scout guessing");
    }

    [Fact]
    public async Task The_configured_repository_is_read_back_for_the_schedule()
    {
        var schedule = await SeedScheduleAsync();

        var repository = await BuildStore().GetScheduleRepositoryAsync(schedule.Id, default);

        repository.Should().Be(Repository);
    }

    [Fact]
    public void A_RepoDigest_schedule_with_no_repository_fails_startup_validation()
    {
        var entries = new List<ScheduledRunOptions>
        {
            new() { Name = "daily-digest", Cron = "0 7 * * *", Trigger = "RepoDigest", ChannelId = "telegram", ConversationId = "1", Repository = null },
        };

        var act = () => RepoDigestRepositoryValidator.Validate(entries);

        act.Should().Throw<InvalidOperationException>().WithMessage("*daily-digest*");
    }

    [Fact]
    public void A_RepoDigest_schedule_with_a_repository_passes_startup_validation()
    {
        var entries = new List<ScheduledRunOptions>
        {
            new() { Name = "daily-digest", Cron = "0 7 * * *", Trigger = "RepoDigest", ChannelId = "telegram", ConversationId = "1", Repository = Repository },
        };

        var act = () => RepoDigestRepositoryValidator.Validate(entries);

        act.Should().NotThrow();
    }

    /// <summary>Seeds an enabled <c>RepoDigest</c> schedule, configured with <see cref="Repository"/>, due at <see cref="Now"/>.</summary>
    private async Task<ScheduledRun> SeedScheduleAsync()
    {
        var schedule = ScheduledRun.Create(
            $"daily-digest-{Guid.NewGuid():N}", "0 7 * * *", "RepoDigest", "telegram", "482910337",
            "schedule:daedalus", Roles, ScheduleOrigin.Config, Now, Repository).Value;

        await using var db = fixture.CreateDbContext();
        db.ScheduledRuns.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    /// <summary>Seeds a <see cref="ScheduledRunExecution"/> for <paramref name="scheduleId"/> that ran all the way to <see cref="RunStep.Done"/>.</summary>
    private async Task SeedCompletedExecutionAsync(Guid scheduleId, DateTime occurrenceAt)
    {
        var execution = ScheduledRunExecution.Create(
            scheduleId, occurrenceAt, "telegram", "482910337", "schedule:daedalus", Roles, occurrenceAt).Value;
        execution.BeginScout(occurrenceAt);
        execution.RecordFindings("findings", occurrenceAt);
        execution.RecordDigest("digest", occurrenceAt);
        execution.Complete(occurrenceAt);

        await using var db = fixture.CreateDbContext();
        db.ScheduledRunExecutions.Add(execution);
        await db.SaveChangesAsync();
    }

    /// <summary>Seeds a <see cref="ScheduledRunExecution"/> for <paramref name="scheduleId"/> that failed at the scout step.</summary>
    private async Task SeedFailedExecutionAsync(Guid scheduleId, DateTime occurrenceAt)
    {
        var execution = ScheduledRunExecution.Create(
            scheduleId, occurrenceAt, "telegram", "482910337", "schedule:daedalus", Roles, occurrenceAt).Value;
        execution.BeginScout(occurrenceAt);
        execution.Fail("boom", occurrenceAt);

        await using var db = fixture.CreateDbContext();
        db.ScheduledRunExecutions.Add(execution);
        await db.SaveChangesAsync();
    }

    /// <summary>
    ///     Resolves the window start for <paramref name="scheduleId"/> as of <see cref="Now"/> — i.e. as if
    ///     <see cref="Now"/> were the current occurrence's <c>OccurrenceAt</c> — through the real, DI-resolved
    ///     <see cref="ScheduledRunExecutionStore"/>, the same production seam <c>RunScoutStepDispatcher</c> calls.
    /// </summary>
    private Task<DateTime> ResolveWindowStartAsync(Guid scheduleId) =>
        BuildStore().ResolveWindowStartAsync(scheduleId, Now, default);

    /// <summary>
    ///     A store resolved from its own scope, wired like production — the same minimal shape
    ///     <see cref="ScheduledRunExecutionStoreTests"/> and <see cref="ScheduledRunFlowTests"/> use. This test
    ///     class never advances a step, so the outbox writers are wired only because the store's constructor
    ///     requires them. <see cref="GitHubOptions.DefaultLookback"/> is configured to <see cref="ConfiguredLookback"/>
    ///     — six hours, nowhere near the real 24-hour default — so
    ///     <c>A_schedule_with_no_previous_occurrence_uses_the_configured_lookback</c> cannot pass against a
    ///     production fallback that ignores configuration entirely.
    /// </summary>
    private ScheduledRunExecutionStore BuildStore()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<GitHubOptions>(o => o.DefaultLookback = ConfiguredLookback);
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContextPool<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddOutbox(o => { })
            .WithEfCore<ApplicationDbContext>()
            .AddRunScoutStepOutbox()
            .AddRunWriterStepOutbox()
            .AddDeliverDigestOutbox()
            .AddChannelMessageQueuedOutbox();
        services.AddScoped<ScheduledRunExecutionStore>();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ScheduledRunExecutionStore>();
    }
}
