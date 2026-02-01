using CSharpFunctionalExtensions;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.Services.CodeAnalysis;

#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable CA1056 // Uri properties should not be strings

/// <summary>
///     Repository interface for CodeAnalysisRequest persistence
/// </summary>
public interface ICodeAnalysisRepository
{
    // Query Operations
    Task<Result<CodeAnalysisRequest>> GetByIdAsync(
        Guid requestId,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<CodeAnalysisRequest>>> GetPendingAsync(
        int batchSize,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<CodeAnalysisRequest>>> GetByStatusAsync(
        AnalysisStatus status,
        CancellationToken ct = default);

    Task<Result<IReadOnlyList<CodeAnalysisRequest>>> GetByRepositoryAsync(
        string repositoryUrl,
        CancellationToken ct = default);

    // Mutation Operations
    Task<Result<CodeAnalysisRequest>> CreateAsync(
        CodeAnalysisRequest request,
        CancellationToken ct = default);

    Task<Result> UpdateStatusAsync(
        Guid requestId,
        AnalysisStatus newStatus,
        CancellationToken ct = default);

    Task<Result> UpdateIterationAsync(
        Guid requestId,
        int iteration,
        string prompt,
        string response,
        CancellationToken ct = default);

    Task<Result> RecordValidationAsync(
        Guid requestId,
        string validationResult,
        bool hasFailed,
        CancellationToken ct = default);

    Task<Result> CompleteAsync(
        Guid requestId,
        string? prUrl = null,
        string? finalCommitSha = null,
        CancellationToken ct = default);

    Task<Result> FailAsync(
        Guid requestId,
        string failureReason,
        CancellationToken ct = default);

    Task<Result> CancelAsync(
        Guid requestId,
        CancellationToken ct = default);

    /// <summary>
    ///     Updates the work tree path for a code analysis request.
    ///     Used to store the local path where the repository was cloned.
    /// </summary>
    Task<Result> UpdateWorkTreePathAsync(
        Guid requestId,
        string workTreePath,
        CancellationToken ct = default);

    /// <summary>
    ///     Records the last prompt and response for a code analysis request.
    ///     Used for caching and history.
    /// </summary>
    Task<Result> UpdateLastPromptAsync(
        Guid requestId,
        string lastPrompt,
        string lastResponse = "",
        CancellationToken ct = default);
}
