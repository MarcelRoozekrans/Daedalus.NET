using System.Diagnostics.CodeAnalysis;
using ZeroAlloc.Results;
using Daedalus.Application.DTOs;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.CreateProject;

/// <summary>
///     Command to create a new Project.
/// </summary>
[SuppressMessage("Design", "CA1054", Justification = "Command uses string for JSON deserialization")]
[SuppressMessage("Design", "CA1056", Justification = "Command uses string for JSON deserialization")]
public readonly record struct CreateProjectCommand(
    string ProjectName,
    string Description,
    string Version,
    string? RepositoryUrl = null,
    string? DefaultBranch = null) : IRequest<Result<ProjectDto>>;
