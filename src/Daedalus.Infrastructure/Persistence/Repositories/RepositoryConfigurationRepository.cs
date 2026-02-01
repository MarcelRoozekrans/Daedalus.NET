using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Persistence.Repositories;

/// <summary>
///     EF Core repository implementation for RepositoryConfiguration.
/// </summary>
public sealed partial class RepositoryConfigurationRepository(
    ApplicationDbContext dbContext,
    ILogger<RepositoryConfigurationRepository> logger) : IRepositoryConfigurationRepository
{
    public async Task<Result<IReadOnlyList<RepositoryConfiguration>>> GetAllAsync(
        CancellationToken ct = default)
    {
        try
        {
            var repos = await dbContext.RepositoryConfigurations
                .AsNoTracking()
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success((IReadOnlyList<RepositoryConfiguration>)repos);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingRepositories(logger, ex);
            return Result.Failure<IReadOnlyList<RepositoryConfiguration>>(
                $"Error retrieving repositories: {ex.Message}");
        }
    }

    public async Task<Result<RepositoryConfiguration>> GetByIdAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var repo = await dbContext.RepositoryConfigurations
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id, ct)
                .ConfigureAwait(false);

            return repo is not null
                ? Result.Success(repo)
                : Result.Failure<RepositoryConfiguration>(
                    $"Repository configuration {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingRepository(logger, ex, id);
            return Result.Failure<RepositoryConfiguration>(
                $"Error retrieving repository: {ex.Message}");
        }
    }

    public async Task<Result<RepositoryConfiguration>> GetByIdTrackingAsync(
        Guid id, CancellationToken ct = default)
    {
        try
        {
            var repo = await dbContext.RepositoryConfigurations
                .FirstOrDefaultAsync(r => r.Id == id, ct)
                .ConfigureAwait(false);

            return repo is not null
                ? Result.Success(repo)
                : Result.Failure<RepositoryConfiguration>(
                    $"Repository configuration {id} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingRepository(logger, ex, id);
            return Result.Failure<RepositoryConfiguration>(
                $"Error retrieving repository: {ex.Message}");
        }
    }

    public async Task<Result<RepositoryConfiguration>> AddAsync(
        RepositoryConfiguration repository, CancellationToken ct = default)
    {
        try
        {
            dbContext.RepositoryConfigurations.Add(repository);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success(repository);
        }
        catch (Exception ex)
        {
            LogErrorAddingRepository(logger, ex, repository.Id);
            return Result.Failure<RepositoryConfiguration>(
                $"Error adding repository: {ex.Message}");
        }
    }

    public async Task<Result> UpdateAsync(
        RepositoryConfiguration repository, CancellationToken ct = default)
    {
        try
        {
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result.Failure(
                "The repository was modified by another operation. Please retry.");
        }
        catch (Exception ex)
        {
            LogErrorUpdatingRepository(logger, ex, repository.Id);
            return Result.Failure($"Error updating repository: {ex.Message}");
        }
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        try
        {
            var repo = await dbContext.RepositoryConfigurations
                .FirstOrDefaultAsync(r => r.Id == id, ct)
                .ConfigureAwait(false);

            if (repo is null)
            {
                return Result.Failure($"Repository configuration {id} not found");
            }

            dbContext.RepositoryConfigurations.Remove(repo);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorDeletingRepository(logger, ex, id);
            return Result.Failure($"Error deleting repository: {ex.Message}");
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
