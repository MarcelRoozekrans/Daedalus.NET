using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Commands.AbandonTask;

/// <summary>
///     Command to abandon a task (mark it as abandoned and set a result/reason).
/// </summary>
public record AbandonTaskCommand(
    Guid TaskId,
    string Reason) : ICommand<Result<TaskDto>>;
