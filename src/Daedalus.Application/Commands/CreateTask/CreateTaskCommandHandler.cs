using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Daedalus.Application.Services;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Commands.CreateTask;

/// <summary>
///     Handles CreateTaskCommand by validating input, creating the domain entity, and persisting it.
/// </summary>
public sealed class CreateTaskCommandHandler(ITaskRepository taskRepository)
    : IRequestHandler<CreateTaskCommand, Result<TaskDto>>
{
    /// <summary>
    ///     Creates a new task and returns its DTO representation.
    /// </summary>
    public async ValueTask<Result<TaskDto>> Handle(CreateTaskCommand command, CancellationToken ct)
    {
        // Validate and normalize command input using optimized zero-allocation validation
        var promptValidation = PerformanceOptimizations.ValidateAndTrimString(command.Prompt, out var promptError);
        if (promptValidation == null)
        {
            return Result<TaskDto>.Failure($"Prompt: {promptError}");
        }

        // Create domain entity using factory method
        var createResult = Task.Create(
            Guid.NewGuid(),
            command.ProjectId,
            command.TaskId,
            command.Title,
            command.Description,
            command.Priority,
            command.Phase,
            command.ParallelGroup,
            command.Complexity,
            promptValidation);

        if (createResult.IsFailure)
        {
            return Result<TaskDto>.Failure(createResult.Error);
        }

        var task = createResult.Value;

        // Persist the task
        var addResult = await taskRepository.AddAsync(task, ct);

        // Return failure if persistence failed
        if (addResult.IsFailure)
        {
            return Result<TaskDto>.Failure(addResult.Error);
        }

        // Map domain entity to DTO
        var taskDto = TaskDtoMapper.ToDto(addResult.Value, WorkflowRunStatus.Unknown);

        return Result<TaskDto>.Success(taskDto);
    }
}
