using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Application.Commands.DeleteTask;

/// <summary>
///     Command to delete a task by its ID.
/// </summary>
public record DeleteTaskCommand(Guid TaskId) : ICommand<Result>;
