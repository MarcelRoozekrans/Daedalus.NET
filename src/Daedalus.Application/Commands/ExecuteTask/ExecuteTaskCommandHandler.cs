using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Mappers;
using Daedalus.Domain.Entities;
using TaskStatus = Daedalus.Domain.Entities.TaskStatus;

namespace Daedalus.Application.Commands.ExecuteTask;

/// <summary>
///     Handles ExecuteTaskCommand by retrieving the task, executing it via LLM, checking for completion,
///     and updating the task state. This handler extracts the core orchestration logic from RalphLoopService.
/// </summary>
public sealed class ExecuteTaskCommandHandler(
    ITaskRepository taskRepository,
    IRalphAgentFactory agentFactory) : ICommandHandler<ExecuteTaskCommand, Result<ExecuteTaskResult>>
{
    /// <summary>
    ///     Executes a task: fetches it, runs the LLM, checks for completion, and updates state.
    /// </summary>
    public async Task<Result<ExecuteTaskResult>> Handle(ExecuteTaskCommand command, CancellationToken cancellationToken)
    {
        // Validate command
        if (command.TaskId == Guid.Empty)
        {
            return Result.Failure<ExecuteTaskResult>("TaskId cannot be empty");
        }

        if (command.SessionId == Guid.Empty)
        {
            return Result.Failure<ExecuteTaskResult>("SessionId cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(command.WorkerName))
        {
            return Result.Failure<ExecuteTaskResult>("WorkerName cannot be empty");
        }

        // Fetch the task
        var taskResult = await taskRepository.GetByIdAsync(command.TaskId, cancellationToken);
        if (taskResult.IsFailure)
        {
            return Result.Failure<ExecuteTaskResult>($"Task not found: {taskResult.Error}");
        }

        var task = taskResult.Value;

        // Validate task can be executed
        if (task.Status != TaskStatus.Pending && task.Status != TaskStatus.InProgress)
        {
            return Result.Failure<ExecuteTaskResult>($"Task cannot be executed: current status is {task.Status}");
        }

        if (task.IterationCount >= task.MaxIterations)
        {
            return Result.Failure<ExecuteTaskResult>("Task has reached maximum iterations");
        }

        // Claim the task if not already claimed
        if (task.Status == TaskStatus.Pending)
        {
            var claimResult = task.Claim(command.SessionId);
            if (claimResult.IsFailure)
            {
                return Result.Failure<ExecuteTaskResult>($"Failed to claim task: {claimResult.Error}");
            }
        }

        try
        {
            // Execute via LLM
            var llmResult = await agentFactory.InvokeAsync(task.Prompt, cancellationToken);
            if (llmResult.IsFailure)
            {
                return Result.Failure<ExecuteTaskResult>($"LLM invocation failed: {llmResult.Error}");
            }

            var llmResponse = llmResult.Value;

            // Check if completion promise is found in response
            // Using IndexOf instead of Contains avoids unnecessary allocations for case-insensitive comparison
#pragma warning disable CA2249 // Use 'string.Contains' instead of 'string.IndexOf' - IndexOf is more efficient for our use case
            var completionPromiseFound =
                llmResponse.IndexOf(task.CompletionPromise, StringComparison.OrdinalIgnoreCase) >= 0;
#pragma warning restore CA2249

            // Create execution record
            var execution = new TaskExecution
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                SessionId = command.SessionId,
                IterationNumber = task.IterationCount + 1,
                Prompt = task.Prompt,
                LlmResponse = llmResponse,
                CompletionPromiseFound = completionPromiseFound,
                ExecutedAt = DateTime.UtcNow,
                ExecutionDuration = TimeSpan.Zero,
                Error = null
            };

            // Record execution (updates task internally)
            var recordResult = task.RecordExecution(execution);
            if (recordResult.IsFailure)
            {
                return Result.Failure<ExecuteTaskResult>($"Failed to record execution: {recordResult.Error}");
            }

            // Persist updates
            var updateResult = await taskRepository.UpdateAsync(task, cancellationToken);
            if (updateResult.IsFailure)
            {
                return Result.Failure<ExecuteTaskResult>($"Failed to update task: {updateResult.Error}");
            }

            // Map results to DTO and result record
            var taskDto = TaskDtoMapper.ToDto(task);
            var lastExecution = task.Executions[^1];
            var executionDto = TaskDtoMapper.ToExecutionDto(lastExecution);

            var result = new ExecuteTaskResult(
                taskDto,
                executionDto,
                completionPromiseFound,
                completionPromiseFound
                    ? $"Task completed! Completion promise '{task.CompletionPromise}' found."
                    : $"Task execution #{task.IterationCount} complete. {task.MaxIterations - task.IterationCount} iterations remaining.");

            return Result.Success(result);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure<ExecuteTaskResult>("Task execution was cancelled");
        }
        catch (Exception ex)
        {
            // Task abandon on failure
            var abandonResult = task.Abandon();
            if (abandonResult.IsSuccess)
            {
                await taskRepository.UpdateAsync(task, cancellationToken);
            }

            return Result.Failure<ExecuteTaskResult>($"Task execution failed: {ex.Message}");
        }
    }
}
