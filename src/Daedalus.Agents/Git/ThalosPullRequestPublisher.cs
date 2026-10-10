using Daedalus.Application.Services.CodeAnalysis;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using Thalos;
using Thalos.Git;
using ZeroAlloc.Results;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Daedalus.Agents.Git;

/// <summary>
///     Implements Thalos's <see cref="IPullRequestPublisher"/> over Daedalus's own
///     <see cref="IPullRequestFactory"/>, so the <c>git__open_pull_request</c> agent tool inherits
///     <see cref="IPullRequestFactory"/>'s platform dispatch for free — GitHub and Azure DevOps both work through
///     this one class, without it knowing which platform a given repository is hosted on.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this lives in Daedalus.Agents, not Daedalus.Infrastructure.</b> Thalos defines the abstraction and
///         knows nothing of GitHub or Azure DevOps by design (see the phase 2.1 design doc's placement decision);
///         Daedalus supplies the hosting-platform implementation. <c>Daedalus.Infrastructure</c> stays free of Thalos —
///         <c>CleanArchitectureTests</c> enforces that it takes no dependency on any <c>Thalos.*</c> namespace.
///         <c>Daedalus.Agents</c> is the one
///         project that already depends on both Thalos.NET and (one-way) <c>Daedalus.Infrastructure</c>, which is
///         exactly the seam this class needs to sit on.
///     </para>
///     <para>
///         Thalos's contract is written against a local working tree (<c>repositoryPath</c>, the directory
///         containing <c>.git</c>) — the same shape <c>IGitWriteService</c> uses, since both are meant to be driven
///         by the same git agent tools in sequence (branch, commit, push, open pull request). Daedalus's
///         <see cref="IPullRequestFactory"/> dispatches on a hosting URL instead (GitHub vs Azure DevOps), so this
///         class reads the working tree's <c>origin</c> remote via LibGit2Sharp to recover the URL
///         <see cref="IPullRequestFactory"/> needs, then delegates everything else to it.
///     </para>
/// </remarks>
public sealed partial class ThalosPullRequestPublisher(
    IPullRequestFactory pullRequestFactory,
    ILogger<ThalosPullRequestPublisher> logger) : IPullRequestPublisher, IOpenPullRequestLookup
{
    public async ValueTask<Result<PullRequestResult, AgentError>> OpenPullRequestAsync(
        string repositoryPath,
        string sourceBranch,
        string targetBranch,
        string title,
        string body,
        CancellationToken ct)
    {
        string? repositoryUrl;
        try
        {
            // LibGit2Sharp is synchronous; run it on the thread pool rather than block the caller, matching
            // Thalos.Git.LibGit2Sharp.LibGit2SharpGitWriteService's own convention for the same library.
            repositoryUrl = await Task.Run(
                () =>
                {
                    using var repo = new Repository(repositoryPath);
                    return repo.Network.Remotes["origin"]?.Url;
                },
                ct).ConfigureAwait(false);
        }
        catch (RepositoryNotFoundException)
        {
            logger.LogError("No git repository found at {RepositoryPath}", repositoryPath);
            return Result<PullRequestResult, AgentError>.Failure(AgentError.GitRepositoryNotFound(repositoryPath));
        }
        catch (LibGit2SharpException ex)
        {
            // A repository LibGit2Sharp cannot open or read, such as one with an unreadable config, is an expected
            // failure of this call, not a fault: it fails the publish with a message instead of escaping it.
            LogRemoteReadFailed(logger, ex, repositoryPath);
            return Result<PullRequestResult, AgentError>.Failure(
                AgentError.GitOperationFailed($"Failed to read the 'origin' remote of the repository at {repositoryPath}", ex.Message));
        }

        if (string.IsNullOrEmpty(repositoryUrl))
        {
            logger.LogError("Repository at {RepositoryPath} has no 'origin' remote", repositoryPath);
            return Result<PullRequestResult, AgentError>.Failure(AgentError.GitRepositoryNotFound(repositoryPath));
        }

        var result = await pullRequestFactory.CreatePullRequestAsync(
            repositoryUrl, sourceBranch, targetBranch, title, body, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            logger.LogError(
                "Failed to open pull request for {RepositoryUrl}: {Error}", repositoryUrl, result.Error);
            return Result<PullRequestResult, AgentError>.Failure(
                AgentError.GitOperationFailed("Failed to open pull request", result.Error));
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Opened pull request: {PrUrl}", result.Value.WebUrl);
        }

        return Result<PullRequestResult, AgentError>.Success(
            new PullRequestResult(result.Value.WebUrl, result.Value.PullRequestId));
    }

    /// <summary>
    ///     The open pull request from <paramref name="sourceBranch"/> on the repository at <paramref name="remoteUrl"/>,
    ///     looked up through <see cref="IPullRequestFactory"/>'s platform dispatch. Unlike
    ///     <see cref="OpenPullRequestAsync"/> this never opens a working tree: the remote URL is the configured one,
    ///     passed in, so there is no synchronous LibGit2Sharp call to move off the caller's thread.
    /// </summary>
    public async ValueTask<Result<PullRequestResult?, AgentError>> FindOpenPullRequestAsync(
        string remoteUrl, string sourceBranch, CancellationToken ct)
    {
        var result = await pullRequestFactory.FindOpenPullRequestAsync(remoteUrl, sourceBranch, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            LogLookupFailed(logger, remoteUrl, result.Error);
            return Result<PullRequestResult?, AgentError>.Failure(
                AgentError.GitOperationFailed("Failed to look up an open pull request", result.Error));
        }

        return Result<PullRequestResult?, AgentError>.Success(
            result.Value is { } found ? new PullRequestResult(found.WebUrl, found.PullRequestId) : null);
    }

    [LoggerMessage(
        EventId = 2321,
        Level = LogLevel.Error,
        Message = "Failed to read the origin remote of the repository at {RepositoryPath}")]
    private static partial void LogRemoteReadFailed(ILogger logger, Exception exception, string repositoryPath);

    [LoggerMessage(
        EventId = 2320,
        Level = LogLevel.Error,
        Message = "Failed to look up an open pull request for {RepositoryUrl}: {Error}")]
    private static partial void LogLookupFailed(ILogger logger, string repositoryUrl, string error);
}
