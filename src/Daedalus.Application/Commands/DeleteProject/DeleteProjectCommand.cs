using ZeroAlloc.Mediator;
using ZeroAlloc.Results;

namespace Daedalus.Application.Commands.DeleteProject;

/// <summary>
///     Command to delete a project by ID.
/// </summary>
public readonly record struct DeleteProjectCommand(Guid Id) : IRequest<Result>;
