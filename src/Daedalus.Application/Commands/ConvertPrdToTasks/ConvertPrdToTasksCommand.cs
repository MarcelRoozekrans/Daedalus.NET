using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Commands.ConvertPrdToTasks;

/// <summary>
///     Command to convert PRD items to actual Task entities and persist them.
/// </summary>
public record ConvertPrdToTasksCommand(
    Guid ProjectId,
    IReadOnlyList<PrdItemForConversionDto> PrdItems) : ICommand<Result<List<TaskDto>>>;
