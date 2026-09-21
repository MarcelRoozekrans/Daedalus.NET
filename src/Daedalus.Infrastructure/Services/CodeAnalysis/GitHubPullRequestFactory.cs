#pragma warning disable CA1054 // URI-like parameters should not be strings

using ZeroAlloc.Results;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging;

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
            // Parse owner/repo from URL
            var urlParts = repositoryUrl
                .Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
                .Replace("git@github.com:", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".git", "", StringComparison.OrdinalIgnoreCase)
                .Split('/');

            if (urlParts.Length < 2)
            {
                return Result<PullRequestResult>.Failure("Invalid GitHub URL format");
            }

            var repoRef = RepoRef.Parse($"{urlParts[0]}/{urlParts[1]}");
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
}
