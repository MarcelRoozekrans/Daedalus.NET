using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Daedalus.Application.Commands.CreateTask;

/// <summary>
///     Command to create a new Task with specified parameters. A missing <c>Prompt</c> takes the description,
///     and a missing <c>ParallelGroup</c> takes 1.
/// </summary>
public readonly record struct CreateTaskCommand(
    Guid ProjectId,
    string TaskId,
    string Title,
    string Description,
    Priority Priority,
    string Phase,
    int? ParallelGroup,
    Complexity Complexity,
    string? Prompt) : IRequest<Result<TaskDto>>;
