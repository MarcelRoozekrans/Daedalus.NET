#pragma warning disable CA1054 // URI-like parameters should not be strings

using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Factory for creating pull requests on GitHub — the GitHub-specific leg of <see cref="PullRequestFactory"/>'s
///     platform dispatch. Owns only URL parsing here; the actual HTTP call is delegated to <see cref="GitHubApi"/>,
///     which is the single client that ever speaks to the GitHub REST API from this codebase. It catches nothing:
///     <c>GitHubApi</c> returns every expected failure, and its text is shown on a run.
/// </summary>
public sealed partial class GitHubPullRequestFactory(
    GitHubApi gitHub,
    ILogger<GitHubPullRequestFactory> logger)
{
    public async Task<Result<PullRequestResult>> CreatePullRequestAsync(
        string repositoryUrl,
        string featureBranch,
        string baseBranch,
        string title,
        string description,
        CancellationToken ct = default)
    {
        var repoRef = ParseRepository(repositoryUrl);
        if (repoRef.IsFailure)
        {
            return Result<PullRequestResult>.Failure(repoRef.Error);
        }

        var result = await gitHub.CreatePullRequestAsync(
            repoRef.Value, featureBranch, baseBranch, title, description, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            LogCreateRefused(logger, result.Error.Kind, result.Error.StatusCode);
            return Result<PullRequestResult>.Failure(result.Error.Message);
        }

        LogCreated(logger, result.Value.PullRequestId);
        return Result<PullRequestResult>.Success(result.Value);
    }

    /// <summary>
    ///     The open pull request from <paramref name="featureBranch"/> on the repository at
    ///     <paramref name="repositoryUrl"/>, or <see langword="null"/> when none is open. Parses the URL only; the
    ///     query is <see cref="GitHubApi.FindOpenPullRequestAsync"/>'s.
    /// </summary>
    public async Task<Result<PullRequestResult?>> FindOpenPullRequestAsync(
        string repositoryUrl, string featureBranch, CancellationToken ct = default)
    {
        var repoRef = ParseRepository(repositoryUrl);
        if (repoRef.IsFailure)
        {
            return Result<PullRequestResult?>.Failure(repoRef.Error);
        }

        var found = await gitHub.FindOpenPullRequestAsync(repoRef.Value, featureBranch, ct).ConfigureAwait(false);
        return found.IsSuccess
            ? Result<PullRequestResult?>.Success(found.Value)
            : Result<PullRequestResult?>.Failure(found.Error.Message);
    }

    /// <summary>The fixed failure for a remote this factory cannot read as a GitHub repository.</summary>
    public const string NotAGitHubUrl = "The repository URL is not a GitHub repository URL.";

    /// <summary>
    ///     Owner and repository out of an HTTPS or SSH GitHub remote URL. A failure never repeats the URL or any part of it:
    ///     a configured remote can carry credentials in its user info, such as <c>https://user:token@host/...</c>, and this
    ///     text reaches the run's error through the publisher (P30).
    /// </summary>
    private static Result<RepoRef> ParseRepository(string repositoryUrl)
    {
        var urlParts = repositoryUrl
            .Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
            .Replace("git@github.com:", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".git", "", StringComparison.OrdinalIgnoreCase)
            .Split('/');

        var parsed = urlParts.Length < 2 ? Result<RepoRef>.Failure(NotAGitHubUrl) : RepoRef.Parse($"{urlParts[0]}/{urlParts[1]}");
        return parsed.IsSuccess ? parsed : Result<RepoRef>.Failure(NotAGitHubUrl);
    }

    [LoggerMessage(EventId = 2340, Level = LogLevel.Error, Message = "GitHub refused to create a pull request: {Kind} {StatusCode}")]
    private static partial void LogCreateRefused(ILogger logger, GitHubErrorKind kind, int? statusCode);

    [LoggerMessage(EventId = 2341, Level = LogLevel.Information, Message = "Created GitHub PR {PullRequestId}")]
    private static partial void LogCreated(ILogger logger, string pullRequestId);
}
