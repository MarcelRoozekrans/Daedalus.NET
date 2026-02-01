namespace Daedalus.Application.DTOs;

/// <summary>
///     Request body for plan regeneration.
/// </summary>
/// <param name="WorkspacePath">Optional workspace path override. If null, uses the configured workspace path.</param>
public record RegeneratePlanDto(string? WorkspacePath = null);
