using Daedalus.Domain.Entities;
using ZeroAlloc.Results;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Repository for RepositoryConfiguration persistence and querying.
/// </summary>
public interface IRepositoryConfigurationRepository
{
    /// <summary>Gets all repository configurations.</summary>
    Task<Result<IReadOnlyList<RepositoryConfiguration>>> GetAllAsync(CancellationToken ct = default);

    /// <summary>Gets a repository configuration by ID.</summary>
    Task<Result<RepositoryConfiguration>> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Adds a new repository configuration.</summary>
    Task<Result<RepositoryConfiguration>> AddAsync(RepositoryConfiguration repository, CancellationToken ct = default);

    /// <summary>
    ///     Overwrites the stored configuration with the one given, the columns its update changes, with no version
    ///     check. Fails with "modified by another operation" when the row no longer exists.
    /// </summary>
    Task<Result> UpdateAsync(RepositoryConfiguration repository, CancellationToken ct = default);

    /// <summary>Deletes a repository configuration by ID.</summary>
    Task<Result> DeleteAsync(Guid id, CancellationToken ct = default);
}
