using ZeroAlloc.Results;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.DeleteProject;

/// <summary>
///     Command to delete a project by ID.
/// </summary>
public readonly record struct DeleteProjectCommand(Guid Id) : IRequest<Result>;
