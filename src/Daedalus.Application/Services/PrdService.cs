using ZeroAlloc.Results;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Commands.ConvertPrdToTasks;
using Daedalus.Application.Commands.GeneratePrd;
using Daedalus.Application.DTOs;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Services;

/// <summary>
///     Implementation of PRD service coordinating generation and task conversion. Internal because it
///     takes the internal IMediator in its constructor - callers outside this assembly depend on the
///     public IPrdService interface, never on this concrete type.
/// </summary>
internal sealed class PrdService(IMediator mediator) : IPrdService
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

        return await mediator.Send(command, cancellationToken);
    }

    public async Task<Result<List<TaskDto>>> ConvertToTasksAsync(
        Guid projectId,
        IReadOnlyList<PrdItemForConversionDto> selectedItems,
        CancellationToken cancellationToken = default)
    {
        var command = new ConvertPrdToTasksCommand(
            projectId,
            selectedItems);

        return await mediator.Send(command, cancellationToken);
    }
}
