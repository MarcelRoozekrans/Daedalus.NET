using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Commands.CreateTask;

/// <summary>
///     Command to create a new Task with specified parameters.
/// </summary>
public record CreateTaskCommand(
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
    int MaxIterations) : ICommand<Result<TaskDto>>;
