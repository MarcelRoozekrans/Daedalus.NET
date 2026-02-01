using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     EF Core implementation of the learnings repository.
///     Uses simple SQL text search (ILIKE for PostgreSQL) — no vectors, no embeddings.
///     Aligned with Ralph philosophy: simple persistence beats complex retrieval.
/// </summary>
public sealed partial class LearningsRepository(
    ApplicationDbContext dbContext,
    ILogger<LearningsRepository> logger) : ILearningsRepository
{
    public async Task<Result<StructuredLearningEntry>> AddAsync(
        StructuredLearningEntry entry, CancellationToken ct)
    {
        try
        {
            dbContext.StructuredLearnings.Add(entry);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            LogLearningAdded(logger, entry.Id, entry.Category);
            return Result.Success(entry);
        }
        catch (DbUpdateException ex)
        {
            LogFailedAddLearning(logger, ex, entry.Pattern);
            return Result.Failure<StructuredLearningEntry>($"Failed to add learning: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<StructuredLearningEntry>>> GetByCategoryAsync(
        LearningCategory category, int maxResults, CancellationToken ct)
    {
        try
        {
            var results = await dbContext.StructuredLearnings
                .AsNoTracking()
                .Where(l => l.Category == category)
                .OrderByDescending(l => l.Severity)
                .ThenByDescending(l => l.CreatedAt)
                .Take(maxResults)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success<IReadOnlyList<StructuredLearningEntry>>(results);
        }
        catch (Exception ex)
        {
            LogSearchFailed(logger, ex, "category", category.ToString());
            return Result.Failure<IReadOnlyList<StructuredLearningEntry>>(
                $"Failed to search by category: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<StructuredLearningEntry>>> SearchByKeywordAsync(
        string keyword, int maxResults, CancellationToken ct)
    {
        try
        {
            // Use EF.Functions.ILike for PostgreSQL case-insensitive search
            var results = await dbContext.StructuredLearnings
                .AsNoTracking()
                .Where(l =>
                    EF.Functions.ILike(l.Pattern, $"%{keyword}%") ||
                    EF.Functions.ILike(l.Resolution, $"%{keyword}%"))
                .OrderByDescending(l => l.Severity)
                .ThenByDescending(l => l.HitCount)
                .Take(maxResults)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success<IReadOnlyList<StructuredLearningEntry>>(results);
        }
        catch (Exception ex)
        {
            LogSearchFailed(logger, ex, "keyword", keyword);
            return Result.Failure<IReadOnlyList<StructuredLearningEntry>>(
                $"Failed to search by keyword: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<StructuredLearningEntry>>> GetByProjectIdAsync(
        Guid projectId, int maxResults, CancellationToken ct)
    {
        try
        {
            var results = await dbContext.StructuredLearnings
                .AsNoTracking()
                .Where(l => l.ProjectId == projectId)
                .OrderByDescending(l => l.Severity)
                .ThenByDescending(l => l.HitCount)
                .ThenByDescending(l => l.CreatedAt)
                .Take(maxResults)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success<IReadOnlyList<StructuredLearningEntry>>(results);
        }
        catch (Exception ex)
        {
            LogSearchFailed(logger, ex, "project", projectId.ToString());
            return Result.Failure<IReadOnlyList<StructuredLearningEntry>>(
                $"Failed to search by project: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<StructuredLearningEntry>>> GetTopLearningsAsync(
        int maxResults, CancellationToken ct)
    {
        try
        {
            var results = await dbContext.StructuredLearnings
                .AsNoTracking()
                .OrderByDescending(l => l.Severity)
                .ThenByDescending(l => l.HitCount)
                .ThenByDescending(l => l.CreatedAt)
                .Take(maxResults)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success<IReadOnlyList<StructuredLearningEntry>>(results);
        }
        catch (Exception ex)
        {
            LogSearchFailed(logger, ex, "top", "global");
            return Result.Failure<IReadOnlyList<StructuredLearningEntry>>(
                $"Failed to get top learnings: {ex.Message}");
        }
    }

    public async Task<Result> UpdateAsync(StructuredLearningEntry entry, CancellationToken ct)
    {
        try
        {
            dbContext.StructuredLearnings.Update(entry);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Success();
        }
        catch (DbUpdateException ex)
        {
            LogFailedUpdateLearning(logger, ex, entry.Id);
            return Result.Failure($"Failed to update learning: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<StructuredLearningEntry>>> SearchByTagsAsync(
        IEnumerable<string> tags, int maxResults, CancellationToken ct)
    {
        try
        {
            var tagList = tags
                .AsValueEnumerable()
                .Select(t => t.ToUpperInvariant())
                .ToArray();

            // PostgreSQL array overlap operator via raw SQL for tag matching
            var results = await dbContext.StructuredLearnings
                .AsNoTracking()
                .Where(l => tagList.Any(t =>
                    EF.Functions.ILike(l.Pattern, $"%{t}%") ||
                    EF.Functions.ILike(l.Resolution, $"%{t}%")))
                .OrderByDescending(l => l.Severity)
                .ThenByDescending(l => l.HitCount)
                .Take(maxResults)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success<IReadOnlyList<StructuredLearningEntry>>(results);
        }
        catch (Exception ex)
        {
            LogSearchFailed(logger, ex, "tags", string.Join(',', tags));
            return Result.Failure<IReadOnlyList<StructuredLearningEntry>>(
                $"Failed to search by tags: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Debug,
        Message = "Structured learning added: {Id} ({Category})")]
    private static partial void LogLearningAdded(ILogger logger, Guid id, LearningCategory category);

    [LoggerMessage(EventId = 101, Level = LogLevel.Error,
        Message = "Failed to add structured learning entry: {Pattern}")]
    private static partial void LogFailedAddLearning(ILogger logger, Exception exception, string pattern);

    [LoggerMessage(EventId = 102, Level = LogLevel.Error,
        Message = "Failed to search learnings by {SearchType}: {SearchValue}")]
    private static partial void LogSearchFailed(ILogger logger, Exception exception, string searchType,
        string searchValue);

    [LoggerMessage(EventId = 103, Level = LogLevel.Warning,
        Message = "Failed to update structured learning entry: {Id}")]
    private static partial void LogFailedUpdateLearning(ILogger logger, Exception exception, Guid id);
}
