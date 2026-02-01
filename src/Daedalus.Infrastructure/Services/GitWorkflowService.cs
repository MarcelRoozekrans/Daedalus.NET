using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Git workflow service that creates checkpoint commits after successful iterations.
///     Implements the article's recovery pattern: "Is it easier to do a git reset --hard
///     and to kick Ralph back off again?"
/// </summary>
public sealed partial class GitWorkflowService(
    ILogger<GitWorkflowService> logger) : IGitWorkflowService
{
    private const int _commandTimeoutSeconds = 30;

    public async Task<Result<string>> CommitAfterSuccessAsync(
        string workspacePath,
        string taskId,
        int iterationNumber,
        string summary,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result.Failure<string>("Workspace path cannot be empty");
        }

        // Stage all changes
        var stageResult = await RunGitAsync(workspacePath, "add -A", ct).ConfigureAwait(false);
        if (!stageResult.Succeeded)
        {
            LogGitOperationFailed(logger, "stage", stageResult.StandardError);
            return Result.Failure<string>($"Failed to stage changes: {stageResult.StandardError}");
        }

        // Check if there are staged changes
        var statusResult = await RunGitAsync(workspacePath, "diff --cached --quiet", ct).ConfigureAwait(false);
        if (statusResult.Succeeded)
        {
            LogNoChangesToCommit(logger, taskId, iterationNumber);
            return Result.Success("no-changes");
        }

        // Commit with descriptive message
        var commitMessage = $"[ralph] {taskId} iteration {iterationNumber}: {summary}";
        var escapedMessage = commitMessage.Replace("\"", "\\\"", StringComparison.Ordinal);
        var commitResult =
            await RunGitAsync(workspacePath, $"commit -m \"{escapedMessage}\"", ct).ConfigureAwait(false);

        if (!commitResult.Succeeded)
        {
            LogGitOperationFailed(logger, "commit", commitResult.StandardError);
            return Result.Failure<string>($"Failed to commit: {commitResult.StandardError}");
        }

        // Extract commit SHA from output
        var sha = await GetHeadShaAsync(workspacePath, ct).ConfigureAwait(false);
        LogCommitCreated(logger, taskId, iterationNumber, sha);

        return Result.Success(sha);
    }

    public async Task<Result<string>> TagOnCompletionAsync(
        string workspacePath,
        string taskId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result.Failure<string>("Workspace path cannot be empty");
        }

        // Get latest tag to determine next version
        var latestTagResult = await GetLatestTagAsync(workspacePath, ct).ConfigureAwait(false);
        if (latestTagResult.IsFailure)
        {
            return Result.Failure<string>(latestTagResult.Error);
        }

        var nextTag = IncrementPatchVersion(latestTagResult.Value);

        var tagMessage = $"Ralph loop completed: {taskId}";
        var escapedMessage = tagMessage.Replace("\"", "\\\"", StringComparison.Ordinal);
        var tagResult = await RunGitAsync(workspacePath, $"tag -a {nextTag} -m \"{escapedMessage}\"", ct)
            .ConfigureAwait(false);

        if (!tagResult.Succeeded)
        {
            LogGitOperationFailed(logger, "tag", tagResult.StandardError);
            return Result.Failure<string>($"Failed to create tag: {tagResult.StandardError}");
        }

        LogTagCreated(logger, nextTag, taskId);
        return Result.Success(nextTag);
    }

    public async Task<Result> ResetToLastGoodAsync(
        string workspacePath,
        string? commitSha,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result.Failure("Workspace path cannot be empty");
        }

        var target = commitSha ?? "HEAD";
        var resetResult = await RunGitAsync(workspacePath, $"reset --hard {target}", ct).ConfigureAwait(false);

        if (!resetResult.Succeeded)
        {
            LogGitOperationFailed(logger, "reset", resetResult.StandardError);
            return Result.Failure($"Failed to reset: {resetResult.StandardError}");
        }

        // Clean untracked files
        var cleanResult = await RunGitAsync(workspacePath, "clean -fd", ct).ConfigureAwait(false);
        if (!cleanResult.Succeeded)
        {
            LogGitOperationFailed(logger, "clean", cleanResult.StandardError);
            // Non-fatal — reset succeeded
        }

        LogResetCompleted(logger, target);
        return Result.Success();
    }

    public async Task<Result> PushAsync(
        string workspacePath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result.Failure("Workspace path cannot be empty");
        }

        var pushResult = await RunGitAsync(workspacePath, "push --follow-tags", ct).ConfigureAwait(false);

        if (!pushResult.Succeeded)
        {
            LogGitOperationFailed(logger, "push", pushResult.StandardError);
            return Result.Failure($"Failed to push: {pushResult.StandardError}");
        }

        LogPushCompleted(logger);
        return Result.Success();
    }

    public async Task<Result<string?>> GetLatestTagAsync(
        string workspacePath,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return Result.Failure<string?>("Workspace path cannot be empty");
        }

        var result = await RunGitAsync(workspacePath, "describe --tags --abbrev=0", ct).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            // No tags exist yet
            return Result.Success<string?>(null);
        }

        return Result.Success<string?>(result.StandardOutput.Trim());
    }

    /// <summary>
    ///     Increments the patch version of a semver tag (0.0.1 → 0.0.2).
    ///     If no tag exists, starts at 0.0.1.
    /// </summary>
    internal static string IncrementPatchVersion(string? currentTag)
    {
        if (string.IsNullOrEmpty(currentTag))
        {
            return "0.0.1";
        }

        var match = SemverRegex().Match(currentTag);
        if (!match.Success)
        {
            return "0.0.1";
        }

        var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture);
        var patch = int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture);

        return $"{major}.{minor}.{patch + 1}";
    }

    private static async Task<string> GetHeadShaAsync(string workspacePath, CancellationToken ct)
    {
        var result = await RunGitAsync(workspacePath, "rev-parse --short HEAD", ct).ConfigureAwait(false);
        return result.Succeeded ? result.StandardOutput.Trim() : "unknown";
    }

    private static async Task<CommandExecutionResult> RunGitAsync(
        string workspacePath,
        string arguments,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workspacePath,
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

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_commandTimeoutSeconds));

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
                StandardError = "Command timed out",
                TimedOut = true,
                Duration = TimeSpan.FromSeconds(_commandTimeoutSeconds)
            };
        }

        return new CommandExecutionResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdoutBuilder.ToString(),
            StandardError = stderrBuilder.ToString(),
            TimedOut = false
        };
    }

    [GeneratedRegex(@"v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)",
        RegexOptions.ExplicitCapture | RegexOptions.NonBacktracking)]
    private static partial Regex SemverRegex();

    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "Git {Operation} failed: {Error}")]
    private static partial void LogGitOperationFailed(ILogger logger, string operation, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Git commit created for task {TaskId} iteration {Iteration}: {Sha}")]
    private static partial void LogCommitCreated(ILogger logger, string taskId, int iteration, string sha);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Git tag {Tag} created for task {TaskId}")]
    private static partial void LogTagCreated(ILogger logger, string tag, string taskId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information,
        Message = "Git reset completed to {Target}")]
    private static partial void LogResetCompleted(ILogger logger, string target);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Git push completed")]
    private static partial void LogPushCompleted(ILogger logger);

    [LoggerMessage(EventId = 6, Level = LogLevel.Debug,
        Message = "No changes to commit for task {TaskId} iteration {Iteration}")]
    private static partial void LogNoChangesToCommit(ILogger logger, string taskId, int iteration);
}
