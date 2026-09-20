using ZeroAlloc.Results;
using Daedalus.Application.DTOs;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.UpdateProject;

/// <summary>
///     Command to update an existing project.
/// </summary>
public readonly record struct UpdateProjectCommand(
    Guid Id,
    string? ProjectName,
    string? Description,
    string? Version) : IRequest<Result<ProjectDto>>;
