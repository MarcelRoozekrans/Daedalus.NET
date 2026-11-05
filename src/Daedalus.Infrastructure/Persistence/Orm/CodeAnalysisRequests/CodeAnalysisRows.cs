using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Infrastructure.Persistence.Orm.CodeAnalysisRequests;

/// <summary>
///     One <c>"CodeAnalysisRequests"</c> row, every live column, the owned ones flat, and its <c>xmin</c>, as
///     <see cref="CodeAnalysisSql"/> reads it.
/// </summary>
internal sealed record CodeAnalysisRequestRow(
    Guid Id,
    string Title,
    string Description,
    string RepositoryUrl,
    string? RepositoryBranch,
    string? CommitSha,
    RepositoryPlatform Platform,
    string FilePath,
    int? StartLine,
    int? EndLine,
    AnalysisType Type,
    int Priority,
    string Requirements,
    string? ReferencedDocuments,
    string? ExternalReferences,
    string? LocalWorkTreePath,
    string? FeatureBranchName,
    AnalysisStatus Status,
    int CurrentIteration,
    int MaxIterations,
    string? CompletionPromise,
    string? LastPromptSent,
    string? LastAiResponse,
    string? PullRequestUrl,
    string? CommitShaFinal,
    string? ValidationResult,
    bool HasFailedValidation,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime? CancelledAt,
    string? FailureReason,
    Guid? CreatedBy,
    Guid? AssignedTo,
    uint RowVersion)
{
    /// <summary>
    ///     The request the row holds, as stored: <see cref="CodeAnalysisRequest.Rehydrate"/> and the value objects'
    ///     <c>Rehydrate</c> validate nothing, and the stored platform is kept.
    /// </summary>
    /// <param name="iterations">The request's iterations, empty for a read that loads none.</param>
    /// <returns>The domain request.</returns>
    public CodeAnalysisRequest ToDomain(IEnumerable<AnalysisIteration> iterations) =>
        CodeAnalysisRequest.Rehydrate(
            Id, Title, Description,
            RepositoryReference.Rehydrate(RepositoryUrl, RepositoryBranch, CommitSha, Platform),
            CodeLocation.Rehydrate(FilePath, StartLine, EndLine),
            Type, Priority, Requirements, ReferencedDocuments, ExternalReferences, LocalWorkTreePath, FeatureBranchName,
            Status, CurrentIteration, MaxIterations, CompletionPromise, LastPromptSent, LastAiResponse,
            AnalysisOutcome.Rehydrate(PullRequestUrl, CommitShaFinal, ValidationResult, HasFailedValidation),
            CreatedAt, StartedAt, CompletedAt, CancelledAt, FailureReason, CreatedBy, AssignedTo, RowVersion, iterations);
}

/// <summary>One <c>"AnalysisIterations"</c> row, every column, as <see cref="CodeAnalysisSql"/> reads it.</summary>
internal sealed record AnalysisIterationRow(
    Guid Id,
    Guid CodeAnalysisRequestId,
    int IterationNumber,
    string? BranchName,
    string? CommitSha,
    string PromptSent,
    string AiResponse,
    bool? CompilationSucceeded,
    bool? TestsPassed,
    string? ValidationErrors,
    int InputTokens,
    int OutputTokens,
    string? ModelId,
    DateTime CreatedAt)
{
    /// <summary>The iteration the row holds, as stored: <see cref="AnalysisIteration.Rehydrate"/> validates nothing.</summary>
    /// <returns>The domain iteration.</returns>
    public AnalysisIteration ToDomain() =>
        AnalysisIteration.Rehydrate(
            Id, CodeAnalysisRequestId, IterationNumber, BranchName, CommitSha, PromptSent, AiResponse,
            CompilationSucceeded, TestsPassed, ValidationErrors, InputTokens, OutputTokens, ModelId, CreatedAt);
}
