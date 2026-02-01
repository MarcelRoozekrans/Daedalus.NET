using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Commands.GeneratePrd;

/// <summary>
///     Command to generate a PRD from user requirements using LLM.
/// </summary>
public record GeneratePrdCommand(
    Guid ProjectId,
    string UserRequirements,
    string? Context = null,
    int MaxItems = 10) : ICommand<Result<PrdResponseDto>>;
