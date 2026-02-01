using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.ConvertPrdToTasks;
using Daedalus.Application.Commands.GeneratePrd;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Services;

/// <summary>
///     Implementation of PRD service coordinating generation and task conversion.
/// </summary>
public sealed class PrdService(
    ICommandHandler<GeneratePrdCommand, Result<PrdResponseDto>> generatePrdHandler,
    ICommandHandler<ConvertPrdToTasksCommand, Result<List<TaskDto>>> convertToTasksHandler) : IPrdService
{
    public async Task<Result<PrdResponseDto>> GeneratePrdAsync(
        Guid projectId,
        string userRequirements,
        string? context = null,
        CancellationToken cancellationToken = default)
    {
        var command = new GeneratePrdCommand(
            projectId,
            userRequirements,
            context);

        return await generatePrdHandler.Handle(command, cancellationToken);
    }

    public async Task<Result<List<TaskDto>>> ConvertToTasksAsync(
        Guid projectId,
        IReadOnlyList<PrdItemForConversionDto> selectedItems,
        CancellationToken cancellationToken = default)
    {
        var command = new ConvertPrdToTasksCommand(
            projectId,
            selectedItems);

        return await convertToTasksHandler.Handle(command, cancellationToken);
    }
}
