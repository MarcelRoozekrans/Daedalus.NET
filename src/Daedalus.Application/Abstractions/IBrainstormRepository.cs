using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Repository for brainstorm session persistence and querying.
/// </summary>
public interface IBrainstormRepository
{
    /// <summary>Adds a new brainstorm session.</summary>
    Task<Result<BrainstormSession>> AddAsync(BrainstormSession session, CancellationToken ct);

    /// <summary>Gets a brainstorm session by ID, including ordered messages.</summary>
    Task<Result<BrainstormSession>> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Gets all brainstorm sessions for a project, ordered by newest first.</summary>
    Task<Result<IReadOnlyList<BrainstormSession>>> GetByProjectIdAsync(Guid projectId, CancellationToken ct);

    /// <summary>Updates an existing brainstorm session.</summary>
    Task<Result> UpdateAsync(BrainstormSession session, CancellationToken ct);
}
