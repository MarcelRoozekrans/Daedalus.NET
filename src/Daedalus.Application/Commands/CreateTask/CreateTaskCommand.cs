using ZeroAlloc.Results;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.CreateTask;

/// <summary>
///     Command to create a new Task with specified parameters.
/// </summary>
public readonly record struct CreateTaskCommand(
    Guid ProjectId,
    string TaskId,
    string Title,
    string Description,
    Priority Priority,
    string Phase,
    int ParallelGroup,
    Complexity Complexity,
    string Prompt,
    string CompletionPromise,
    int MaxIterations) : IRequest<Result<TaskDto>>;
