using ZeroAlloc.Results;
using Daedalus.Application.DTOs;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Queries.GetTaskById;

/// <summary>
///     Query to retrieve a specific task by its ID.
/// </summary>
public readonly record struct GetTaskByIdQuery(Guid TaskId) : IRequest<Result<TaskDto>>;
