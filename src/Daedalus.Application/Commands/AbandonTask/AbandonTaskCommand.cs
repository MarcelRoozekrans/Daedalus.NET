using ZeroAlloc.Results;
using Daedalus.Application.DTOs;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.AbandonTask;

/// <summary>
///     Command to abandon a task (mark it as abandoned and set a result/reason).
/// </summary>
public readonly record struct AbandonTaskCommand(
    Guid TaskId,
    string Reason) : IRequest<Result<TaskDto>>;
