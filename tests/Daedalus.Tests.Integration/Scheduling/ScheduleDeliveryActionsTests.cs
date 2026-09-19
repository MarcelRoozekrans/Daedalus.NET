using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     <see cref="ScheduleDeliveryActions"/> against a real PostgreSQL database and the real, DI-resolved
///     <see cref="IOutboxSerializer"/> and <see cref="IOutboxDashboardStore"/> — the same rule
///     <see cref="ScheduleDiagnosticsTests"/> follows for its own outbox reads.
/// </summary>
/// <remarks>
///     Three things matter here that a mock could not prove: the execution-id-to-outbox-row lookup finds the
///     right row when more than one schedule's messages share the table and the type name, a genuine "not
///     found" surfaces as a <c>Result</c> failure rather than a silent no-op or an exception, and a successful
///     requeue actually changes the row's status rather than merely returning success.
/// </remarks>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduleDeliveryActionsTests(PostgresFixture fixture) : IAsyncLifetime
{
    private static readonly DateTime _now = new(2026, 9, 18, 7, 30, 0, DateTimeKind.Utc);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Requeue_finds_the_dead_lettered_message_and_resets_it_for_redelivery()
    {
        // Anchored to _now rather than inherited from the real wall clock: the dead letter below is stamped at
        // _now minus five minutes, and DeadLetterLookback is one day measured from "now". Time only moves
        // forward, so a test that let this fall back to TimeProvider.System would pass on the day it was
        // written and fail permanently once the real clock drifted a day past _now.
        var time = new FakeTimeProvider(new DateTimeOffset(_now));

        var schedule = await SeedScheduleAsync("morning-digest");
        var execution = await SeedDoneExecutionAsync(schedule);

        await using var provider = BuildProvider(time);
        await QueueMessageAsync(provider, execution.Id);
        await DeadLetterAsync(provider, execution.Id, "Telegram returned 429 after 8 attempts", retryCount: 7);

        await using var scope = provider.CreateAsyncScope();
        var actions = scope.ServiceProvider.GetRequiredService<IScheduleDeliveryActions>();
        var result = await actions.RequeueAsync(execution.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : null);

        var row = await SingleRowForAsync(execution.Id);
        row.Status.Should().Be(OutboxMessageStatus.Pending,
            "a successful requeue puts the message back in the ordinary poller's path, not merely reports success");
    }

    [Fact]
    public async Task Requeue_fails_when_no_dead_lettered_message_exists_for_the_execution()
    {
        var schedule = await SeedScheduleAsync("morning-digest");
        var execution = await SeedDoneExecutionAsync(schedule);

        // No QueueMessageAsync/DeadLetterAsync at all: this execution has nothing in the outbox, the way a
        // page showing a stale Undelivered row after someone else already resolved it would look.
        await using var provider = BuildProvider();

        await using var scope = provider.CreateAsyncScope();
        var actions = scope.ServiceProvider.GetRequiredService<IScheduleDeliveryActions>();
        var result = await actions.RequeueAsync(execution.Id, CancellationToken.None);

        result.IsFailure.Should().BeTrue("resend must surface the contract's failed state, not silently succeed");
        result.Error.Should().Contain(execution.Id.ToString());
    }

    [Fact]
    public async Task Requeue_reads_the_window_from_the_injected_clock_not_the_wall_clock()
    {
        // fakeNow is deliberately far in the PAST relative to the real wall clock, and the dead letter is
        // stamped just inside a 1-day lookback measured from fakeNow. A query of the shape "CreatedAt >= since"
        // has no upper bound, so a dead letter stamped in the FUTURE relative to the real clock would still be
        // found by an implementation that read the real wall clock -- that shape doesn't discriminate. Anchoring
        // in the past does: measured from the real wall clock, a lookback of 1 day starting around "today" does
        // not reach back to 2020, so the buggy DateTimeOffset.UtcNow implementation fails to find this row,
        // while the fixed implementation -- which computes the window from the injected fake clock -- finds it and succeeds.
        var fakeNow = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(fakeNow);

        var schedule = await SeedScheduleAsync("morning-digest");
        var execution = await SeedDoneExecutionAsync(schedule);

        await using var provider = BuildProvider(time);
        await QueueMessageAsync(provider, execution.Id);
        await DeadLetterAsync(provider, execution.Id, "Telegram returned 429 after 8 attempts", retryCount: 7,
            createdAt: fakeNow.AddMinutes(-5));

        await using var scope = provider.CreateAsyncScope();
        var actions = scope.ServiceProvider.GetRequiredService<IScheduleDeliveryActions>();
        var result = await actions.RequeueAsync(execution.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : null);
    }

    [Fact]
    public async Task Requeue_only_touches_the_matching_executions_dead_letter_not_another_schedules()
    {
        // Same reasoning as Requeue_finds_the_dead_lettered_message_and_resets_it_for_redelivery: both dead
        // letters below are stamped relative to _now, so the lookback window must be measured from _now too,
        // not from whatever the real wall clock happens to be when this test runs.
        var time = new FakeTimeProvider(new DateTimeOffset(_now));

        var targeted = await SeedScheduleAsync("morning-digest");
        var other = await SeedScheduleAsync("weekly-digest");
        var targetedExecution = await SeedDoneExecutionAsync(targeted);
        var otherExecution = await SeedDoneExecutionAsync(other);

        await using var provider = BuildProvider(time);
        await QueueMessageAsync(provider, targetedExecution.Id);
        await QueueMessageAsync(provider, otherExecution.Id);
        await DeadLetterAsync(provider, targetedExecution.Id, "Telegram returned 429 after 8 attempts", retryCount: 7);
        await DeadLetterAsync(provider, otherExecution.Id, "Telegram returned 500 after 8 attempts", retryCount: 7);

        await using var scope = provider.CreateAsyncScope();
        var actions = scope.ServiceProvider.GetRequiredService<IScheduleDeliveryActions>();
        var result = await actions.RequeueAsync(targetedExecution.Id, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error : null);

        (await SingleRowForAsync(targetedExecution.Id)).Status.Should().Be(OutboxMessageStatus.Pending);
        (await SingleRowForAsync(otherExecution.Id)).Status.Should().Be(OutboxMessageStatus.DeadLetter,
            "the other schedule's dead letter shares the type name and must be left alone");
    }

    private ServiceProvider BuildProvider(TimeProvider? timeProvider = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(timeProvider ?? TimeProvider.System);
        services.AddDbContext<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddOutbox(o => o.PollingInterval = TimeSpan.FromHours(1))
            .WithEfCore<ApplicationDbContext>()
            .AddChannelMessageQueuedOutbox();

        // Mirrors DaedalusSchedulingServiceCollectionExtensions.AddDaedalusScheduling: WithEfCore only
        // registers IOutboxStore, so the dashboard seam needs its own TryAdd.
        services.TryAddScoped<IOutboxDashboardStore, EfCoreOutboxStore<ApplicationDbContext>>();
        services.Configure<ScheduleDiagnosticsOptions>(o => o.DeadLetterLookback = TimeSpan.FromDays(1));
        services.AddScoped<IScheduleDeliveryActions, ScheduleDeliveryActions>();

        return services.BuildServiceProvider();
    }

    private async Task<ScheduledRun> SeedScheduleAsync(string name)
    {
        var schedule = ScheduledRun.Create(
            name, "0 7 * * *", "RepoDigest", "telegram", "482910337",
            "schedule:daedalus", ["reader", "writer"], ScheduleOrigin.Config, _now.AddHours(1)).Value;

        await using var db = fixture.CreateDbContext();
        db.ScheduledRuns.Add(schedule);
        await db.SaveChangesAsync();
        return schedule;
    }

    /// <summary>Walks a run all the way to <see cref="RunStep.Done"/>, exactly as the four dispatchers would have.</summary>
    private async Task<ScheduledRunExecution> SeedDoneExecutionAsync(ScheduledRun schedule)
    {
        var occurrenceAtUtc = _now.AddHours(-2);
        var execution = ScheduledRunExecution.Create(
            schedule.Id, occurrenceAtUtc, schedule.ChannelId, schedule.ConversationId,
            schedule.PrincipalId, schedule.Roles, occurrenceAtUtc).Value;
        execution.BeginScout(occurrenceAtUtc);
        execution.RecordFindings("three open PRs, one failing CI run", occurrenceAtUtc.AddMinutes(1));
        execution.RecordDigest("Three PRs are waiting on you.", occurrenceAtUtc.AddMinutes(2));
        execution.Complete(occurrenceAtUtc.AddMinutes(3));

        await using var db = fixture.CreateDbContext();
        db.ScheduledRunExecutions.Add(execution);
        await db.SaveChangesAsync();
        return execution;
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

    private async Task DeadLetterAsync(ServiceProvider provider, Guid executionId, string error, int retryCount, DateTimeOffset? createdAt = null) =>
        await MutateRowAsync(provider, executionId, row =>
        {
            row.Status = OutboxMessageStatus.DeadLetter;
            row.DeadLetterError = error;
            row.RetryCount = retryCount;
            row.CreatedAt = createdAt ?? new DateTimeOffset(_now.AddMinutes(-5));
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

    private async Task<OutboxMessageEntity> SingleRowForAsync(Guid executionId)
    {
        await using var scope = BuildProvider().CreateAsyncScope();
        var serializer = scope.ServiceProvider.GetRequiredService<IOutboxSerializer>();
        var typeName = typeof(ChannelMessageQueued).FullName;

        await using var db = fixture.CreateDbContext();
        var rows = await db.OutboxMessages.Where(m => m.TypeName == typeName).ToListAsync();
        return rows.Single(r => serializer.Deserialize<ChannelMessageQueued>(r.Payload).ExecutionId == executionId);
    }
}
