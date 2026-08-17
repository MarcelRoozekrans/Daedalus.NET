using System.Text.RegularExpressions;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Services;
using Daedalus.Application.Services.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services;

/// <summary>
///     Orchestrates per-task workspace lifecycle: clone repo, create feature branch, push on completion.
/// </summary>
public sealed partial class WorkspaceOrchestrator(
    IProjectRepository projectRepository,
    IGitRepositoryManager gitManager,
    IPullRequestFactory prFactory,
    ILogger<WorkspaceOrchestrator> logger) : IWorkspaceOrchestrator
{
    public async Task<Result<WorkspaceInfo>> PrepareWorkspaceAsync(
        Guid projectId,
        Guid taskId,
        string taskTitle,
        CancellationToken ct)
    {
        if (projectId == Guid.Empty)
        {
            return Result.Failure<WorkspaceInfo>("Project ID is required for workspace preparation");
        }

        // Load project to get repository URL
        var projectResult = await projectRepository.GetByIdAsync(projectId, ct).ConfigureAwait(false);
        if (projectResult.IsFailure)
        {
            LogProjectNotFound(logger, projectId, projectResult.Error);
            return Result.Failure<WorkspaceInfo>($"Project not found: {projectResult.Error}");
        }

        var project = projectResult.Value;
        if (string.IsNullOrEmpty(project.RepositoryUrl))
        {
            LogNoRepositoryUrl(logger, projectId);
            return Result.Failure<WorkspaceInfo>("Project has no repository URL configured");
        }

        var baseBranch = !string.IsNullOrEmpty(project.DefaultBranch) ? project.DefaultBranch : "main";

        // Clone the repository
        LogCloningRepository(logger, project.RepositoryUrl, baseBranch);
        var cloneResult = await gitManager.CloneRepositoryAsync(
            project.RepositoryUrl, baseBranch, ct: ct).ConfigureAwait(false);

        if (cloneResult.IsFailure)
        {
            LogCloneFailed(logger, project.RepositoryUrl, cloneResult.Error);
            return Result.Failure<WorkspaceInfo>($"Failed to clone repository: {cloneResult.Error}");
        }

        var workspacePath = cloneResult.Value.LocalWorkTreePath;

        // Create feature branch
        var branchName = BuildFeatureBranchName(taskId, taskTitle);
        LogCreatingFeatureBranch(logger, branchName, workspacePath);
        var branchResult = await gitManager.CreateFeatureBranchAsync(
            workspacePath, branchName, baseBranch, ct).ConfigureAwait(false);

        if (branchResult.IsFailure)
        {
            LogBranchCreationFailed(logger, branchName, branchResult.Error);
            // Cleanup the cloned repo on failure
            await gitManager.CleanupAsync(workspacePath, ct).ConfigureAwait(false);
            return Result.Failure<WorkspaceInfo>($"Failed to create feature branch: {branchResult.Error}");
        }

        var workspace = new WorkspaceInfo(
            workspacePath,
            branchName,
            baseBranch,
            project.RepositoryUrl,
            projectId,
            taskTitle);

        LogWorkspacePrepared(logger, workspacePath, branchName);
        return Result.Success(workspace);
    }

    public async Task<Result<string?>> FinalizeWorkspaceAsync(
        WorkspaceInfo workspace,
        bool pushOnCompletion,
        CancellationToken ct)
    {
        string? prUrl = null;

        try
        {
            if (pushOnCompletion)
            {
                // Commit any remaining changes
                LogCommittingFinalChanges(logger, workspace.WorkspacePath);
                await gitManager.CommitChangesAsync(
                    workspace.WorkspacePath,
                    $"[ralph] Final state for branch {workspace.FeatureBranch}",
                    ct: ct).ConfigureAwait(false);

                // Push the feature branch
                LogPushingFeatureBranch(logger, workspace.FeatureBranch);
                var pushResult = await gitManager.PushBranchAsync(
                    workspace.WorkspacePath, workspace.FeatureBranch, ct: ct).ConfigureAwait(false);

                if (pushResult.IsFailure)
                {
                    LogPushFailed(logger, workspace.FeatureBranch, pushResult.Error);
                    // Continue to cleanup even if push fails (no PR without push)
                }
                else
                {
                    LogPushSucceeded(logger, workspace.FeatureBranch);

                    // Create pull request after successful push
                    prUrl = await CreatePullRequestAsync(workspace, ct).ConfigureAwait(false);
                }
            }

            // Cleanup the workspace
            LogCleaningUpWorkspace(logger, workspace.WorkspacePath);
            var cleanupResult = await gitManager.CleanupAsync(workspace.WorkspacePath, ct).ConfigureAwait(false);
            if (cleanupResult.IsFailure)
            {
                LogCleanupFailed(logger, workspace.WorkspacePath, cleanupResult.Error);
            }

            return Result.Success(prUrl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error finalizing workspace at {Path}", workspace.WorkspacePath);
            return Result.Failure<string?>($"Error finalizing workspace: {ex.Message}");
        }
    }

    private async Task<string?> CreatePullRequestAsync(WorkspaceInfo workspace, CancellationToken ct)
    {
        try
        {
            LogCreatingPullRequest(logger, workspace.FeatureBranch, workspace.BaseBranch);

            var prResult = await prFactory.CreatePullRequestAsync(
                workspace.RepositoryUrl,
                workspace.FeatureBranch,
                workspace.BaseBranch,
                $"[Ralph] {workspace.TaskTitle}",
                $"Automated pull request created by Ralph Loop.\n\nBranch: `{workspace.FeatureBranch}`",
                ct).ConfigureAwait(false);

            if (prResult.IsFailure)
            {
                LogPullRequestFailed(logger, workspace.FeatureBranch, prResult.Error);
                return null;
            }

            LogPullRequestCreated(logger, prResult.Value.WebUrl);
            return prResult.Value.WebUrl;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating pull request for branch {Branch}", workspace.FeatureBranch);
            return null;
        }
    }

    /// <summary>
    ///     Builds a sanitized feature branch name from the task ID and title.
    ///     Format: ralph/{short-task-id}-{sanitized-title}
    /// </summary>
    internal static string BuildFeatureBranchName(Guid taskId, string taskTitle)
    {
        var shortId = taskId.ToString("N")[..8];
        var sanitized = SanitizeBranchName(taskTitle);
        var branch = $"ralph/{shortId}-{sanitized}";

        // Git branch names max ~250 chars, keep it reasonable
        if (branch.Length > 80)
        {
            branch = branch[..80];
        }

        return branch.TrimEnd('-', '/');
    }

    private static string SanitizeBranchName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "task";
        }

        // Replace non-alphanumeric chars with hyphens, collapse multiples, lowercase
#pragma warning disable CA1308 // Branch names are conventionally lowercase in git
        var sanitized = InvalidBranchCharsRegex().Replace(input.ToLowerInvariant(), "-");
#pragma warning restore CA1308
        sanitized = MultipleHyphensRegex().Replace(sanitized, "-");
        return sanitized.Trim('-');
    }

    [GeneratedRegex("[^a-z0-9-]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvalidBranchCharsRegex();

    [GeneratedRegex("-{2,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MultipleHyphensRegex();

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Project {ProjectId} not found: {Error}")]
    private static partial void LogProjectNotFound(ILogger logger, Guid projectId, string error);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Project {ProjectId} has no repository URL — skipping workspace preparation")]
    private static partial void LogNoRepositoryUrl(ILogger logger, Guid projectId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Cloning repository {Url} (branch: {Branch})")]
    private static partial void LogCloningRepository(ILogger logger, string url, string branch);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Failed to clone repository {Url}: {Error}")]
    private static partial void LogCloneFailed(ILogger logger, string url, string error);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information,
        Message = "Creating feature branch {BranchName} in {Path}")]
    private static partial void LogCreatingFeatureBranch(ILogger logger, string branchName, string path);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error,
        Message = "Failed to create feature branch {BranchName}: {Error}")]
    private static partial void LogBranchCreationFailed(ILogger logger, string branchName, string error);

    [LoggerMessage(EventId = 7, Level = LogLevel.Information,
        Message = "Workspace prepared at {Path} on branch {Branch}")]
    private static partial void LogWorkspacePrepared(ILogger logger, string path, string branch);

    [LoggerMessage(EventId = 8, Level = LogLevel.Information,
        Message = "Committing final changes in {Path}")]
    private static partial void LogCommittingFinalChanges(ILogger logger, string path);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information,
        Message = "Pushing feature branch {BranchName}")]
    private static partial void LogPushingFeatureBranch(ILogger logger, string branchName);

    [LoggerMessage(EventId = 10, Level = LogLevel.Error,
        Message = "Failed to push feature branch {BranchName}: {Error}")]
    private static partial void LogPushFailed(ILogger logger, string branchName, string error);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information,
        Message = "Successfully pushed feature branch {BranchName}")]
    private static partial void LogPushSucceeded(ILogger logger, string branchName);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information,
        Message = "Cleaning up workspace at {Path}")]
    private static partial void LogCleaningUpWorkspace(ILogger logger, string path);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning,
        Message = "Failed to cleanup workspace at {Path}: {Error}")]
    private static partial void LogCleanupFailed(ILogger logger, string path, string error);

    [LoggerMessage(EventId = 14, Level = LogLevel.Information,
        Message = "Creating pull request: {FeatureBranch} -> {BaseBranch}")]
    private static partial void LogCreatingPullRequest(ILogger logger, string featureBranch, string baseBranch);

    [LoggerMessage(EventId = 15, Level = LogLevel.Warning,
        Message = "Failed to create pull request for branch {Branch}: {Error}")]
    private static partial void LogPullRequestFailed(ILogger logger, string branch, string error);

    [LoggerMessage(EventId = 16, Level = LogLevel.Information,
        Message = "Pull request created: {PrUrl}")]
    private static partial void LogPullRequestCreated(ILogger logger, string prUrl);
}
