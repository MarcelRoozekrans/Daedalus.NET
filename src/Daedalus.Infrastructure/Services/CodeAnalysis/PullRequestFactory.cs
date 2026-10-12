using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Router for creating pull requests on appropriate platform
/// </summary>
public sealed partial class PullRequestFactory(
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

        LogCreating(logger, platform);

        return platform switch
        {
            RepositoryPlatform.GitHub => await github.CreatePullRequestAsync(
                repositoryUrl, featureBranch, baseBranch, title, description, ct).ConfigureAwait(false),

            RepositoryPlatform.AzureDevOps => await azureDevOps.CreatePullRequestAsync(
                repositoryUrl, featureBranch, baseBranch, title, description, ct).ConfigureAwait(false),

            _ => Result<PullRequestResult>.Failure($"Unsupported platform: {platform}")
        };
    }

    public async Task<Result<PullRequestResult?>> FindOpenPullRequestAsync(
        string repositoryUrl,
        string featureBranch,
        CancellationToken ct = default)
    {
        var platform = detector.DetectPlatform(repositoryUrl);

        return platform switch
        {
            RepositoryPlatform.GitHub => await github.FindOpenPullRequestAsync(
                repositoryUrl, featureBranch, ct).ConfigureAwait(false),

            RepositoryPlatform.AzureDevOps => Result<PullRequestResult?>.Failure(
                "finding an open pull request is not supported for Azure DevOps"),

            _ => Result<PullRequestResult?>.Failure($"Unsupported platform: {platform}")
        };
    }

    [LoggerMessage(EventId = 2350, Level = LogLevel.Information, Message = "Creating PR on {Platform}")]
    private static partial void LogCreating(ILogger logger, RepositoryPlatform platform);
}
