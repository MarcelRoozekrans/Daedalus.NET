using CSharpFunctionalExtensions;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.Services.CodeAnalysis;

/// <summary>
///     Main orchestrator for Ralph Loop code analysis
/// </summary>
#pragma warning disable CA1054 // Uri parameters should not be strings — URLs passed as strings through the pipeline
public interface IRalphLoopOrchestrator
{
    // Create new analysis request
    Task<Result<Guid>> SubmitAnalysisAsync(
        string repoUrl,
        string filePath,
        AnalysisType type,
        string title,
        string description,
        IReadOnlyList<string> requirements,
        string? targetBranch = null,
        string? targetCommit = null,
        int maxIterations = 15,
        CancellationToken ct = default);

    // Get next pending request
    Task<Result<CodeAnalysisRequest?>> GetNextPendingAsync(
        CancellationToken ct = default);

    // Initialize repository for analysis
    Task<Result> InitializeRepositoryAsync(
        Guid requestId,
        CancellationToken ct = default);

    // Get analysis prompt
    Task<Result<string>> GetAnalysisPromptAsync(
        Guid requestId,
        CancellationToken ct = default);

    // Process iteration
    Task<Result> ProcessIterationAsync(
        Guid requestId,
        string aiResponse,
        CancellationToken ct = default);

    // Check completion
    Task<Result<bool>> IsCompleteAsync(
        Guid requestId,
        CancellationToken ct = default);

    // Finalize analysis
    Task<Result<string?>> FinalizeAsync(
        Guid requestId,
        bool createPullRequest = true,
        CancellationToken ct = default);

    // Get status
    Task<Result<CodeAnalysisRequest>> GetStatusAsync(
        Guid requestId,
        CancellationToken ct = default);
}
