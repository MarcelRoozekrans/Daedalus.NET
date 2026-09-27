#pragma warning disable CA1054 // URI-like parameters should not be strings

using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Factory for creating pull requests on GitHub — the GitHub-specific leg of <see cref="PullRequestFactory"/>'s
///     platform dispatch. Owns only URL parsing here; the actual HTTP call is delegated to <see cref="GitHubApi"/>,
///     which is the single client that ever speaks to the GitHub REST API from this codebase.
/// </summary>
public sealed class GitHubPullRequestFactory(
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
        try
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
                logger.LogError("GitHub API error: {Error}", result.Error);
                return result;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Created GitHub PR: {PrUrl}", result.Value.WebUrl);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating GitHub pull request");
            return Result<PullRequestResult>.Failure($"Error creating PR: {ex.Message}");
        }
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

        return await gitHub.FindOpenPullRequestAsync(repoRef.Value, featureBranch, ct).ConfigureAwait(false);
    }

    /// <summary>Owner and repository out of an HTTPS or SSH GitHub remote URL.</summary>
    private static Result<RepoRef> ParseRepository(string repositoryUrl)
    {
        var urlParts = repositoryUrl
            .Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
            .Replace("git@github.com:", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".git", "", StringComparison.OrdinalIgnoreCase)
            .Split('/');

        return urlParts.Length < 2
            ? Result<RepoRef>.Failure("Invalid GitHub URL format")
            : RepoRef.Parse($"{urlParts[0]}/{urlParts[1]}");
    }
}
