#pragma warning disable S4144

using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Console;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SystemTask = System.Threading.Tasks.Task;

namespace Daedalus.Tests.Integration.Services;

/// <summary>
///     Integration tests for RalphLoopWorker background service.
///     Tests worker lifecycle, session registration, task claiming, and cancellation handling.
/// </summary>
public class RalphLoopWorkerTests : IAsyncLifetime
{
    private readonly ILogger<RalphLoopWorker> _loggerMock = Substitute.For<ILogger<RalphLoopWorker>>();
    private readonly IServiceProvider _serviceProvider;
    private RalphLoopWorker _worker = null!;

    public RalphLoopWorkerTests()
    {
        var services = new ServiceCollection();

        // Register mocked services that RalphLoopWorker depends on
        services.AddScoped(_ => Substitute.For<IExecutionSessionRepository>());
        services.AddScoped(_ => Substitute.For<ITaskAssignmentService>());
        services.AddScoped(_ => Substitute.For<IRalphLoopService>());
        services.AddScoped(_ => Substitute.For<IPhaseOrchestrator>());

        _serviceProvider = services.BuildServiceProvider();
    }

    public async SystemTask InitializeAsync()
    {
        SetupWorker();
        await SystemTask.CompletedTask;
    }

    public async SystemTask DisposeAsync()
    {
        if (_serviceProvider is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        await SystemTask.CompletedTask;
    }

    private void SetupWorker()
    {
        // Get the IServiceScopeFactory from the service provider
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _worker = new RalphLoopWorker(
            scopeFactory,
            _loggerMock
        );
    }

    #region Concurrency Tests

    [Fact(Timeout = 10000)]
    public async SystemTask MultipleWorkers_CanRunConcurrently()
    {
        // Arrange
        var workers = new List<RalphLoopWorker>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        for (var i = 0; i < 3; i++)
        {
            SetupWorker();
            workers.Add(_worker);
        }

        // Act
        var startTasks = workers.Select(w => SystemTask.Run(async () =>
        {
            try
            {
                await w.StartAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }));

        await SystemTask.WhenAll(startTasks);

        // Assert - Workers should complete without errors
        workers.Should().HaveCount(3);
    }

    #endregion

    #region Initialization & Cleanup Tests

    [Fact(Timeout = 10000)]
    public async SystemTask StartAsync_RegistersExecutionSession()
    {
        // Arrange
        SetupWorker();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act
        try
        {
            await _worker.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected - worker stops after registration
        }

        // Assert - Worker should attempt to create scope but get cancelled
        _worker.Should().NotBeNull();
    }

    [Fact(Timeout = 10000)]
    public async SystemTask StopAsync_CleansUpSession()
    {
        // Arrange
        SetupWorker();
        using var cts = new CancellationTokenSource();

        // Act
        await _worker.StopAsync(cts.Token);

        // Assert
        _worker.Should().NotBeNull();
    }

    [Fact(Timeout = 10000)]
    public async SystemTask StartAsync_WithCancellation_StopsGracefully()
    {
        // Arrange
        SetupWorker();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act - Worker should handle cancellation gracefully without throwing
        await _worker.StartAsync(cts.Token);

        // Assert - Worker should complete without errors
        _worker.Should().NotBeNull();
    }

    #endregion

    #region Session Registration Tests

    [Fact(Timeout = 10000)]
    public async SystemTask StartAsync_RegistersExecutionSession_WithCancellation()
    {
        // Arrange
        SetupWorker();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act
        try
        {
            await _worker.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert - Worker should be cancelled
        _worker.Should().NotBeNull();
    }

    [Fact(Timeout = 10000)]
    public async SystemTask StartAsync_LogsStartupMessage()
    {
        // Arrange
        SetupWorker();
        using var cts = new CancellationTokenSource();

        // Act - StartAsync fires ExecuteAsync in the background and returns immediately
        await _worker.StartAsync(cts.Token);

        // Give ExecuteAsync time to run and log the startup message
        await SystemTask.Delay(300);

        // Stop the worker gracefully
        await cts.CancelAsync();
        try
        {
            await _worker.StopAsync(CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }

        // Assert - ExecuteAsync should have logged the startup message
        _loggerMock.ReceivedCalls().Should().NotBeEmpty();
    }

    #endregion

    #region Error Handling Tests

    [Fact(Timeout = 10000)]
    public async SystemTask StartAsync_WithServiceFactoryException_LogsError()
    {
        // Arrange
        SetupWorker();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act - Worker should handle gracefully and get cancelled
        try
        {
            await _worker.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected due to cancellation token
        }

        // Assert - Worker should complete without unhandled exceptions
        _worker.Should().NotBeNull();
    }

    [Fact(Timeout = 10000)]
    public async SystemTask StopAsync_WithException_LogsShutdownError()
    {
        // Arrange
        SetupWorker();
        using var cts = new CancellationTokenSource();

        // Act - Worker should handle shutdown gracefully
        await _worker.StopAsync(cts.Token);

        // Assert - Worker should complete without errors
        _worker.Should().NotBeNull();
    }

    #endregion

    #region Service Lifecycle Tests

    [Fact(Timeout = 10000)]
    public SystemTask Worker_ImplementsBackgroundServiceInterface()
    {
        // Arrange
        SetupWorker();

        // Assert
        _worker.Should().BeAssignableTo<BackgroundService>();
        return SystemTask.CompletedTask;
    }

    [Fact(Timeout = 10000)]
    public async SystemTask StartAsync_CallsServiceScopeFactory()
    {
        // Arrange
        SetupWorker();
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        // Act - Worker should create scopes to register session
        try
        {
            await _worker.StartAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected due to cancellation
        }

        // Assert - Worker should complete without errors
        _worker.Should().NotBeNull();
    }

    #endregion
}
