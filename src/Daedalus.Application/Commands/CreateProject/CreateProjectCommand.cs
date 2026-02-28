using System.Diagnostics.CodeAnalysis;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Commands.CreateProject;

/// <summary>
///     Command to create a new Project.
/// </summary>
[SuppressMessage("Design", "CA1054", Justification = "Command uses string for JSON deserialization")]
[SuppressMessage("Design", "CA1056", Justification = "Command uses string for JSON deserialization")]
public record CreateProjectCommand(
    string ProjectName,
    string Description,
    string Version,
    string? RepositoryUrl = null,
    string? DefaultBranch = null) : ICommand<Result<ProjectDto>>;
