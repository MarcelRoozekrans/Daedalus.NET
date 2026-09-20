using ZeroAlloc.Results;
using Daedalus.Application.DTOs;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Queries.GetAllTasks;

/// <summary>
///     Query to retrieve all tasks with optional pagination.
/// </summary>
public readonly record struct GetAllTasksQuery(
    int Page = 1,
    int PageSize = 10) : IRequest<Result<PagedResultDto<TaskDto>>>;
