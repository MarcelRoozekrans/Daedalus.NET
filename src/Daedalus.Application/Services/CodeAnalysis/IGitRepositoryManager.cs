using CSharpFunctionalExtensions;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.Services.CodeAnalysis;

/// <summary>
///     Manages git repository operations
/// </summary>
#pragma warning disable CA1054 // Uri parameters should not be strings — URLs passed as strings to LibGit2Sharp
public interface IGitRepositoryManager
{
    // Repository Initialization
    Task<Result<GitOperationContext>> CloneRepositoryAsync(
        string repoUrl,
        string? branch = null,
        string? targetPath = null,
        CancellationToken ct = default);

    Task<Result<GitOperationContext>> FetchLatestAsync(
        string workTreePath,
        CancellationToken ct = default);

    // Branch Management
    Task<Result<string>> CreateFeatureBranchAsync(
        string workTreePath,
        string branchName,
        string? fromBranch = null,
        CancellationToken ct = default);

    Task<Result> SwitchBranchAsync(
        string workTreePath,
        string branchName,
        CancellationToken ct = default);

    Task<Result> DeleteBranchAsync(
        string workTreePath,
        string branchName,
        bool force = false,
        CancellationToken ct = default);

    // Worktree Management
    Task<Result<string>> CreateWorktreeAsync(
        string baseRepoPath,
        string worktreeName,
        string branchName,
        CancellationToken ct = default);

    Task<Result> DeleteWorktreeAsync(
        string worktreePath,
        CancellationToken ct = default);

    // Changes
    Task<Result<IReadOnlyList<GitDiff>>> GetDiffsAsync(
        string workTreePath,
        string baseBranch,
        CancellationToken ct = default);

    Task<Result> ApplyPatchAsync(
        string workTreePath,
        string patchContent,
        CancellationToken ct = default);

    Task<Result> CommitChangesAsync(
        string workTreePath,
        string message,
        string? author = null,
        CancellationToken ct = default);

    // Push & PR Operations
    Task<Result> PushBranchAsync(
        string workTreePath,
        string branchName,
        bool force = false,
        CancellationToken ct = default);

    // Cleanup
    Task<Result> CleanupAsync(
        string workTreePath,
        CancellationToken ct = default);
}
