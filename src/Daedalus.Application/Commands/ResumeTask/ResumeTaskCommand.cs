using ZeroAlloc.Results;
using Daedalus.Application.DTOs;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.ResumeTask;

/// <summary>
///     Command to resume an abandoned task, making it eligible for execution again.
/// </summary>
public readonly record struct ResumeTaskCommand(
    Guid TaskId,
    Guid? NewSessionId) : IRequest<Result<TaskDto>>;
