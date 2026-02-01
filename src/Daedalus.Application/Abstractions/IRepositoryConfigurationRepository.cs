using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Repository for RepositoryConfiguration persistence and querying.
/// </summary>
public interface IRepositoryConfigurationRepository
{
    /// <summary>Gets all repository configurations.</summary>
    Task<Result<IReadOnlyList<RepositoryConfiguration>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Gets a repository configuration by ID (untracked, for read-only queries).</summary>
    Task<Result<RepositoryConfiguration>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Gets a tracked repository configuration by ID (for updates with change tracking).</summary>
    Task<Result<RepositoryConfiguration>> GetByIdTrackingAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a new repository configuration.</summary>
    Task<Result<RepositoryConfiguration>> AddAsync(RepositoryConfiguration repository, CancellationToken ct = default);

    /// <summary>Updates an existing repository configuration.</summary>
    Task<Result> UpdateAsync(RepositoryConfiguration repository, CancellationToken ct = default);

    /// <summary>Deletes a repository configuration by ID.</summary>
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
}
