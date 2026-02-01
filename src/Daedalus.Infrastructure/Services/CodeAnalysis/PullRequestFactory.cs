using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Router for creating pull requests on appropriate platform
/// </summary>
public sealed class PullRequestFactory(
    GitHubPullRequestFactory github,
    AzureDevOpsPullRequestFactory azureDevOps,
    IRepositoryPlatformDetector detector,
    ILogger<PullRequestFactory> logger) : IPullRequestFactory
{
    public async Task<Result<PullRequestResult>> CreatePullRequestAsync(
        string repositoryUrl,
        string featureBranch,
        string baseBranch,
        string title,
        string description,
        CancellationToken ct = default)
    {
        var platform = detector.DetectPlatform(repositoryUrl);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Creating PR on {Platform} for {RepositoryUrl}", platform, repositoryUrl);
        }

        return platform switch
        {
            RepositoryPlatform.GitHub => await github.CreatePullRequestAsync(
                repositoryUrl, featureBranch, baseBranch, title, description, ct).ConfigureAwait(false),

            RepositoryPlatform.AzureDevOps => await azureDevOps.CreatePullRequestAsync(
                repositoryUrl, featureBranch, baseBranch, title, description, ct).ConfigureAwait(false),

            _ => Result.Failure<PullRequestResult>($"Unsupported platform: {platform}")
        };
    }
}
