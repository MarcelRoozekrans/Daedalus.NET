using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;

namespace Daedalus.Infrastructure.Services.NoOp;

/// <summary>
///     No-op git workflow for when git integration is disabled.
///     All methods succeed silently — the loop doesn't need to know git isn't configured.
/// </summary>
public sealed class NoOpGitWorkflowService : IGitWorkflowService
{
    public Task<Result<string>> CommitAfterSuccessAsync(
        string workspacePath, string taskId, int iterationNumber, string summary, CancellationToken ct)
        => Task.FromResult(Result.Success(string.Empty));

    public Task<Result<string>> TagOnCompletionAsync(
        string workspacePath, string taskId, CancellationToken ct)
        => Task.FromResult(Result.Success(string.Empty));

    public Task<Result> PushAsync(string workspacePath, CancellationToken ct)
        => Task.FromResult(Result.Success());

    public Task<Result<string?>> GetLatestTagAsync(string workspacePath, CancellationToken ct)
        => Task.FromResult(Result.Success<string?>(null));

    public Task<Result> ResetToLastGoodAsync(
        string workspacePath, string? commitSha, CancellationToken ct)
        => Task.FromResult(Result.Success());
}
