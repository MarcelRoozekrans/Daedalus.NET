using ZeroAlloc.Results;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.ExecuteTask;

/// <summary>
///     Command to execute a task by finding it, checking the completion promise, and updating its status.
///     Extracted from RalphLoopService to follow Command pattern for use cases.
/// </summary>
public readonly record struct ExecuteTaskCommand(
    Guid TaskId,
    Guid SessionId,
    string WorkerName) : IRequest<Result<ExecuteTaskResult>>;
