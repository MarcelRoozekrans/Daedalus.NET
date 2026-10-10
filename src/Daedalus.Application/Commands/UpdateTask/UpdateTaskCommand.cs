using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Daedalus.Application.Commands.UpdateTask;

/// <summary>
///     Command to update an existing task's metadata.
/// </summary>
public readonly record struct UpdateTaskCommand(
    Guid TaskId,
    string? Title,
    string? Description,
    Priority? Priority,
    string? Phase,
    int? ParallelGroup,
    Complexity? EstimatedComplexity,
    string? Prompt) : IRequest<Result<TaskDto>>;
