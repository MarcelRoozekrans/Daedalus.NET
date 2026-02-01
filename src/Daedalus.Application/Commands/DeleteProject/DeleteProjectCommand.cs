using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Application.Commands.DeleteProject;

/// <summary>
///     Command to delete a project by ID.
/// </summary>
public record DeleteProjectCommand(Guid Id) : ICommand<Result>;
