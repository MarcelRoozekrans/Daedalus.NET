using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Commands.UpdateProject;

/// <summary>
///     Command to update an existing project.
/// </summary>
public record UpdateProjectCommand(
    Guid Id,
    string? ProjectName,
    string? Description,
    string? Version) : ICommand<Result<ProjectDto>>;
