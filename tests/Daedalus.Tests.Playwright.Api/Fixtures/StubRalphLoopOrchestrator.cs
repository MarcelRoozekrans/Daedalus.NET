using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Tests.Playwright.Api;

internal class StubRalphLoopOrchestrator : IRalphLoopOrchestrator
{
    public Task<Result<Guid>> SubmitAnalysisAsync(
        string repoUrl, string filePath, AnalysisType type, string title, string description,
        IReadOnlyList<string> requirements, string? targetBranch = null, string? targetCommit = null,
        int maxIterations = 15, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<Guid>("Ralph Loop not available in test environment"));

    public Task<Result<CodeAnalysisRequest?>> GetNextPendingAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<CodeAnalysisRequest?>(null));

    public Task<Result> InitializeRepositoryAsync(Guid requestId, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure("Ralph Loop not available in test environment"));

    public Task<Result<string>> GetAnalysisPromptAsync(Guid requestId, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<string>("Ralph Loop not available in test environment"));

    public Task<Result> ProcessIterationAsync(Guid requestId, string aiResponse, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure("Ralph Loop not available in test environment"));

    public Task<Result<bool>> IsCompleteAsync(Guid requestId, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(true));

    public Task<Result<string?>> FinalizeAsync(Guid requestId, bool createPullRequest = true,
        CancellationToken ct = default) => Task.FromResult(Result.Success<string?>(null));

    public Task<Result<CodeAnalysisRequest>> GetStatusAsync(Guid requestId, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<CodeAnalysisRequest>("Ralph Loop not available in test environment"));
}
