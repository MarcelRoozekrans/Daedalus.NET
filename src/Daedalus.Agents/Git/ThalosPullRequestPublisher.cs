using LibGit2Sharp;
using Thalos;
using Thalos.Git;
using ZeroAlloc.Results;
using Daedalus.Application.Services.CodeAnalysis;
using Microsoft.Extensions.Logging;
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
///         Daedalus supplies the hosting-platform implementation. <c>Daedalus.Infrastructure</c> is Ralph Loop code,
///         left untouched behind a strangler boundary until phase 2.5 retires it — <c>CleanArchitectureTests</c>
///         enforces that it takes no dependency on any <c>Thalos.*</c> namespace. <c>Daedalus.Agents</c> is the one
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
public sealed class ThalosPullRequestPublisher(
    IPullRequestFactory pullRequestFactory,
    ILogger<ThalosPullRequestPublisher> logger) : IPullRequestPublisher
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
}
