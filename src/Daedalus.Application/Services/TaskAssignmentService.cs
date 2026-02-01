using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Services;

/// <summary>
///     Default implementation using distributed locking via database.
/// </summary>
public sealed partial class TaskAssignmentService(
    ITaskRepository taskRepository,
    ILogger<TaskAssignmentService> logger) : ITaskAssignmentService
{
    /// <summary>
    ///     Atomically claims the next pending task (using SELECT FOR UPDATE SKIP LOCKED).
    /// </summary>
    public async Task<Result<Task?>> GetNextAvailableTaskAsync(Guid sessionId, CancellationToken ct)
    {
        var result = await taskRepository.ClaimNextAsync(sessionId, ct).ConfigureAwait(false);

        if (result.IsSuccess && result.Value is not null)
        {
            LogTaskClaimed(logger, sessionId, result.Value.Id);
        }

        return result;
    }

    /// <summary>
    ///     Finds tasks stuck with stale sessions and marks them as abandoned for reclaim.
    /// </summary>
    public async Task<Result> ReclaimStaleTasksAsync(CancellationToken ct)
    {
        var staleResult = await taskRepository.GetStaleInProgressAsync(TimeSpan.FromMinutes(5), ct)
            .ConfigureAwait(false);

        if (staleResult.IsFailure)
        {
            return staleResult;
        }

        var staleCount = staleResult.Value.Count;
        if (staleCount == 0)
        {
            return Result.Success();
        }

        LogStaleTasksFound(logger, staleCount);

        foreach (var task in staleResult.Value)
        {
            var abandonResult = task.Abandon();
            if (abandonResult.IsFailure)
            {
                LogFailedAbandonTask(logger, task.Id, abandonResult.Error);
                continue;
            }

            var updateResult = await taskRepository.UpdateAsync(task, ct).ConfigureAwait(false);
            if (updateResult.IsFailure)
            {
                LogFailedUpdateAbandonedTask(logger, task.Id, updateResult.Error);
                continue;
            }

            LogTaskReclaimed(logger, task.Id);
        }

        return Result.Success();
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Information, Message = "Session {SessionId} claimed task {TaskId}")]
    private static partial void LogTaskClaimed(ILogger logger, Guid sessionId, Guid taskId);

    [LoggerMessage(EventId = 31, Level = LogLevel.Warning,
        Message = "Found {StaleCount} tasks stuck with stale sessions")]
    private static partial void LogStaleTasksFound(ILogger logger, int staleCount);

    [LoggerMessage(EventId = 32, Level = LogLevel.Error, Message = "Failed to abandon task {TaskId}: {Error}")]
    private static partial void LogFailedAbandonTask(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 33, Level = LogLevel.Error, Message = "Failed to update abandoned task {TaskId}: {Error}")]
    private static partial void LogFailedUpdateAbandonedTask(ILogger logger, Guid taskId, string error);

    [LoggerMessage(EventId = 34, Level = LogLevel.Information, Message = "Reclaimed task {TaskId} from stale session")]
    private static partial void LogTaskReclaimed(ILogger logger, Guid taskId);
}
