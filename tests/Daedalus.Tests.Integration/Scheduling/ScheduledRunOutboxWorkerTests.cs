using System.Diagnostics;
using Daedalus.Agents.Channels;
using Daedalus.Agents.Scheduling;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Thalos;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Scheduling;

/// <summary>
///     Drives a <see cref="RunScoutStep"/> row through the real, generated ZeroAlloc.Outbox pipeline — the actual
///     <see cref="OutboxWorkerService"/> polling the actual <c>OutboxMessages</c> table — rather than constructing
///     <see cref="RunScoutStepDispatcher"/> directly and handing it a message, the way
///     <see cref="ScheduledRunFlowTests"/> deliberately does (see that type's remarks for why). Nothing else in this
///     phase proves that a scheduling message's type-name key — a fully-qualified string baked into
///     ZeroAlloc.Outbox's generated code — actually round-trips through the serializer and reaches the right
///     dispatcher: the AppHost end-to-end run that would normally cover this could not be performed (both hosts
///     crash on an unrelated upstream dependency conflict), so this is the closest available substitute. Follows
///     <see cref="Channels.ChannelOutboxTests"/>'s harness shape (poll the database row, not an in-memory recording
///     queue, so the wait can never race ahead of the worker's own persisted side effect).
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScheduledRunOutboxWorkerTests(PostgresFixture fixture) : IAsyncLifetime
{
    // Same "deliberately far from any default" reasoning as ChannelOutboxTests: a test that silently fell back to
    // a library or production default would still look plausible.
    private static readonly TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(100);
    private const int _batchSize = 7;
    private const int _maxAttempts = 3;
    private static readonly TimeSpan _retryBaseDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(15);

    private static readonly DateTime Occurrence = new(2026, 9, 17, 7, 0, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 17, 7, 0, 5, TimeSpan.Zero));

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_real_OutboxWorkerService_drives_a_RunScoutStep_row_to_the_writer_step()
    {
        const string findings = "three open PRs, one failing CI run";
        var executionId = await SeedScoutExecutionAsync();

        await using var provider = BuildProvider(new FixedFindingsSubagentRunExecutor(findings));
        await WriteAsync(provider, new RunScoutStep(executionId));

        var worker = ResolveWorker(provider);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Poll the RunScoutStep row's own status, not the execution row: OutboxWorkerService persists
            // Status == Succeeded only AFTER DispatchAsync returns (same ordering ChannelOutboxTests relies on),
            // and RunScoutStepDispatcher commits the execution's advance to Writer INSIDE that call, before it
            // returns. Waiting on the execution row directly would still be correct on the happy path, but
            // waiting on the outbox row's terminal state is what actually proves the dispatch fully completed,
            // rather than merely raced ahead of it.
            await WaitUntilAsync(async () => (await ReadOutboxRowAsync<RunScoutStep>()).Status == OutboxMessageStatus.Succeeded);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var outboxRow = await ReadOutboxRowAsync<RunScoutStep>();
        outboxRow.Status.Should().Be(OutboxMessageStatus.Succeeded,
            "the RunScoutStep row itself must leave the pending state, proving the type-name key round-tripped " +
            "through the real serializer and reached RunScoutStepDispatcher");

        var row = await ReadExecutionAsync(executionId);
        row.Step.Should().Be(RunStep.Writer);
        row.Findings.Should().Be(findings);

        var writerStepRow = await ReadOutboxRowAsync<RunWriterStep>();
        writerStepRow.Should().NotBeNull("advancing to Writer must enqueue the next step's outbox message too");
    }

    /// <summary>Seeds a schedule and an execution row already at <see cref="RunStep.Scout"/>, exactly what <see cref="ScheduledRunExecutionStore.TryBeginAsync"/> would have produced.</summary>
    private async Task<Guid> SeedScoutExecutionAsync()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var schedule = ScheduledRun.Create(
            "daily-digest", "0 7 * * *", "RepoDigest", "telegram", "482910337",
            "schedule:daedalus", ["reader", "writer"], ScheduleOrigin.Config, Occurrence).Value;

        var execution = ScheduledRunExecution.Create(
            schedule.Id, Occurrence, schedule.ChannelId, schedule.ConversationId,
            schedule.PrincipalId, schedule.Roles, now).Value;
        execution.BeginScout(now);

        await using var db = fixture.CreateDbContext();
        db.ScheduledRuns.Add(schedule);
        db.ScheduledRunExecutions.Add(execution);
        await db.SaveChangesAsync();
        return execution.Id;
    }

    private async Task<ScheduledRunExecution> ReadExecutionAsync(Guid id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.ScheduledRunExecutions.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    private async Task<OutboxMessageEntity> ReadOutboxRowAsync<T>()
    {
        var typeName = typeof(T).FullName;
        await using var db = fixture.CreateDbContext();
        return await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.TypeName == typeName);
    }

    private ServiceProvider BuildProvider(ISubagentRunExecutor executor)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddDbContextPool<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddOutbox(o =>
            {
                o.PollingInterval = _pollingInterval;
                o.BatchSize = _batchSize;
                o.MaxAttempts = _maxAttempts;
                o.RetryBaseDelay = _retryBaseDelay;
            })
            .WithEfCore<ApplicationDbContext>()
            .AddRunScoutStepOutbox()
            .AddRunWriterStepOutbox()
            .AddDeliverDigestOutbox()
            .AddChannelMessageQueuedOutbox();

        services.AddScoped<ScheduledRunExecutionStore>();
        services.AddSingleton(executor);

        // Registered after AddRunScoutStepOutbox's own TryAdd of the throwing default: an unconditional Add wins
        // DI resolution regardless of order, the same override ChannelOutboxTests uses and production's own
        // Replace calls achieve via AddDaedalusScheduling.
        services.AddTransient<IOutboxDispatcher<RunScoutStep>, RunScoutStepDispatcher>();

        return services.BuildServiceProvider();
    }

    private static async Task WriteAsync(ServiceProvider provider, RunScoutStep message)
    {
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter<RunScoutStep>>();
        await writer.WriteAsync(message, ct: CancellationToken.None);
    }

    private static OutboxWorkerService ResolveWorker(ServiceProvider provider) =>
        provider.GetServices<IHostedService>().OfType<OutboxWorkerService>().Single();

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < _waitTimeout)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Condition was not met within {_waitTimeout}.");
    }

    /// <summary>Always succeeds with fixed findings text, standing in for a real Thalos scout turn.</summary>
    private sealed class FixedFindingsSubagentRunExecutor(string findings) : ISubagentRunExecutor
    {
        public ValueTask<ZeroAlloc.Results.Result<string, AgentError>> RunAsync(
            string agentName, string task, string principalId, IReadOnlyList<string> roles, CancellationToken ct) =>
            ValueTask.FromResult(ZeroAlloc.Results.Result<string, AgentError>.Success(findings));
    }
}
