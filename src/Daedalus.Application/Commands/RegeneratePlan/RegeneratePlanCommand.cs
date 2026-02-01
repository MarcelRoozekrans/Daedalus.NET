using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Application.Commands.RegeneratePlan;

/// <summary>
///     Command to explicitly regenerate the fix_plan.md for a task's workspace.
///     The article: "Eventually, Ralph will run out of things to do in the task list.
///     Or, it goes completely off track." This command forces a fresh plan.
/// </summary>
public record RegeneratePlanCommand(
    Guid TaskId,
    string? WorkspacePath) : ICommand<Result<RegeneratePlanResult>>
{
    /// <summary>
    ///     Validates the command input. Returns Result.Failure with the first validation error, or Result.Success.
    /// </summary>
    public Result Validate()
    {
        if (TaskId == Guid.Empty)
            return Result.Failure("TaskId is required");

        if (WorkspacePath is not null && string.IsNullOrWhiteSpace(WorkspacePath))
            return Result.Failure("WorkspacePath cannot be whitespace-only");

        return Result.Success();
    }
}
