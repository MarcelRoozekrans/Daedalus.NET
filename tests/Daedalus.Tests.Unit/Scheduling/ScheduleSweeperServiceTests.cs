using Daedalus.Agents.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Daedalus.Tests.Unit.Scheduling;

/// <summary>
///     Covers the one property of <see cref="ScheduleSweeperService"/> that is genuinely testable without a real
///     Postgres database: a tick that fails must not escape and stop every future sweep. Everything else —
///     delegating to <see cref="ScheduledRunStore"/>, logging the fired count, logging nothing when nothing is
///     due — is covered by <c>ScheduleSweeperServiceTests</c> in <c>Daedalus.Tests.Integration</c> instead, because
///     <see cref="ScheduledRunStore.ClaimAndEnqueueDueAsync"/> opens a real transaction over Postgres's <c>xmin</c>
///     system column, which the EF Core InMemory provider does not support — there is no way to exercise even the
///     "nothing is due" path without a relational database.
/// </summary>
public sealed class ScheduleSweeperServiceTests
{
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan _waitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task A_tick_that_fails_to_even_open_a_scope_is_caught_logged_and_does_not_crash_the_service()
    {
        // No ScheduledRunStore, no ApplicationDbContext — CreateScope itself throws, which is as close to
        // "the DI container is broken" as a tick can get. If ScheduleSweeperService's catch only wrapped the
        // store's own call, this would still propagate and take down the BackgroundService loop.
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(_ => throw new InvalidOperationException("Simulated scope-creation failure."));
        var logger = new RecordingLogger();

        var service = new ScheduleSweeperService(scopeFactory, logger, _sweepInterval);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => logger.Entries.Any(e => e.Level == LogLevel.Error));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Error);
        logger.Entries.Should().NotContain(e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task Repeated_scope_failures_keep_producing_error_logs_instead_of_stopping_after_the_first()
    {
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(_ => throw new InvalidOperationException("Simulated scope-creation failure."));
        var logger = new RecordingLogger();

        var service = new ScheduleSweeperService(scopeFactory, logger, _sweepInterval);

        await service.StartAsync(CancellationToken.None);
        try
        {
            // A second error only appears if the loop survived the first failure and kept ticking.
            await WaitUntilAsync(() => logger.Entries.Where(e => e.Level == LogLevel.Error).Skip(1).Any());
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + _waitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException($"Condition was not met within {_waitTimeout}.");
    }

    /// <summary>
    ///     A minimal <see cref="ILogger{TCategoryName}"/> that records every entry's level and formatted message,
    ///     without depending on the <c>[LoggerMessage]</c> source generator's private state-holder type.
    /// </summary>
    private sealed class RecordingLogger : ILogger<ScheduleSweeperService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries => _entries;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
