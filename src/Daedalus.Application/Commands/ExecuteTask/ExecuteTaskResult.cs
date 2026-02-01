using Daedalus.Application.DTOs;

namespace Daedalus.Application.Commands.ExecuteTask;

/// <summary>
///     Result of task execution containing the task and execution details.
/// </summary>
public record ExecuteTaskResult(
    TaskDto Task,
    TaskExecutionDto Execution,
    bool IsCompleted,
    string? CompletionMessage);
