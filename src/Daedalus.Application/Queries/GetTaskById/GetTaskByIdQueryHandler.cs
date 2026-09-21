using ZeroAlloc.Results;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Queries.GetTaskById;

/// <summary>
///     Handles GetTaskByIdQuery by retrieving a task from the repository and returning its DTO.
/// </summary>
public sealed class GetTaskByIdQueryHandler(ITaskRepository taskRepository)
    : IRequestHandler<GetTaskByIdQuery, Result<TaskDto>>
{
    /// <summary>
    ///     Retrieves a task by ID and returns its DTO representation.
    /// </summary>
    public async ValueTask<Result<TaskDto>> Handle(GetTaskByIdQuery query, CancellationToken ct)
    {
        // Validate query
        if (query.TaskId == Guid.Empty)
        {
            return Result<TaskDto>.Failure("TaskId cannot be empty");
        }

        // Fetch the task
        var result = await taskRepository.GetByIdAsync(query.TaskId, ct);

        // Return failure if not found
        if (result.IsFailure)
        {
            return Result<TaskDto>.Failure(result.Error);
        }

        // Map to DTO and return
        var taskDto = TaskDtoMapper.ToDto(result.Value);
        return Result<TaskDto>.Success(taskDto);
    }
}
