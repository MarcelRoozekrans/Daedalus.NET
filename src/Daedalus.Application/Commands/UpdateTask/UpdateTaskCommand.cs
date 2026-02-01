using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Commands.UpdateTask;

/// <summary>
///     Command to update an existing task's metadata.
/// </summary>
public record UpdateTaskCommand(
    Guid TaskId,
    string? Title,
    string? Description,
    Priority? Priority,
    string? Phase,
    int? ParallelGroup,
    Complexity? EstimatedComplexity,
    string? Prompt,
    string? CompletionPromise,
    int? MaxIterations) : ICommand<Result<TaskDto>>;
