using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;
using ZLinq;
using Task = Daedalus.Domain.Entities.Task;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Application.Services;

/// <summary>
///     Phase chaining orchestrator: when a task completes, evaluates whether
///     dependent sibling tasks are now fully unblocked (all dependencies satisfied).
///     This closes the gap between having Phase/Dependencies fields on the domain model
///     and actually orchestrating multi-phase execution per the Ralph Wiggum technique.
/// </summary>
public sealed partial class PhaseOrchestrator(
    ITaskRepository taskRepository,
    ILogger<PhaseOrchestrator> logger) : IPhaseOrchestrator
{
    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<string>>> OnTaskCompletedAsync(
        Task completedTask,
        CancellationToken ct)
    {
        if (completedTask.Status != TaskStatus.Completed)
        {
            return Result.Failure<IReadOnlyList<string>>(
                $"Task {completedTask.TaskId} is not completed (status: {completedTask.Status})");
        }

        LogTaskCompletedPhaseCheck(logger, completedTask.TaskId, completedTask.Phase, completedTask.ProjectId);

        // Get all sibling tasks in the same project
        var siblingResult = await taskRepository.GetByProjectIdAsync(completedTask.ProjectId, ct)
            .ConfigureAwait(false);

        if (siblingResult.IsFailure)
        {
            LogErrorLoadingSiblingTasks(logger, completedTask.ProjectId, siblingResult.Error);
            return Result.Failure<IReadOnlyList<string>>(
                $"Failed to load project tasks: {siblingResult.Error}");
        }

        var siblings = siblingResult.Value;

        // Build a lookup of TaskId → Status for efficient dependency resolution
        var taskStatusLookup = siblings
            .AsValueEnumerable()
            .ToDictionary(t => t.TaskId, t => t.Status, StringComparer.Ordinal);

        // Find pending tasks that depend on the completed task
        var unblockedTaskIds = new List<string>();

        foreach (var candidate in siblings.AsValueEnumerable())
        {
            // Only evaluate Pending tasks that have dependencies
            if (candidate.Status != TaskStatus.Pending || candidate.Dependencies.Count == 0)
            {
                continue;
            }

            // Check if this candidate depends on the just-completed task
            if (!candidate.Dependencies.Contains(completedTask.TaskId, StringComparer.Ordinal))
            {
                continue;
            }

            // Check if ALL dependencies of this candidate are now Completed
            var allDependenciesMet = true;

            foreach (var depTaskId in candidate.Dependencies)
            {
                if (!taskStatusLookup.TryGetValue(depTaskId, out var depStatus) ||
                    depStatus != TaskStatus.Completed)
                {
                    allDependenciesMet = false;
                    break;
                }
            }

            if (allDependenciesMet)
            {
                unblockedTaskIds.Add(candidate.TaskId);
                LogTaskUnblocked(logger, candidate.TaskId, candidate.Phase, completedTask.TaskId);
            }
            else
            {
                LogTaskStillBlocked(logger, candidate.TaskId, candidate.Phase);
            }
        }

        if (unblockedTaskIds.Count > 0)
        {
            LogPhaseChainingSummary(logger, unblockedTaskIds.Count, completedTask.TaskId, completedTask.Phase);
        }
        else
        {
            LogNoDependentsUnblocked(logger, completedTask.TaskId);
        }

        return Result.Success<IReadOnlyList<string>>(unblockedTaskIds.AsReadOnly());
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 100, Level = LogLevel.Information,
        Message = "Phase check: task {TaskId} completed in phase '{Phase}' for project {ProjectId}")]
    private static partial void LogTaskCompletedPhaseCheck(
        ILogger logger, string taskId, string phase, Guid projectId);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error,
        Message = "Failed to load sibling tasks for project {ProjectId}: {Error}")]
    private static partial void LogErrorLoadingSiblingTasks(
        ILogger logger, Guid projectId, string error);

    [LoggerMessage(EventId = 102, Level = LogLevel.Information,
        Message = "Task {TaskId} in phase '{Phase}' is now UNBLOCKED (dependency {CompletedTaskId} satisfied)")]
    private static partial void LogTaskUnblocked(
        ILogger logger, string taskId, string phase, string completedTaskId);

    [LoggerMessage(EventId = 103, Level = LogLevel.Debug,
        Message = "Task {TaskId} in phase '{Phase}' still has unsatisfied dependencies")]
    private static partial void LogTaskStillBlocked(
        ILogger logger, string taskId, string phase);

    [LoggerMessage(EventId = 104, Level = LogLevel.Information,
        Message = "Phase chaining: {UnblockedCount} task(s) unblocked after {CompletedTaskId} in phase '{Phase}'")]
    private static partial void LogPhaseChainingSummary(
        ILogger logger, int unblockedCount, string completedTaskId, string phase);

    [LoggerMessage(EventId = 105, Level = LogLevel.Debug,
        Message = "No dependents unblocked by task {TaskId} completion")]
    private static partial void LogNoDependentsUnblocked(
        ILogger logger, string taskId);
}
