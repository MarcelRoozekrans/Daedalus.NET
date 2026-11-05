using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.RepositoryConfigurations;
using Microsoft.Extensions.Logging;
using ZeroAlloc.ORM;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Persistence.Repositories;

/// <summary>
///     Stores repository configurations on ZeroAlloc.ORM, one pooled connection per call.
/// </summary>
/// <remarks>
///     <see cref="UpdateAsync"/> is an independent full overwrite of the columns the request carries, with no version
///     check (owner decision 5): the second of two saves wins. It matches the row by id only, so a configuration
///     deleted since it was read answers <see cref="ModifiedByAnotherOperation"/>, as EF's update matching no row did.
/// </remarks>
public sealed partial class RepositoryConfigurationRepository(DaedalusDb db, ILogger<RepositoryConfigurationRepository> logger)
    : IRepositoryConfigurationRepository
{
    /// <summary>The failure of an update that matched no row, a configuration deleted since it was read.</summary>
    public const string ModifiedByAnotherOperation = "The repository was modified by another operation. Please retry.";

    public async Task<Result<IReadOnlyList<RepositoryConfiguration>>> GetAllAsync(
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var repos = await new RepositoryConfigurationSql(connection).ListAsync(ct).ConfigureAwait(false);
            return Result<IReadOnlyList<RepositoryConfiguration>>.Success(repos);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingRepositories(logger, ex);
            return Result<IReadOnlyList<RepositoryConfiguration>>.Failure(
                "Error retrieving repositories. The cause is logged.");
        }
    }

    public async Task<Result<RepositoryConfiguration>> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var repo = await new RepositoryConfigurationSql(connection).FindAsync(id, ct).ConfigureAwait(false);

            return repo is not null
                ? Result<RepositoryConfiguration>.Success(repo)
                : Result<RepositoryConfiguration>.Failure(
                    $"Repository configuration {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingRepository(logger, ex, id);
            return Result<RepositoryConfiguration>.Failure(
                "Error retrieving repository. The cause is logged.");
        }
    }

    public async Task<Result<RepositoryConfiguration>> AddAsync(
        RepositoryConfiguration repository, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            await new RepositoryConfigurationSql(connection).InsertAsync(repository, ct).ConfigureAwait(false);
            return Result<RepositoryConfiguration>.Success(repository);
        }
        catch (Exception ex)
        {
            LogErrorAddingRepository(logger, ex, repository.Id);
            return Result<RepositoryConfiguration>.Failure(
                "Error adding repository. The cause is logged.");
        }
    }

    public async Task<Result> UpdateAsync(
        RepositoryConfiguration repository, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            await new RepositoryConfigurationSql(connection).UpdateAsync(repository, ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (ZeroAllocOrmConcurrencyException)
        {
            return Result.Failure(ModifiedByAnotherOperation);
        }
        catch (Exception ex)
        {
            LogErrorUpdatingRepository(logger, ex, repository.Id);
            return Result.Failure("Error updating repository. The cause is logged.");
        }
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var deleted = await new RepositoryConfigurationSql(connection).DeleteAsync(id, ct).ConfigureAwait(false);
            return deleted > 0
                ? Result.Success()
                : Result.Failure($"Repository configuration {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorDeletingRepository(logger, ex, id);
            return Result.Failure("Error deleting repository. The cause is logged.");
        }
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Error,
        Message = "Error retrieving repository configurations")]
    private static partial void LogErrorRetrievingRepositories(
        ILogger logger, Exception ex);

    [LoggerMessage(EventId = 301, Level = LogLevel.Error,
        Message = "Error retrieving repository {RepositoryId}")]
    private static partial void LogErrorRetrievingRepository(
        ILogger logger, Exception ex, Guid repositoryId);

    [LoggerMessage(EventId = 302, Level = LogLevel.Error,
        Message = "Error adding repository {RepositoryId}")]
    private static partial void LogErrorAddingRepository(
        ILogger logger, Exception ex, Guid repositoryId);

    [LoggerMessage(EventId = 303, Level = LogLevel.Error,
        Message = "Error updating repository {RepositoryId}")]
    private static partial void LogErrorUpdatingRepository(
        ILogger logger, Exception ex, Guid repositoryId);

    [LoggerMessage(EventId = 304, Level = LogLevel.Error,
        Message = "Error deleting repository {RepositoryId}")]
    private static partial void LogErrorDeletingRepository(
        ILogger logger, Exception ex, Guid repositoryId);
}
