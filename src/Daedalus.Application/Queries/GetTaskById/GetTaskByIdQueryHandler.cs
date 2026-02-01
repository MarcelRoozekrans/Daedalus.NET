using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;

namespace Daedalus.Application.Queries.GetTaskById;

/// <summary>
///     Handles GetTaskByIdQuery by retrieving a task from the repository and returning its DTO.
/// </summary>
public sealed class GetTaskByIdQueryHandler(ITaskRepository taskRepository)
    : IQueryHandler<GetTaskByIdQuery, Result<TaskDto>>
{
    /// <summary>
    ///     Retrieves a task by ID and returns its DTO representation.
    /// </summary>
    public async Task<Result<TaskDto>> Handle(GetTaskByIdQuery query, CancellationToken cancellationToken)
    {
        // Validate query
        if (query.TaskId == Guid.Empty)
        {
            return Result.Failure<TaskDto>("TaskId cannot be empty");
        }

        // Fetch the task
        var result = await taskRepository.GetByIdAsync(query.TaskId, cancellationToken);

        // Return failure if not found
        if (result.IsFailure)
        {
            return Result.Failure<TaskDto>(result.Error);
        }

        // Map to DTO and return
        var taskDto = TaskDtoMapper.ToDto(result.Value);
        return Result.Success(taskDto);
    }
}
