using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     EF Core repository implementation for CodeAnalysisRequest
/// </summary>
public sealed partial class CodeAnalysisRepository(
    ApplicationDbContext dbContext,
    ILogger<CodeAnalysisRepository> logger) : ICodeAnalysisRepository
{
    public async Task<Result<CodeAnalysisRequest>> GetByIdAsync(
        Guid requestId,
        CancellationToken ct = default)
    {
        try
        {
            var request = await dbContext.CodeAnalysisRequests
                .AsNoTracking()
                .Include(r => r.Iterations)
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                .ConfigureAwait(false);

            return request is not null
                ? Result.Success(request)
                : Result.Failure<CodeAnalysisRequest>($"Analysis request {requestId} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingAnalysisRequest(logger, ex, requestId);
            return Result.Failure<CodeAnalysisRequest>($"Error retrieving analysis request: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<CodeAnalysisRequest>>> GetPendingAsync(
        int batchSize,
        CancellationToken ct = default)
    {
        try
        {
            var requests = await dbContext.CodeAnalysisRequests
                .AsNoTracking()
                .Where(r => r.Status == AnalysisStatus.Pending || r.Status == AnalysisStatus.Ready)
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.CreatedAt)
                .Take(batchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success((IReadOnlyList<CodeAnalysisRequest>)requests);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingPendingRequests(logger, ex);
            return Result.Failure<IReadOnlyList<CodeAnalysisRequest>>(
                $"Error retrieving pending requests: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<CodeAnalysisRequest>>> GetByStatusAsync(
        AnalysisStatus status,
        CancellationToken ct = default)
    {
        try
        {
            var requests = await dbContext.CodeAnalysisRequests
                .AsNoTracking()
                .Where(r => r.Status == status)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success((IReadOnlyList<CodeAnalysisRequest>)requests);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingRequestsByStatus(logger, ex, status);
            return Result.Failure<IReadOnlyList<CodeAnalysisRequest>>($"Error retrieving requests: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<CodeAnalysisRequest>>> GetByRepositoryAsync(
        string repositoryUrl,
        CancellationToken ct = default)
    {
        try
        {
            var requests = await dbContext.CodeAnalysisRequests
                .AsNoTracking()
                .Where(r => r.Repository.Url == repositoryUrl)
                .OrderByDescending(r => r.CreatedAt)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            return Result.Success((IReadOnlyList<CodeAnalysisRequest>)requests);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingRequestsByRepository(logger, ex, repositoryUrl);
            return Result.Failure<IReadOnlyList<CodeAnalysisRequest>>($"Error retrieving requests: {ex.Message}");
        }
    }

    public async Task<Result<CodeAnalysisRequest>> CreateAsync(
        CodeAnalysisRequest request,
        CancellationToken ct = default)
    {
        try
        {
            // Request should already be created by factory method
            // Just persist to database
            dbContext.CodeAnalysisRequests.Add(request);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Created code analysis request {RequestId} for repository {RepositoryUrl}",
                    request.Id,
                    request.Repository.Url);
            }

            return Result.Success(request);
        }
        catch (Exception ex)
        {
            LogErrorCreatingRequest(logger, ex);
            return Result.Failure<CodeAnalysisRequest>($"Error creating analysis request: {ex.Message}");
        }
    }

    public async Task<Result> UpdateStatusAsync(
        Guid requestId,
        AnalysisStatus newStatus,
        CancellationToken ct = default)
    {
        try
        {
            var request = await dbContext.CodeAnalysisRequests
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            // Call appropriate domain method based on new status
            var updateResult = newStatus switch
            {
                AnalysisStatus.AnalysisInProgress => request.StartAnalysis(),
                AnalysisStatus.Cancelled => request.Cancel(),
                _ => Result.Failure($"Status transition to {newStatus} not supported via this method")
            };

            if (updateResult.IsFailure)
            {
                return updateResult;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Updated analysis request {RequestId} status to {Status}",
                    requestId,
                    newStatus);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorUpdatingRequestStatus(logger, ex);
            return Result.Failure($"Error updating status: {ex.Message}");
        }
    }

    public async Task<Result> UpdateIterationAsync(
        Guid requestId,
        int iteration,
        string prompt,
        string response,
        CancellationToken ct = default)
    {
        try
        {
            var request = await dbContext.CodeAnalysisRequests
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            // Create analysis iteration using factory method
            var iterationResult = AnalysisIteration.Create(
                Guid.NewGuid(),
                requestId,
                iteration,
                prompt,
                response);

            if (iterationResult.IsFailure)
            {
                return Result.Failure(iterationResult.Error);
            }

            var analysisIteration = iterationResult.Value;

            // Record iteration using domain method
            var recordResult = request.RecordIteration(analysisIteration);
            if (recordResult.IsFailure)
            {
                return recordResult;
            }

            dbContext.AnalysisIterations.Add(analysisIteration);
            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Recorded iteration {Iteration} for analysis request {RequestId}",
                    iteration,
                    requestId);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorUpdatingRequestIteration(logger, ex);
            return Result.Failure($"Error updating iteration: {ex.Message}");
        }
    }

    public async Task<Result> RecordValidationAsync(
        Guid requestId,
        string validationResult,
        bool hasFailed,
        CancellationToken ct = default)
    {
        try
        {
            var request = await dbContext.CodeAnalysisRequests
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            // Record validation using domain method
            var recordResult = request.RecordValidation(validationResult, hasFailed);
            if (recordResult.IsFailure)
            {
                return recordResult;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Recorded validation for analysis request {RequestId}. Failed: {HasFailed}",
                    requestId,
                    hasFailed);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorRecordingValidation(logger, ex);
            return Result.Failure($"Error recording validation: {ex.Message}");
        }
    }

    public async Task<Result> CompleteAsync(
        Guid requestId,
        string? prUrl = null,
        string? finalCommitSha = null,
        CancellationToken ct = default)
    {
        try
        {
            var request = await dbContext.CodeAnalysisRequests
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            // Complete using domain method
            var completeResult = request.Complete(prUrl, finalCommitSha);
            if (completeResult.IsFailure)
            {
                return completeResult;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Completed analysis request {RequestId}. PR: {PrUrl}",
                    requestId,
                    prUrl);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorCompletingRequest(logger, ex);
            return Result.Failure($"Error completing request: {ex.Message}");
        }
    }

    public async Task<Result> FailAsync(
        Guid requestId,
        string failureReason,
        CancellationToken ct = default)
    {
        try
        {
            var request = await dbContext.CodeAnalysisRequests
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            // Fail using domain method
            var failResult = request.Fail(failureReason);
            if (failResult.IsFailure)
            {
                return failResult;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            logger.LogError("Analysis request {RequestId} failed: {Reason}", requestId, failureReason);

            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorFailingRequest(logger, ex);
            return Result.Failure($"Error failing request: {ex.Message}");
        }
    }

    public async Task<Result> CancelAsync(
        Guid requestId,
        CancellationToken ct = default)
    {
        try
        {
            var request = await dbContext.CodeAnalysisRequests
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            // Cancel using domain method
            var cancelResult = request.Cancel();
            if (cancelResult.IsFailure)
            {
                return cancelResult;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Cancelled analysis request {RequestId}", requestId);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorCancellingRequest(logger, ex);
            return Result.Failure($"Error cancelling request: {ex.Message}");
        }
    }

    public async Task<Result> UpdateWorkTreePathAsync(
        Guid requestId,
        string workTreePath,
        CancellationToken ct = default)
    {
        try
        {
            var request = await dbContext.CodeAnalysisRequests
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            // Set work tree path using domain method
            var setResult = request.SetWorkTreePath(workTreePath);
            if (setResult.IsFailure)
            {
                return setResult;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Updated work tree path for request {RequestId}: {Path}",
                    requestId,
                    workTreePath);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorUpdatingWorkTreePath(logger, ex, requestId);
            return Result.Failure($"Error updating work tree path: {ex.Message}");
        }
    }

    public async Task<Result> UpdateLastPromptAsync(
        Guid requestId,
        string lastPrompt,
        string lastResponse = "",
        CancellationToken ct = default)
    {
        try
        {
            var request = await dbContext.CodeAnalysisRequests
                .FirstOrDefaultAsync(r => r.Id == requestId, ct)
                .ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            // Record prompt and response using domain method
            var recordResult = request.RecordPromptAndResponse(lastPrompt, lastResponse ?? "");
            if (recordResult.IsFailure)
            {
                return recordResult;
            }

            await dbContext.SaveChangesAsync(ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Updated last prompt for request {RequestId}",
                    requestId);
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            LogErrorUpdatingLastPrompt(logger, ex, requestId);
            return Result.Failure($"Error updating last prompt: {ex.Message}");
        }
    }

    [LoggerMessage(EventId = 30, Level = LogLevel.Error, Message = "Error retrieving analysis request {RequestId}")]
    private static partial void LogErrorRetrievingAnalysisRequest(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 31, Level = LogLevel.Error, Message = "Error retrieving pending analysis requests")]
    private static partial void LogErrorRetrievingPendingRequests(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 32, Level = LogLevel.Error,
        Message = "Error retrieving analysis requests by status {Status}")]
    private static partial void LogErrorRetrievingRequestsByStatus(ILogger logger, Exception exception,
        AnalysisStatus status);

    [LoggerMessage(EventId = 33, Level = LogLevel.Error,
        Message = "Error retrieving analysis requests for repository {RepositoryUrl}")]
    private static partial void LogErrorRetrievingRequestsByRepository(ILogger logger, Exception exception,
        string repositoryUrl);

    [LoggerMessage(EventId = 34, Level = LogLevel.Error, Message = "Error creating code analysis request")]
    private static partial void LogErrorCreatingRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 35, Level = LogLevel.Error, Message = "Error updating analysis request status")]
    private static partial void LogErrorUpdatingRequestStatus(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 36, Level = LogLevel.Error, Message = "Error updating analysis request iteration")]
    private static partial void LogErrorUpdatingRequestIteration(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 37, Level = LogLevel.Error, Message = "Error recording validation")]
    private static partial void LogErrorRecordingValidation(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 38, Level = LogLevel.Error, Message = "Error completing analysis request")]
    private static partial void LogErrorCompletingRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 39, Level = LogLevel.Error, Message = "Error marking analysis request as failed")]
    private static partial void LogErrorFailingRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 40, Level = LogLevel.Error, Message = "Error cancelling analysis request")]
    private static partial void LogErrorCancellingRequest(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 41, Level = LogLevel.Error,
        Message = "Error updating work tree path for request {RequestId}")]
    private static partial void LogErrorUpdatingWorkTreePath(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 42, Level = LogLevel.Error,
        Message = "Error updating last prompt for request {RequestId}")]
    private static partial void LogErrorUpdatingLastPrompt(ILogger logger, Exception exception, Guid requestId);
}
