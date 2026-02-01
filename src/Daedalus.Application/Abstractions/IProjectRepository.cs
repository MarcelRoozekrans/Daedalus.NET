using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Repository for project persistence and querying.
/// </summary>
public interface IProjectRepository
{
    /// <summary>Gets a project by ID.</summary>
    Task<Result<Project>> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Adds a new project.</summary>
    Task<Result<Project>> AddAsync(Project project, CancellationToken ct);

    /// <summary>Updates an existing project.</summary>
    Task<Result> UpdateAsync(Project project, CancellationToken ct);

    /// <summary>Deletes a project by ID.</summary>
    Task<Result> DeleteAsync(Guid id, CancellationToken ct);
}
