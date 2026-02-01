using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Application.Mappers;
using Daedalus.Application.Services;
using Task = Daedalus.Domain.Entities.Task;

namespace Daedalus.Application.Commands.CreateTask;

/// <summary>
///     Handles CreateTaskCommand by validating input, creating the domain entity, and persisting it.
/// </summary>
public sealed class CreateTaskCommandHandler(ITaskRepository taskRepository)
    : ICommandHandler<CreateTaskCommand, Result<TaskDto>>
{
    /// <summary>
    ///     Creates a new task and returns its DTO representation.
    /// </summary>
    public async Task<Result<TaskDto>> Handle(CreateTaskCommand command, CancellationToken cancellationToken)
    {
        // Validate and normalize command input using optimized zero-allocation validation
        var promptValidation = PerformanceOptimizations.ValidateAndTrimString(command.Prompt, out var promptError);
        if (promptValidation == null)
        {
            return Result.Failure<TaskDto>($"Prompt: {promptError}");
        }

        var promiseValidation =
            PerformanceOptimizations.ValidateAndTrimString(command.CompletionPromise, out var promiseError);
        if (promiseValidation == null)
        {
            return Result.Failure<TaskDto>($"CompletionPromise: {promiseError}");
        }

        if (command.MaxIterations <= 0)
        {
            return Result.Failure<TaskDto>("MaxIterations must be greater than 0");
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
            promptValidation,
            promiseValidation,
            command.MaxIterations);

        if (createResult.IsFailure)
        {
            return Result.Failure<TaskDto>(createResult.Error);
        }

        var task = createResult.Value;

        // Persist the task
        var addResult = await taskRepository.AddAsync(task, cancellationToken);

        // Return failure if persistence failed
        if (addResult.IsFailure)
        {
            return Result.Failure<TaskDto>(addResult.Error);
        }

        // Map domain entity to DTO
        var taskDto = TaskDtoMapper.ToDto(addResult.Value);

        return Result.Success(taskDto);
    }
}
