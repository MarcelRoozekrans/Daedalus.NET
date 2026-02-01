using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Queries.GetAllTasks;

/// <summary>
///     Query to retrieve all tasks with optional pagination.
/// </summary>
public record GetAllTasksQuery(
    int Page = 1,
    int PageSize = 10) : IQuery<Result<PagedResultDto<TaskDto>>>;
