using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Commands.CreateProject;

/// <summary>
///     Command to create a new Project.
/// </summary>
public record CreateProjectCommand(
    string ProjectName,
    string Description,
    string Version) : ICommand<Result<ProjectDto>>;
