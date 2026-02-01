using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.DTOs;

namespace Daedalus.Application.Queries.GetTaskById;

/// <summary>
///     Query to retrieve a specific task by its ID.
/// </summary>
public record GetTaskByIdQuery(Guid TaskId) : IQuery<Result<TaskDto>>;
