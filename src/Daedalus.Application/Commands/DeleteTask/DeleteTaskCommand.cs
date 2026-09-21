using ZeroAlloc.Results;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.DeleteTask;

/// <summary>
///     Command to delete a task by its ID.
/// </summary>
public readonly record struct DeleteTaskCommand(Guid TaskId) : IRequest<Result>;
