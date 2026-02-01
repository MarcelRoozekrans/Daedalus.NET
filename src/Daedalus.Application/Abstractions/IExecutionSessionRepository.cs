using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Repository for execution session tracking.
/// </summary>
public interface IExecutionSessionRepository
{
    /// <summary>Gets a session by ID.</summary>
    Task<Result<ExecutionSession>> GetByIdAsync(Guid id, CancellationToken ct);

    /// <summary>Adds a new session.</summary>
    Task<Result<ExecutionSession>> AddAsync(ExecutionSession session, CancellationToken ct);

    /// <summary>Updates session (heartbeat, etc).</summary>
    Task<Result> UpdateAsync(ExecutionSession session, CancellationToken ct);

    /// <summary>Gets active sessions.</summary>
    Task<Result<IReadOnlyList<ExecutionSession>>> GetActiveAsync(CancellationToken ct);
}
