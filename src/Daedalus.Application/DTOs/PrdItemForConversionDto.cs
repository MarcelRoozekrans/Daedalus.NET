using Daedalus.Domain.Entities;

namespace Daedalus.Application.DTOs;

/// <summary>
///     Represents a PRD item ready for conversion to a task.
/// </summary>
public record PrdItemForConversionDto(
    string Title,
    string Description,
    string AcceptanceCriteria,
    Priority Priority,
    Complexity Complexity,
    IReadOnlyList<string> Dependencies,
    string Phase,
    int ParallelGroup);
