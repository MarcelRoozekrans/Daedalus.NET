using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Domain.Entities;
using SystemTask = System.Threading.Tasks.Task;
using Task = Daedalus.Domain.Entities.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Console;

/// <summary>
///     Long-running worker that processes Ralph loop tasks.
///     Multiple instances can run concurrently on the same task backlog.
/// </summary>
public sealed partial class RalphLoopWorker(
    IServiceScopeFactory serviceScopeFactory,
    ILogger<RalphLoopWorker> logger) : BackgroundService
{
    private static readonly TimeSpan _taskPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan _staleClaimInterval = TimeSpan.FromMinutes(5);
    private Guid _sessionId;
    private string _workerName = null!;

    /// <summary>
    ///     Executes the worker loop.
    /// </summary>
    protected override async SystemTask ExecuteAsync(CancellationToken stoppingToken)
    {
        _sessionId = Guid.NewGuid();
        _workerName = $"worker-{Environment.MachineName}-{_sessionId:N}"[..50];

        LogWorkerStarting(logger, _workerName);

        // Register session
        await RegisterSessionAsync(stoppingToken);

        // Main worker loop
        using var timer = new PeriodicTimer(_taskPollInterval);
        using var heartbeatTimer = new PeriodicTimer(_heartbeatInterval);
        using var staleClaimTimer = new PeriodicTimer(_staleClaimInterval);

        try
        {
            await SystemTask.WhenAll(
                PollAndProcessTasksAsync(timer, stoppingToken),
                UpdateHeartbeatAsync(heartbeatTimer, stoppingToken),
                ReclaimStaleTasksAsync(staleClaimTimer, stoppingToken));
        }
        catch (OperationCanceledException)
        {
            LogWorkerCancellationRequested(logger);
        }
        finally
        {
            await ShutdownSessionAsync(stoppingToken);
        }
    }

    /// <summary>
    ///     Registers this worker session in the database.
    /// </summary>
    private async SystemTask RegisterSessionAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var sessionRepository = scope.ServiceProvider.GetRequiredService<IExecutionSessionRepository>();

            var sessionResult = ExecutionSession.Create(_sessionId, _workerName);
            if (sessionResult.IsFailure)
            {
                LogFailedCreateSession(logger, sessionResult.Error);
                return;
            }

            var registerResult = await sessionRepository.AddAsync(sessionResult.Value, ct);
            if (registerResult.IsFailure)
            {
                LogFailedRegisterSession(logger, registerResult.Error);
                return;
            }

            LogSessionRegistered(logger, _sessionId);
        }
        catch (Exception ex)
        {
            LogErrorRegisteringSession(logger, ex);
        }
    }

    /// <summary>
    ///     Main loop: continuously poll for tasks and execute them.
    /// </summary>
    private async SystemTask PollAndProcessTasksAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await ProcessNextTaskAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            LogTaskPollingCancelled(logger);
        }
    }

    /// <summary>
    ///     Attempts to claim and execute the next available task.
    /// </summary>
    private async SystemTask ProcessNextTaskAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var taskAssignment = scope.ServiceProvider.GetRequiredService<ITaskAssignmentService>();
            var ralphLoop = scope.ServiceProvider.GetRequiredService<IRalphLoopService>();

            // Get next available task (atomic claim with dependency gate)
            var nextTaskResult = await taskAssignment.GetNextAvailableTaskAsync(_sessionId, ct);
            if (nextTaskResult.IsFailure)
            {
                LogErrorGettingNextTask(logger, nextTaskResult.Error);
                return;
            }

            var task = nextTaskResult.Value;
            if (task is null)
            {
                // No tasks available, wait for next poll
                return;
            }

            LogProcessingTask(logger, task.Id, task.Prompt.Length, task.MaxIterations);

            // Execute the Ralph loop
            var executeResult = await ralphLoop.ExecuteAsync(task, _sessionId, ct);

            if (executeResult.IsFailure)
            {
                LogRalphLoopExecutionFailed(logger, task.Id, executeResult.Error);
                return;
            }

            // Phase chaining: evaluate whether dependent tasks are now unblocked
            await EvaluatePhaseChainingAsync(scope.ServiceProvider, task, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            LogUnexpectedErrorProcessingTask(logger, ex);
        }
    }

    /// <summary>
    ///     After a task completes, re-reads it from DB and runs the phase orchestrator
    ///     to determine which dependent tasks are now unblocked.
    /// </summary>
    private async SystemTask EvaluatePhaseChainingAsync(
        IServiceProvider serviceProvider,
        Task executedTask,
        CancellationToken ct)
    {
        try
        {
            var taskRepository = serviceProvider.GetRequiredService<ITaskRepository>();
            var phaseOrchestrator = serviceProvider.GetRequiredService<IPhaseOrchestrator>();

            // Re-read from DB to get the updated status after execution
            var refreshResult = await taskRepository.GetByIdAsync(executedTask.Id, ct).ConfigureAwait(false);

            if (refreshResult.IsFailure)
            {
                LogPhaseCheckSkipped(logger, executedTask.Id, refreshResult.Error);
                return;
            }

            var refreshedTask = refreshResult.Value;

            if (refreshedTask.Status != TaskStatus.Completed)
            {
                // Task didn't complete (failed/still running) — no phase chaining needed
                return;
            }

            var orchestratorResult = await phaseOrchestrator
                .OnTaskCompletedAsync(refreshedTask, ct)
                .ConfigureAwait(false);

            if (orchestratorResult.IsFailure)
            {
                LogPhaseOrchestratorError(logger, executedTask.Id, orchestratorResult.Error);
            }
        }
        catch (Exception ex)
        {
            LogPhaseOrchestratorException(logger, ex, executedTask.Id);
        }
    }

    /// <summary>
    ///     Periodically update heartbeat to signal the session is alive.
    /// </summary>
    private async SystemTask UpdateHeartbeatAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await using var scope = serviceScopeFactory.CreateAsyncScope();
                    var sessionRepository = scope.ServiceProvider.GetRequiredService<IExecutionSessionRepository>();

                    var sessionResult = await sessionRepository.GetByIdAsync(_sessionId, ct).ConfigureAwait(false);
                    if (sessionResult.IsFailure)
                    {
                        LogSessionNotFoundForHeartbeat(logger);
                        continue;
                    }

                    var session = sessionResult.Value;
                    session.Heartbeat();

                    var updateResult = await sessionRepository.UpdateAsync(session, ct).ConfigureAwait(false);
                    if (updateResult.IsFailure)
                    {
                        LogFailedUpdateHeartbeat(logger, updateResult.Error);
                    }
                }
                catch (Exception ex)
                {
                    LogErrorUpdatingHeartbeat(logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogHeartbeatUpdateCancelled(logger);
        }
    }

    /// <summary>
    ///     Periodically check for and reclaim tasks stuck with stale sessions.
    /// </summary>
    private async SystemTask ReclaimStaleTasksAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await using var scope = serviceScopeFactory.CreateAsyncScope();
                    var taskAssignment = scope.ServiceProvider.GetRequiredService<ITaskAssignmentService>();

                    var reclaimResult = await taskAssignment.ReclaimStaleTasksAsync(ct).ConfigureAwait(false);
                    if (reclaimResult.IsFailure)
                    {
                        LogErrorReclaimingStaleTasks(logger, reclaimResult.Error);
                    }
                }
                catch (Exception ex)
                {
                    LogErrorInStaleTaskReclamation(logger, ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            LogStaleClaimReclamationCancelled(logger);
        }
    }

    /// <summary>
    ///     Gracefully shutdown the session.
    /// </summary>
    private async SystemTask ShutdownSessionAsync(CancellationToken ct)
    {
        try
        {
            LogShuttingDownSession(logger, _sessionId);

            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var sessionRepository = scope.ServiceProvider.GetRequiredService<IExecutionSessionRepository>();

            var sessionResult = await sessionRepository.GetByIdAsync(_sessionId, ct);
            if (sessionResult.IsSuccess)
            {
                var session = sessionResult.Value;
                session.Shutdown();

                var updateResult = await sessionRepository.UpdateAsync(session, ct);
                if (updateResult.IsFailure)
                {
                    LogFailedMarkSessionInactive(logger, updateResult.Error);
                }
            }

            LogWorkerShutdownComplete(logger);
        }
        catch (Exception ex)
        {
            LogErrorDuringSessionShutdown(logger, ex);
        }
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Ralph loop worker starting: {WorkerName}")]
    private static partial void LogWorkerStarting(ILogger logger, string workerName);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Worker cancellation requested")]
    private static partial void LogWorkerCancellationRequested(ILogger logger);

    [LoggerMessage(EventId = 3, Level = LogLevel.Error, Message = "Failed to create session: {Error}")]
    private static partial void LogFailedCreateSession(ILogger logger, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Failed to register session: {Error}")]
    private static partial void LogFailedRegisterSession(ILogger logger, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Session registered: {SessionId}")]
    private static partial void LogSessionRegistered(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Error registering session")]
    private static partial void LogErrorRegisteringSession(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information, Message = "Task polling cancelled")]
    private static partial void LogTaskPollingCancelled(ILogger logger);

    [LoggerMessage(EventId = 8, Level = LogLevel.Error, Message = "Error getting next task: {Error}")]
    private static partial void LogErrorGettingNextTask(ILogger logger, string error);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information,
        Message = "Processing task {TaskId}: prompt_length={PromptLength}, max_iterations={MaxIterations}")]
    private static partial void LogProcessingTask(ILogger logger, Guid taskId, int promptLength, int maxIterations);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "Ralph loop execution failed for task {TaskId}: {Error}")]
    private static partial void LogRalphLoopExecutionFailed(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Error, Message = "Unexpected error processing next task")]
    private static partial void LogUnexpectedErrorProcessingTask(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 12, Level = LogLevel.Warning, Message = "Session not found for heartbeat update")]
    private static partial void LogSessionNotFoundForHeartbeat(ILogger logger);

    [LoggerMessage(EventId = 13, Level = LogLevel.Error, Message = "Failed to update heartbeat: {Error}")]
    private static partial void LogFailedUpdateHeartbeat(ILogger logger, string error);

    [LoggerMessage(EventId = 14, Level = LogLevel.Error, Message = "Error updating heartbeat")]
    private static partial void LogErrorUpdatingHeartbeat(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 15, Level = LogLevel.Information, Message = "Heartbeat update cancelled")]
    private static partial void LogHeartbeatUpdateCancelled(ILogger logger);

    [LoggerMessage(EventId = 16, Level = LogLevel.Error, Message = "Error reclaiming stale tasks: {Error}")]
    private static partial void LogErrorReclaimingStaleTasks(ILogger logger, string error);

    [LoggerMessage(EventId = 17, Level = LogLevel.Error, Message = "Error in stale task reclamation")]
    private static partial void LogErrorInStaleTaskReclamation(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "Stale claim reclamation cancelled")]
    private static partial void LogStaleClaimReclamationCancelled(ILogger logger);

    [LoggerMessage(EventId = 19, Level = LogLevel.Information, Message = "Shutting down session {SessionId}")]
    private static partial void LogShuttingDownSession(ILogger logger, Guid sessionId);

    [LoggerMessage(EventId = 20, Level = LogLevel.Error, Message = "Failed to mark session as inactive: {Error}")]
    private static partial void LogFailedMarkSessionInactive(ILogger logger, string error);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Worker shutdown complete")]
    private static partial void LogWorkerShutdownComplete(ILogger logger);

    [LoggerMessage(EventId = 22, Level = LogLevel.Error, Message = "Error during session shutdown")]
    private static partial void LogErrorDuringSessionShutdown(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning,
        Message = "Phase check skipped for task {TaskId}: {Error}")]
    private static partial void LogPhaseCheckSkipped(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 24, Level = LogLevel.Error,
        Message = "Phase orchestrator failed for task {TaskId}: {Error}")]
    private static partial void LogPhaseOrchestratorError(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 25, Level = LogLevel.Error,
        Message = "Unexpected error in phase orchestrator for task {TaskId}")]
    private static partial void LogPhaseOrchestratorException(ILogger logger, Exception exception, Guid taskId);
}
