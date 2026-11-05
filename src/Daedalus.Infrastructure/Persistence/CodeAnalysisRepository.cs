using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Persistence.Orm;
using Daedalus.Infrastructure.Persistence.Orm.CodeAnalysisRequests;
using Microsoft.Extensions.Logging;
using ZeroAlloc.ORM;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Persistence;

/// <summary>
///     Stores code analysis requests and their iterations on ZeroAlloc.ORM, one pooled connection per call.
/// </summary>
/// <remarks>
///     <para>
///         Every write loads the request, applies its domain method and saves, on one connection. The six lifecycle
///         writes, <see cref="UpdateStatusAsync"/>, <see cref="UpdateIterationAsync"/>,
///         <see cref="RecordValidationAsync"/>, <see cref="CompleteAsync"/>, <see cref="FailAsync"/> and
///         <see cref="CancelAsync"/>, are guarded by the <c>xmin</c> the request was read with (#290): a request another
///         write changed since its load is refused with <see cref="ModifiedByAnotherOperation"/> and nothing is
///         written; one deleted since answers the method's logged failure. <see cref="UpdateIterationAsync"/> writes the iteration count and inserts the iteration in one
///         transaction. <see cref="UpdateWorkTreePathAsync"/> and <see cref="UpdateLastPromptAsync"/> are independent:
///         they write only their own columns, by id, with no version check.
///     </para>
///     <para>
///         No save reads the new <c>xmin</c> back into an instance (rulings R58, R70): every write here reads the row
///         itself, so a caller never carries a version from one call into another. A caller holding a request from
///         <see cref="GetByIdAsync"/> reads it again for fresh values after a write.
///     </para>
///     <para>
///         Failure texts carry no exception message; the exception is logged with the request id (ruling R79).
///     </para>
/// </remarks>
public sealed partial class CodeAnalysisRepository(
    DaedalusDb db,
    ILogger<CodeAnalysisRepository> logger) : ICodeAnalysisRepository
{
    /// <summary>The failure of a guarded save that lost a race with another write to the request.</summary>
    public const string ModifiedByAnotherOperation =
        "The analysis request was modified by another operation. Please retry.";

    public async Task<Result<CodeAnalysisRequest>> GetByIdAsync(
        Guid requestId,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var request = await new CodeAnalysisSql(connection).FindWithIterationsAsync(requestId, ct)
                .ConfigureAwait(false);

            return request is not null
                ? Result<CodeAnalysisRequest>.Success(request)
                : Result<CodeAnalysisRequest>.Failure($"Analysis request {requestId} not found");
        }
        catch (Exception ex)
        {
            LogErrorRetrievingAnalysisRequest(logger, ex, requestId);
            return Result<CodeAnalysisRequest>.Failure(
                $"Error retrieving analysis request {requestId}. The cause is logged.");
        }
    }

    public async Task<Result<IReadOnlyList<CodeAnalysisRequest>>> GetPendingAsync(
        int batchSize,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var requests = await new CodeAnalysisSql(connection).ListPendingAsync(batchSize, ct).ConfigureAwait(false);
            return Result<IReadOnlyList<CodeAnalysisRequest>>.Success(requests);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingPendingRequests(logger, ex);
            return Result<IReadOnlyList<CodeAnalysisRequest>>.Failure(
                "Error retrieving pending requests. The cause is logged.");
        }
    }

    public async Task<Result<IReadOnlyList<CodeAnalysisRequest>>> GetByStatusAsync(
        AnalysisStatus status,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var requests = await new CodeAnalysisSql(connection).ListByStatusAsync(status, ct).ConfigureAwait(false);
            return Result<IReadOnlyList<CodeAnalysisRequest>>.Success(requests);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingRequestsByStatus(logger, ex, status);
            return Result<IReadOnlyList<CodeAnalysisRequest>>.Failure(
                "Error retrieving requests. The cause is logged.");
        }
    }

    public async Task<Result<IReadOnlyList<CodeAnalysisRequest>>> GetByRepositoryAsync(
        string repositoryUrl,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var requests = await new CodeAnalysisSql(connection).ListByRepositoryAsync(repositoryUrl, ct)
                .ConfigureAwait(false);
            return Result<IReadOnlyList<CodeAnalysisRequest>>.Success(requests);
        }
        catch (Exception ex)
        {
            LogErrorRetrievingRequestsByRepository(logger, ex, repositoryUrl);
            return Result<IReadOnlyList<CodeAnalysisRequest>>.Failure(
                "Error retrieving requests. The cause is logged.");
        }
    }

    public async Task<Result<CodeAnalysisRequest>> CreateAsync(
        CodeAnalysisRequest request,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            await new CodeAnalysisSql(connection).InsertAsync(request, ct).ConfigureAwait(false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Created code analysis request {RequestId} for repository {RepositoryUrl}",
                    request.Id,
                    request.Repository.Url);
            }

            return Result<CodeAnalysisRequest>.Success(request);
        }
        catch (Exception ex)
        {
            LogErrorCreatingRequest(logger, ex, request.Id);
            return Result<CodeAnalysisRequest>.Failure(
                $"Error creating analysis request {request.Id}. The cause is logged.");
        }
    }

    /// <summary>
    ///     Starts or cancels the request, the two transitions this method supports, under the <c>xmin</c> guard; a lost
    ///     race answers <see cref="ModifiedByAnotherOperation"/>.
    /// </summary>
    public async Task<Result> UpdateStatusAsync(
        Guid requestId,
        AnalysisStatus newStatus,
        CancellationToken ct = default)
    {
        var result = await SaveGuardedAsync(
            requestId,
            request => newStatus switch
            {
                AnalysisStatus.AnalysisInProgress => request.StartAnalysis(),
                AnalysisStatus.Cancelled => request.Cancel(),
                _ => Result.Failure($"Status transition to {newStatus} not supported via this method")
            },
            static (sql, request, ct) => sql.SaveLifecycleAsync(request, ct),
            LogErrorUpdatingRequestStatus,
            $"Error updating status of analysis request {requestId}. The cause is logged.",
            ct).ConfigureAwait(false);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Updated analysis request {RequestId} status to {Status}",
                requestId,
                newStatus);
        }

        return result;
    }

    /// <summary>
    ///     Records an iteration: the iteration count, under the <c>xmin</c> guard, and the iteration's row, in one
    ///     transaction; a lost race answers <see cref="ModifiedByAnotherOperation"/> and stores neither.
    /// </summary>
    public async Task<Result> UpdateIterationAsync(
        Guid requestId,
        int iteration,
        string prompt,
        string response,
        CancellationToken ct = default)
    {
        AnalysisIteration? analysisIteration = null;
        var result = await SaveGuardedAsync(
            requestId,
            request =>
            {
                var iterationResult = AnalysisIteration.Create(Guid.NewGuid(), requestId, iteration, prompt, response);
                if (iterationResult.IsFailure)
                {
                    return Result.Failure(iterationResult.Error);
                }

                analysisIteration = iterationResult.Value;
                return request.RecordIteration(analysisIteration);
            },
            (sql, request, ct) => sql.SaveIterationAsync(request, analysisIteration!, ct),
            LogErrorUpdatingRequestIteration,
            $"Error updating iteration of analysis request {requestId}. The cause is logged.",
            ct).ConfigureAwait(false);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Recorded iteration {Iteration} for analysis request {RequestId}",
                iteration,
                requestId);
        }

        return result;
    }

    /// <summary>
    ///     Records validation under the <c>xmin</c> guard; a lost race answers <see cref="ModifiedByAnotherOperation"/>.
    /// </summary>
    public async Task<Result> RecordValidationAsync(
        Guid requestId,
        string validationResult,
        bool hasFailed,
        CancellationToken ct = default)
    {
        var result = await SaveGuardedAsync(
            requestId,
            request => request.RecordValidation(validationResult, hasFailed),
            static (sql, request, ct) => sql.SaveLifecycleAsync(request, ct),
            LogErrorRecordingValidation,
            $"Error recording validation of analysis request {requestId}. The cause is logged.",
            ct).ConfigureAwait(false);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Recorded validation for analysis request {RequestId}. Failed: {HasFailed}",
                requestId,
                hasFailed);
        }

        return result;
    }

    /// <summary>
    ///     Completes the request under the <c>xmin</c> guard; a lost race answers <see cref="ModifiedByAnotherOperation"/>.
    /// </summary>
    public async Task<Result> CompleteAsync(
        Guid requestId,
        string? prUrl = null,
        string? finalCommitSha = null,
        CancellationToken ct = default)
    {
        var result = await SaveGuardedAsync(
            requestId,
            request => request.Complete(prUrl, finalCommitSha),
            static (sql, request, ct) => sql.SaveLifecycleAsync(request, ct),
            LogErrorCompletingRequest,
            $"Error completing analysis request {requestId}. The cause is logged.",
            ct).ConfigureAwait(false);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Completed analysis request {RequestId}. PR: {PrUrl}",
                requestId,
                prUrl);
        }

        return result;
    }

    /// <summary>
    ///     Fails the request under the <c>xmin</c> guard; a lost race answers <see cref="ModifiedByAnotherOperation"/>.
    /// </summary>
    public async Task<Result> FailAsync(
        Guid requestId,
        string failureReason,
        CancellationToken ct = default)
    {
        var result = await SaveGuardedAsync(
            requestId,
            request => request.Fail(failureReason),
            static (sql, request, ct) => sql.SaveLifecycleAsync(request, ct),
            LogErrorFailingRequest,
            $"Error failing analysis request {requestId}. The cause is logged.",
            ct).ConfigureAwait(false);

        if (result.IsSuccess)
        {
            logger.LogError("Analysis request {RequestId} failed: {Reason}", requestId, failureReason);
        }

        return result;
    }

    /// <summary>
    ///     Cancels the request under the <c>xmin</c> guard; a lost race answers <see cref="ModifiedByAnotherOperation"/>.
    /// </summary>
    public async Task<Result> CancelAsync(
        Guid requestId,
        CancellationToken ct = default)
    {
        var result = await SaveGuardedAsync(
            requestId,
            static request => request.Cancel(),
            static (sql, request, ct) => sql.SaveLifecycleAsync(request, ct),
            LogErrorCancellingRequest,
            $"Error cancelling analysis request {requestId}. The cause is logged.",
            ct).ConfigureAwait(false);

        if (result.IsSuccess && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Cancelled analysis request {RequestId}", requestId);
        }

        return result;
    }

    /// <summary>
    ///     Writes the work tree path alone, by id, with no version check: it never fails on a concurrent write of the
    ///     request's other columns.
    /// </summary>
    public async Task<Result> UpdateWorkTreePathAsync(
        Guid requestId,
        string workTreePath,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var sql = new CodeAnalysisSql(connection);
            var request = await sql.FindAsync(requestId, ct).ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            var setResult = request.SetWorkTreePath(workTreePath);
            if (setResult.IsFailure)
            {
                return setResult;
            }

            await sql.SetWorkTreePathAsync(requestId, request.LocalWorkTreePath, ct).ConfigureAwait(false);

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
            return Result.Failure(
                $"Error updating work tree path of analysis request {requestId}. The cause is logged.");
        }
    }

    /// <summary>
    ///     Writes the last prompt and response alone, by id, with no version check: it never fails on a concurrent write
    ///     of the request's other columns.
    /// </summary>
    public async Task<Result> UpdateLastPromptAsync(
        Guid requestId,
        string lastPrompt,
        string lastResponse = "",
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var sql = new CodeAnalysisSql(connection);
            var request = await sql.FindAsync(requestId, ct).ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            var recordResult = request.RecordPromptAndResponse(lastPrompt, lastResponse ?? "");
            if (recordResult.IsFailure)
            {
                return recordResult;
            }

            await sql.SetLastPromptAsync(requestId, request.LastPromptSent, request.LastAiResponse, ct)
                .ConfigureAwait(false);

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
            return Result.Failure(
                $"Error updating last prompt of analysis request {requestId}. The cause is logged.");
        }
    }

    /// <summary>
    ///     Loads the request without its iterations, applies <paramref name="change"/> and runs <paramref name="save"/>,
    ///     which is guarded by the <c>xmin</c> the request was read with, on one connection. A request not stored answers
    ///     not found, as before; a refused change answers its own failure. A save that matches no row answers
    ///     <see cref="ModifiedByAnotherOperation"/> while the row still exists, and <paramref name="failure"/>, logged,
    ///     when it was deleted since the load (ruling R68), as does any other error.
    /// </summary>
    private async Task<Result> SaveGuardedAsync(
        Guid requestId,
        Func<CodeAnalysisRequest, Result> change,
        Func<CodeAnalysisSql, CodeAnalysisRequest, CancellationToken, Task> save,
        Action<ILogger, Exception, Guid> logError,
        string failure,
        CancellationToken ct)
    {
        try
        {
            await using var connection = await db.OpenAsync(ct).ConfigureAwait(false);
            var sql = new CodeAnalysisSql(connection);
            var request = await sql.FindAsync(requestId, ct).ConfigureAwait(false);

            if (request is null)
            {
                return Result.Failure($"Analysis request {requestId} not found");
            }

            var changed = change(request);
            if (changed.IsFailure)
            {
                return changed;
            }

            ZeroAllocOrmConcurrencyException conflict;
            try
            {
                await save(sql, request, ct).ConfigureAwait(false);
                return Result.Success();
            }
            catch (ZeroAllocOrmConcurrencyException ex)
            {
                conflict = ex;
            }

            if (await sql.ExistsAsync(requestId, ct).ConfigureAwait(false))
            {
                return Result.Failure(ModifiedByAnotherOperation);
            }

            logError(logger, conflict, requestId);
            return Result.Failure(failure);
        }
        catch (Exception ex)
        {
            logError(logger, ex, requestId);
            return Result.Failure(failure);
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

    [LoggerMessage(EventId = 34, Level = LogLevel.Error, Message = "Error creating code analysis request {RequestId}")]
    private static partial void LogErrorCreatingRequest(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 35, Level = LogLevel.Error,
        Message = "Error updating status of analysis request {RequestId}")]
    private static partial void LogErrorUpdatingRequestStatus(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 36, Level = LogLevel.Error,
        Message = "Error updating iteration of analysis request {RequestId}")]
    private static partial void LogErrorUpdatingRequestIteration(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 37, Level = LogLevel.Error,
        Message = "Error recording validation of analysis request {RequestId}")]
    private static partial void LogErrorRecordingValidation(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 38, Level = LogLevel.Error, Message = "Error completing analysis request {RequestId}")]
    private static partial void LogErrorCompletingRequest(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 39, Level = LogLevel.Error,
        Message = "Error marking analysis request {RequestId} as failed")]
    private static partial void LogErrorFailingRequest(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 40, Level = LogLevel.Error, Message = "Error cancelling analysis request {RequestId}")]
    private static partial void LogErrorCancellingRequest(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 41, Level = LogLevel.Error,
        Message = "Error updating work tree path for request {RequestId}")]
    private static partial void LogErrorUpdatingWorkTreePath(ILogger logger, Exception exception, Guid requestId);

    [LoggerMessage(EventId = 42, Level = LogLevel.Error,
        Message = "Error updating last prompt for request {RequestId}")]
    private static partial void LogErrorUpdatingLastPrompt(ILogger logger, Exception exception, Guid requestId);
}
