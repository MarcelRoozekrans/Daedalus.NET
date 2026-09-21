using ZeroAlloc.Results;
using Daedalus.Application.DTOs;
using ZeroAlloc.Mediator;

namespace Daedalus.Application.Commands.ConvertPrdToTasks;

/// <summary>
///     Command to convert PRD items to actual Task entities and persist them.
/// </summary>
public readonly record struct ConvertPrdToTasksCommand(
    Guid ProjectId,
    IReadOnlyList<PrdItemForConversionDto> PrdItems) : IRequest<Result<List<TaskDto>>>;
