using System.Collections.Concurrent;
using System.Diagnostics;
using Daedalus.Agents.Channels;
using Daedalus.Infrastructure.Persistence;
using Daedalus.Tests.Integration.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ZeroAlloc.Outbox;
using ZeroAlloc.Outbox.EfCore;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Channels;

/// <summary>
///     Integration tests for the ZeroAlloc.Outbox pipeline registered by
///     <see cref="ChannelOutboxServiceCollectionExtensions.AddChannelOutbox"/>, against a real PostgreSQL database.
///     Covers the two properties that justify the whole component: a queued message actually gets delivered and
///     leaves the pending state (round trip), and a dispatch that keeps failing retries — advancing the row's
///     retry count — and eventually dead-letters rather than being retried forever or silently dropped.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ChannelOutboxTests(PostgresFixture fixture) : IAsyncLifetime
{
    // Deliberately far from the library's own defaults (5s / 50 / 5 / 2s) and from AddChannelOutbox's production
    // values (2s / 20 / 8 / 1s) alike, so a test that silently fell back to either would still be visibly wrong.
    private static readonly TimeSpan _pollingInterval = TimeSpan.FromMilliseconds(100);
    private const int _batchSize = 7;
    private const int _maxAttempts = 3;
    private static readonly TimeSpan _retryBaseDelay = TimeSpan.FromMilliseconds(75);
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(15);

    public async Task InitializeAsync() => await fixture.DatabaseResetter.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Round_trip_a_queued_message_is_delivered_and_leaves_the_pending_state()
    {
        var recording = new RecordingDispatcher();
        await using var provider = BuildProvider(recording);

        var message = new ChannelMessageQueued("telegram", "482910337", "Deploy finished: build 4821 is live.");
        await WriteAsync(provider, message);

        var worker = ResolveWorker(provider);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Poll the DATABASE row, not the in-memory recording queue: OutboxWorkerService persists
            // Status == Succeeded only AFTER DispatchAsync returns (ProcessEntryAsync: dispatch, then
            // MarkSucceededAsync), so waiting on the row's terminal state can never race ahead of the
            // dispatcher's own side effect the way waiting on "was DispatchAsync called at all" can. By
            // the time this returns, the recording queue is guaranteed to already hold the message.
            await WaitUntilAsync(async () => (await ReadRowAsync()).Status == OutboxMessageStatus.Succeeded);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        recording.Messages.Should().ContainSingle().Which.Should().Be(message);

        var row = await ReadRowAsync();
        row.Status.Should().Be(OutboxMessageStatus.Succeeded, "a delivered message must leave the pending state");
        row.ProcessedAt.Should().NotBeNull();
        row.DeadLetterError.Should().BeNull();
    }

    [Fact]
    public async Task A_dispatch_that_keeps_failing_retries_then_dead_letters_instead_of_vanishing()
    {
        var failing = new AlwaysFailingDispatcher();
        await using var provider = BuildProvider(failing);

        var message = new ChannelMessageQueued("telegram", "482910337", "Deploy finished: build 4821 is live.");
        await WriteAsync(provider, message);

        var worker = ResolveWorker(provider);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            // First prove a retry actually happens: the row stays Pending but its RetryCount advances off zero
            // before the message is ever dead-lettered. A dispatcher that dead-lettered on the first failure
            // (MaxAttempts effectively 1) would fail this assertion, not just the one below.
            await WaitUntilAsync(async () =>
            {
                var row = await ReadRowAsync();
                return row.Status == OutboxMessageStatus.Pending && row.RetryCount >= 1;
            });

            await WaitUntilAsync(async () => (await ReadRowAsync()).Status == OutboxMessageStatus.DeadLetter);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var final = await ReadRowAsync();
        final.Status.Should().Be(OutboxMessageStatus.DeadLetter, "exhausting every attempt must dead-letter, not silently drop, the message");
        // MarkFailedAsync bumps RetryCount on every failed-but-retried attempt; DeadLetterAsync itself does not
        // touch RetryCount, so the final value is one less than MaxAttempts (the attempt that exhausted the
        // budget dead-letters instead of incrementing it).
        final.RetryCount.Should().Be(_maxAttempts - 1);
        final.DeadLetterError.Should().NotBeNullOrWhiteSpace();
        final.ProcessedAt.Should().NotBeNull();

        failing.AttemptCount.Should().Be(_maxAttempts, "every attempt up to and including the exhausting one must have actually been dispatched");
    }

    private async Task<OutboxMessageEntity> ReadRowAsync()
    {
        await using var db = fixture.CreateDbContext();
        return await db.OutboxMessages.AsNoTracking().SingleAsync();
    }

    private ServiceProvider BuildProvider(IOutboxDispatcher<ChannelMessageQueued> dispatcher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContextPool<ApplicationDbContext>(o => o.UseNpgsql(fixture.ConnectionString));
        services.AddOutbox(o =>
            {
                o.PollingInterval = _pollingInterval;
                o.BatchSize = _batchSize;
                o.MaxAttempts = _maxAttempts;
                o.RetryBaseDelay = _retryBaseDelay;
            })
            .WithEfCore<ApplicationDbContext>()
            .AddChannelMessageQueuedOutbox();

        // Registered after the chain above: DefaultOutboxDispatcher<ChannelMessageQueued> was only TryAdd-ed, so
        // this unconditional Add wins resolution (DI resolves the last registration for a single-instance ask).
        services.AddSingleton(dispatcher);

        return services.BuildServiceProvider();
    }

    private static async Task WriteAsync(ServiceProvider provider, ChannelMessageQueued message)
    {
        await using var scope = provider.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IOutboxWriter<ChannelMessageQueued>>();
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

    /// <summary>Records every message it is asked to deliver; simulates a channel adapter that always succeeds.</summary>
    private sealed class RecordingDispatcher : IOutboxDispatcher<ChannelMessageQueued>
    {
        public ConcurrentQueue<ChannelMessageQueued> Messages { get; } = new();

        public ValueTask DispatchAsync(ChannelMessageQueued message, CancellationToken ct)
        {
            Messages.Enqueue(message);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Always throws; simulates a channel adapter (e.g. Telegram) that is unreachable for every attempt.</summary>
    private sealed class AlwaysFailingDispatcher : IOutboxDispatcher<ChannelMessageQueued>
    {
        private int _attempts;

        public int AttemptCount => _attempts;

        public ValueTask DispatchAsync(ChannelMessageQueued message, CancellationToken ct)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("Simulated channel-delivery failure for the outbox retry/dead-letter test.");
        }
    }
}
