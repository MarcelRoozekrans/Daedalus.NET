using System.Diagnostics.CodeAnalysis;
using CSharpFunctionalExtensions;

namespace Daedalus.Application.Services;

/// <summary>
///     Workspace information for a task execution session.
/// </summary>
[SuppressMessage("Design", "CA1054", Justification = "Internal record uses string URL for git CLI")]
[SuppressMessage("Design", "CA1056", Justification = "Internal record uses string URL for git CLI")]
public sealed record WorkspaceInfo(
    string WorkspacePath,
    string FeatureBranch,
    string BaseBranch,
    string RepositoryUrl,
    Guid ProjectId,
    string TaskTitle);

/// <summary>
///     Orchestrates per-task workspace lifecycle: clone repo, create feature branch, push on completion.
/// </summary>
public interface IWorkspaceOrchestrator
{
    /// <summary>
    ///     Prepares a workspace for task execution: loads project, clones repo, creates feature branch.
    ///     Returns failure (non-fatal) if the project has no RepositoryUrl.
    /// </summary>
    Task<Result<WorkspaceInfo>> PrepareWorkspaceAsync(
        Guid projectId,
        Guid taskId,
        string taskTitle,
        CancellationToken ct);

    /// <summary>
    ///     Finalizes the workspace after task execution: push on completion, create PR, cleanup.
    ///     Returns the PR URL if a pull request was created, null otherwise.
    /// </summary>
    Task<Result<string?>> FinalizeWorkspaceAsync(
        WorkspaceInfo workspace,
        bool pushOnCompletion,
        CancellationToken ct);
}
