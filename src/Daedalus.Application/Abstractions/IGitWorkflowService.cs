using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Git workflow operations for the Ralph loop checkpoint pattern.
///     The article emphasizes: "When the tests pass... git add -A... git commit... git push...
///     As soon as there are no build or test errors create a git tag."
///     This enables the recovery escape hatch: git reset --hard when Ralph goes off the rails.
/// </summary>
public interface IGitWorkflowService
{
    /// <summary>
    ///     Commits all changes after a successful iteration.
    ///     Creates a recovery checkpoint that enables git reset --hard fallback.
    /// </summary>
    /// <param name="workspacePath">Root directory of the workspace.</param>
    /// <param name="taskId">Task identifier for the commit message.</param>
    /// <param name="iterationNumber">Current iteration number.</param>
    /// <param name="summary">Brief summary of changes made.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The commit SHA or failure.</returns>
    Task<Result<string>> CommitAfterSuccessAsync(
        string workspacePath,
        string taskId,
        int iterationNumber,
        string summary,
        CancellationToken ct);

    /// <summary>
    ///     Creates a git tag after all tests pass and task completes successfully.
    ///     Implements incremental semver tagging (0.0.1, 0.0.2, ...).
    /// </summary>
    /// <param name="workspacePath">Root directory of the workspace.</param>
    /// <param name="taskId">Task identifier for the tag message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tag name or failure.</returns>
    Task<Result<string>> TagOnCompletionAsync(
        string workspacePath,
        string taskId,
        CancellationToken ct);

    /// <summary>
    ///     Resets to the last known-good commit when Ralph goes off the rails.
    ///     The article: "Is it easier to do a git reset --hard and to kick Ralph back off again?"
    /// </summary>
    /// <param name="workspacePath">Root directory of the workspace.</param>
    /// <param name="commitSha">Optional specific commit to reset to. If null, resets to last commit.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or failure.</returns>
    Task<Result> ResetToLastGoodAsync(
        string workspacePath,
        string? commitSha,
        CancellationToken ct);

    /// <summary>
    ///     Pushes committed changes to the remote repository.
    /// </summary>
    /// <param name="workspacePath">Root directory of the workspace.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Success or failure.</returns>
    Task<Result> PushAsync(
        string workspacePath,
        CancellationToken ct);

    /// <summary>
    ///     Gets the latest tag version for determining the next tag increment.
    /// </summary>
    /// <param name="workspacePath">Root directory of the workspace.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The latest tag or null if no tags exist.</returns>
    Task<Result<string?>> GetLatestTagAsync(
        string workspacePath,
        CancellationToken ct);
}
