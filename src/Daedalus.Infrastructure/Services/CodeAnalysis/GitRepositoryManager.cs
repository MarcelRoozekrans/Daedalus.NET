using System.Diagnostics;
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

#pragma warning disable CA1305, CA1307, MA0011, MA0089, MA0009, S1172, S1481, IL2026

/// <summary>
///     Manages git repository operations (clone, branch, commit, push) via Process-based git CLI execution.
/// </summary>
public sealed partial class GitRepositoryManager(ILogger<GitRepositoryManager> logger) : IGitRepositoryManager
{
    private readonly string _workingDirectory = Path.Combine(Path.GetTempPath(), "daedalus-repos");

    static GitRepositoryManager()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "daedalus-repos");
        Directory.CreateDirectory(workDir);
    }

    public async Task<Result<GitOperationContext>> CloneRepositoryAsync(
        string repoUrl,
        string? branch = null,
        string? targetPath = null,
        CancellationToken ct = default)
    {
        try
        {
            var repoPath = targetPath ?? Path.Combine(_workingDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(repoPath);

            LogCloning(logger, repoUrl, repoPath);

            var args = new StringBuilder("clone --single-branch");
            if (!string.IsNullOrEmpty(branch))
            {
                args.Append($" --branch {branch}");
            }

            args.Append($" \"{repoUrl}\" \"{repoPath}\"");

            var result = await RunGitAsync(_workingDirectory, args.ToString(), 300, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                LogGitOperationFailed(logger, "clone", result.StandardError);
                return Result.Failure<GitOperationContext>($"Failed to clone repository: {result.StandardError}");
            }

            var context = new GitOperationContext
            {
                RepositoryUrl = repoUrl,
                LocalWorkTreePath = repoPath,
                BaseBranch = branch ?? "main",
                CurrentBranch = branch ?? "main"
            };

            LogCloneCompleted(logger, repoPath);
            return Result.Success(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cloning repository");
            return Result.Failure<GitOperationContext>($"Error cloning repository: {ex.Message}");
        }
    }

    public async Task<Result<GitOperationContext>> FetchLatestAsync(
        string workTreePath,
        CancellationToken ct = default)
    {
        try
        {
            LogFetching(logger, workTreePath);

            var result = await RunGitAsync(workTreePath, "fetch origin", 60, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                LogGitOperationFailed(logger, "fetch", result.StandardError);
                return Result.Failure<GitOperationContext>($"Failed to fetch: {result.StandardError}");
            }

            // Get current branch name
            var branchResult = await RunGitAsync(workTreePath, "rev-parse --abbrev-ref HEAD", 10, ct)
                .ConfigureAwait(false);
            var currentBranch = branchResult.Succeeded ? branchResult.StandardOutput.Trim() : "main";

            var context = new GitOperationContext
            {
                LocalWorkTreePath = workTreePath,
                CurrentBranch = currentBranch
            };

            return Result.Success(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error fetching latest");
            return Result.Failure<GitOperationContext>($"Error fetching latest: {ex.Message}");
        }
    }

    public async Task<Result<string>> CreateFeatureBranchAsync(
        string workTreePath,
        string branchName,
        string? fromBranch = null,
        CancellationToken ct = default)
    {
        try
        {
            LogCreatingBranch(logger, branchName, fromBranch ?? "HEAD", workTreePath);

            var args = fromBranch != null
                ? $"checkout -b {branchName} {fromBranch}"
                : $"checkout -b {branchName}";

            var result = await RunGitAsync(workTreePath, args, 30, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                LogGitOperationFailed(logger, "checkout -b", result.StandardError);
                return Result.Failure<string>($"Failed to create feature branch: {result.StandardError}");
            }

            return Result.Success(branchName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating feature branch");
            return Result.Failure<string>($"Error creating feature branch: {ex.Message}");
        }
    }

    public async Task<Result> SwitchBranchAsync(
        string workTreePath,
        string branchName,
        CancellationToken ct = default)
    {
        try
        {
            LogSwitchingBranch(logger, branchName, workTreePath);

            var result = await RunGitAsync(workTreePath, $"checkout {branchName}", 30, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                LogGitOperationFailed(logger, "checkout", result.StandardError);
                return Result.Failure($"Failed to switch branch: {result.StandardError}");
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error switching branch");
            return Result.Failure($"Error switching branch: {ex.Message}");
        }
    }

    public async Task<Result> DeleteBranchAsync(
        string workTreePath,
        string branchName,
        bool force = false,
        CancellationToken ct = default)
    {
        try
        {
            var flag = force ? "-D" : "-d";
            var result = await RunGitAsync(workTreePath, $"branch {flag} {branchName}", 30, ct)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                LogGitOperationFailed(logger, "branch delete", result.StandardError);
                return Result.Failure($"Failed to delete branch: {result.StandardError}");
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting branch");
            return Result.Failure($"Error deleting branch: {ex.Message}");
        }
    }

    public async Task<Result<string>> CreateWorktreeAsync(
        string baseRepoPath,
        string worktreeName,
        string branchName,
        CancellationToken ct = default)
    {
        try
        {
            var worktreePath = Path.Combine(baseRepoPath, "worktrees", worktreeName);
            Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

            var result = await RunGitAsync(
                    baseRepoPath, $"worktree add \"{worktreePath}\" -b {branchName}", 30, ct)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                LogGitOperationFailed(logger, "worktree add", result.StandardError);
                return Result.Failure<string>($"Failed to create worktree: {result.StandardError}");
            }

            return Result.Success(worktreePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating worktree");
            return Result.Failure<string>($"Error creating worktree: {ex.Message}");
        }
    }

    public async Task<Result> DeleteWorktreeAsync(
        string worktreePath,
        CancellationToken ct = default)
    {
        try
        {
            if (Directory.Exists(worktreePath))
            {
                var parentDir = Path.GetDirectoryName(Path.GetDirectoryName(worktreePath));
                if (parentDir != null)
                {
                    await RunGitAsync(parentDir, $"worktree remove \"{worktreePath}\" --force", 30, ct)
                        .ConfigureAwait(false);
                }

                if (Directory.Exists(worktreePath))
                {
                    Directory.Delete(worktreePath, true);
                }
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting worktree");
            return Result.Failure($"Error deleting worktree: {ex.Message}");
        }
    }

    public async Task<Result<IReadOnlyList<GitDiff>>> GetDiffsAsync(
        string workTreePath,
        string baseBranch,
        CancellationToken ct = default)
    {
        try
        {
            var result = await RunGitAsync(workTreePath, $"diff {baseBranch} --name-status", 30, ct)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                LogGitOperationFailed(logger, "diff", result.StandardError);
                return Result.Failure<IReadOnlyList<GitDiff>>($"Failed to get diffs: {result.StandardError}");
            }

            var diffs = new List<GitDiff>();
            foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t', 2);
                if (parts.Length == 2)
                {
                    diffs.Add(new GitDiff
                    {
                        FilePath = parts[1].Trim()
                    });
                }
            }

            return Result.Success((IReadOnlyList<GitDiff>)diffs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting diffs");
            return Result.Failure<IReadOnlyList<GitDiff>>($"Error getting diffs: {ex.Message}");
        }
    }

    public async Task<Result> ApplyPatchAsync(
        string workTreePath,
        string patchContent,
        CancellationToken ct = default)
    {
        try
        {
            var patchFile = Path.Combine(Path.GetTempPath(), $"daedalus-patch-{Guid.NewGuid():N}.patch");
            await File.WriteAllTextAsync(patchFile, patchContent, ct).ConfigureAwait(false);

            try
            {
                var result = await RunGitAsync(workTreePath, $"apply \"{patchFile}\"", 30, ct)
                    .ConfigureAwait(false);

                if (!result.Succeeded)
                {
                    LogGitOperationFailed(logger, "apply", result.StandardError);
                    return Result.Failure($"Failed to apply patch: {result.StandardError}");
                }

                return Result.Success();
            }
            finally
            {
                File.Delete(patchFile);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error applying patch");
            return Result.Failure($"Error applying patch: {ex.Message}");
        }
    }

    public async Task<Result> CommitChangesAsync(
        string workTreePath,
        string message,
        string? author = null,
        CancellationToken ct = default)
    {
        try
        {
            // Stage all changes
            var stageResult = await RunGitAsync(workTreePath, "add -A", 30, ct).ConfigureAwait(false);
            if (!stageResult.Succeeded)
            {
                LogGitOperationFailed(logger, "add", stageResult.StandardError);
                return Result.Failure($"Failed to stage changes: {stageResult.StandardError}");
            }

            // Check if there are staged changes
            var statusResult = await RunGitAsync(workTreePath, "diff --cached --quiet", 10, ct)
                .ConfigureAwait(false);
            if (statusResult.Succeeded)
            {
                return Result.Success(); // No changes to commit
            }

            var escapedMessage = message.Replace("\"", "\\\"", StringComparison.Ordinal);
            var args = $"commit -m \"{escapedMessage}\"";
            if (!string.IsNullOrEmpty(author))
            {
                var escapedAuthor = author.Replace("\"", "\\\"", StringComparison.Ordinal);
                args += $" --author=\"{escapedAuthor}\"";
            }

            var commitResult = await RunGitAsync(workTreePath, args, 30, ct).ConfigureAwait(false);
            if (!commitResult.Succeeded)
            {
                LogGitOperationFailed(logger, "commit", commitResult.StandardError);
                return Result.Failure($"Failed to commit: {commitResult.StandardError}");
            }

            LogCommitCreated(logger, workTreePath, message);
            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error committing changes");
            return Result.Failure($"Error committing changes: {ex.Message}");
        }
    }

    public async Task<Result> PushBranchAsync(
        string workTreePath,
        string branchName,
        bool force = false,
        CancellationToken ct = default)
    {
        try
        {
            LogPushing(logger, branchName, workTreePath);

            var forceFlag = force ? " --force" : "";
            var result = await RunGitAsync(
                    workTreePath, $"push -u origin {branchName}{forceFlag}", 90, ct)
                .ConfigureAwait(false);

            if (!result.Succeeded)
            {
                LogGitOperationFailed(logger, "push", result.StandardError);
                return Result.Failure($"Failed to push branch: {result.StandardError}");
            }

            LogPushCompleted(logger, branchName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error pushing branch");
            return Result.Failure($"Error pushing branch: {ex.Message}");
        }
    }

    public Task<Result> CleanupAsync(
        string workTreePath,
        CancellationToken ct = default)
    {
        try
        {
            if (Directory.Exists(workTreePath))
            {
                LogCleaningUp(logger, workTreePath);
                Directory.Delete(workTreePath, true);
            }

            return Task.FromResult(Result.Success());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error cleaning up");
            return Task.FromResult(Result.Failure($"Error cleaning up: {ex.Message}"));
        }
    }

    /// <summary>
    ///     Executes a git command as a child process with a configurable timeout.
    /// </summary>
    private static async Task<CommandExecutionResult> RunGitAsync(
        string workingDirectory,
        string arguments,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        var sw = Stopwatch.StartNew();
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(true);
            }
            catch
            {
                // Best-effort kill
            }

            return new CommandExecutionResult
            {
                ExitCode = -1,
                StandardOutput = stdoutBuilder.ToString(),
                StandardError = ct.IsCancellationRequested ? "Operation cancelled" : "Command timed out",
                TimedOut = !ct.IsCancellationRequested,
                Duration = sw.Elapsed
            };
        }

        sw.Stop();
        return new CommandExecutionResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdoutBuilder.ToString(),
            StandardError = stderrBuilder.ToString(),
            TimedOut = false,
            Duration = sw.Elapsed
        };
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "Git {Operation} failed: {Error}")]
    private static partial void LogGitOperationFailed(ILogger logger, string operation, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Cloning repository from {Url} to {Path}")]
    private static partial void LogCloning(ILogger logger, string url, string path);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Clone completed at {Path}")]
    private static partial void LogCloneCompleted(ILogger logger, string path);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Fetching latest from {Path}")]
    private static partial void LogFetching(ILogger logger, string path);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Creating branch {BranchName} from {FromBranch} in {Path}")]
    private static partial void LogCreatingBranch(ILogger logger, string branchName, string fromBranch, string path);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "Switching to branch {BranchName} in {Path}")]
    private static partial void LogSwitchingBranch(ILogger logger, string branchName, string path);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "Commit created in {Path}: {Message}")]
    private static partial void LogCommitCreated(ILogger logger, string path, string message);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "Pushing branch {BranchName} from {Path}")]
    private static partial void LogPushing(ILogger logger, string branchName, string path);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information,
        Message = "Push completed for branch {BranchName}")]
    private static partial void LogPushCompleted(ILogger logger, string branchName);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information,
        Message = "Cleaning up workspace at {Path}")]
    private static partial void LogCleaningUp(ILogger logger, string path);
}

#pragma warning restore CA1305, CA1307, MA0011, MA0089, MA0009, S1172, S1481, IL2026
