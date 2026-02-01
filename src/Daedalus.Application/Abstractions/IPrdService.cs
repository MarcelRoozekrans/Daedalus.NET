using CSharpFunctionalExtensions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Service for PRD generation and task conversion workflow.
/// </summary>
public interface IPrdService
{
    /// <summary>
    ///     Generates a PRD from user requirements using LLM.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <param name="userRequirements">The user's requirements.</param>
    /// <param name="context">Optional additional context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The generated PRD or failure.</returns>
    Task<Result<PrdResponseDto>> GeneratePrdAsync(
        Guid projectId,
        string userRequirements,
        string? context = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Converts selected PRD items to tasks and persists them.
    /// </summary>
    /// <param name="projectId">The project ID.</param>
    /// <param name="selectedItems">The PRD items to convert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The created tasks or failure.</returns>
    Task<Result<List<TaskDto>>> ConvertToTasksAsync(
        Guid projectId,
        IReadOnlyList<PrdItemForConversionDto> selectedItems,
        CancellationToken cancellationToken = default);
}
