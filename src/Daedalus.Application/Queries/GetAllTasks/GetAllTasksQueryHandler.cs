using ZeroAlloc.Results;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Queries.GetAllTasks;

/// <summary>
///     Handles GetAllTasksQuery by retrieving all tasks from the repository with pagination support.
/// </summary>
public sealed class GetAllTasksQueryHandler(ITaskRepository taskRepository)
    : IRequestHandler<GetAllTasksQuery, Result<PagedResultDto<TaskDto>>>
{
    /// <summary>
    ///     Retrieves all tasks with optional pagination and returns them as DTO list.
    /// </summary>
    public async ValueTask<Result<PagedResultDto<TaskDto>>> Handle(GetAllTasksQuery query,
        CancellationToken cancellationToken)
    {
        // Validate query
        if (query.Page < 1)
        {
            return Result<PagedResultDto<TaskDto>>.Failure("Page must be greater than 0");
        }

        if (query.PageSize < 1 || query.PageSize > 100)
        {
            return Result<PagedResultDto<TaskDto>>.Failure("PageSize must be between 1 and 100");
        }

        // Get total count of pending tasks (optimized query - count only, no entities loaded)
        var countResult = await taskRepository.GetPendingCountAsync(cancellationToken);
        if (countResult.IsFailure)
        {
            return Result<PagedResultDto<TaskDto>>.Failure(countResult.Error);
        }

        var totalCount = countResult.Value;

        // Fetch only the requested page from database (pagination at DB level for performance)
        var skip = (query.Page - 1) * query.PageSize;
        var paginatedTasksResult = await taskRepository.GetPendingAsync(
            skip,
            query.PageSize,
            cancellationToken);

        if (paginatedTasksResult.IsFailure)
        {
            return Result<PagedResultDto<TaskDto>>.Failure(paginatedTasksResult.Error);
        }

        var paginatedTasks = paginatedTasksResult.Value;

        // Map to DTOs
        var taskDtos = paginatedTasks
            .Select(TaskDtoMapper.ToDto)
            .ToList();

        // Return paginated result
        var pagedResult = new PagedResultDto<TaskDto>(
            taskDtos,
            totalCount,
            query.Page,
            query.PageSize);

        return Result<PagedResultDto<TaskDto>>.Success(pagedResult);
    }
}
