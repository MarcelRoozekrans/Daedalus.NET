using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Commands.ResumeTask;

/// <summary>
///     Command to resume an abandoned task, making it eligible for execution again.
/// </summary>
public record ResumeTaskCommand(
    Guid TaskId,
    Guid? NewSessionId) : ICommand<Result<TaskDto>>;
