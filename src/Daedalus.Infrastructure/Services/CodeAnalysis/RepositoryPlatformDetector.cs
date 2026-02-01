using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

#pragma warning disable CA1307, CA1308, CA1822, CA1054, S1172, S1481, MA0006, MA0011, MA0089, IL2026

/// <summary>
///     Detects repository platform from URL
/// </summary>
public sealed class RepositoryPlatformDetector(ILogger<RepositoryPlatformDetector> logger) : IRepositoryPlatformDetector
{
    public RepositoryPlatform DetectPlatform(string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return RepositoryPlatform.GitHub; // Default
        }

        var lowerUrl = repositoryUrl.ToUpperInvariant();

        if (lowerUrl.Contains("GITHUB.COM"))
        {
            return RepositoryPlatform.GitHub;
        }

        if (lowerUrl.Contains("DEV.AZURE.COM") || lowerUrl.Contains("VISUALSTUDIO.COM"))
        {
            return RepositoryPlatform.AzureDevOps;
        }

        if (lowerUrl.Contains("GITLAB.COM"))
        {
            return RepositoryPlatform.GitLab;
        }

        // Default to GitHub for unknown platforms
        return RepositoryPlatform.GitHub;
    }

    public async Task<Result<RepositoryInfo>> ParseRepositoryUrlAsync(
        string repositoryUrl,
        CancellationToken ct = default)
    {
        try
        {
            var platform = DetectPlatform(repositoryUrl);

            return platform switch
            {
                RepositoryPlatform.GitHub => await ParseGitHubUrlAsync(repositoryUrl, ct).ConfigureAwait(false),
                RepositoryPlatform.AzureDevOps => await ParseAzureDevOpsUrlAsync(repositoryUrl, ct)
                    .ConfigureAwait(false),
                RepositoryPlatform.GitLab => await ParseGitLabUrlAsync(repositoryUrl, ct).ConfigureAwait(false),
                _ => Result.Failure<RepositoryInfo>("Unsupported platform")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error parsing repository URL: {Url}", repositoryUrl);
            return Result.Failure<RepositoryInfo>($"Error parsing repository URL: {ex.Message}");
        }
    }

    private async Task<Result<RepositoryInfo>> ParseGitHubUrlAsync(
        string repositoryUrl,
        CancellationToken ct = default)
    {
        // Format: https://github.com/owner/repo.git or git@github.com:owner/repo.git
        var uri = new Uri(repositoryUrl.Replace("git@github.com:", "https://github.com/").TrimEnd('/'));
        var parts = uri.AbsolutePath.TrimStart('/').TrimEnd('/').Replace(".git", "").Split('/');

        if (parts.Length < 2)
        {
            return Result.Failure<RepositoryInfo>("Invalid GitHub repository URL format");
        }

        var owner = parts[0];
        var repo = parts[1];

        var info = new RepositoryInfo
        {
            Platform = RepositoryPlatform.GitHub,
            Owner = owner,
            Repository = repo,
            HttpsUrl = BuildGitHubHttpsUrl(owner, repo),
            SshUrl = BuildGitHubSshUrl(owner, repo),
            WebUrl = BuildGitHubWebUrl(owner, repo),
            ApiBaseUrl = GetGitHubApiBaseUrl()
        };

        return await Task.FromResult(Result.Success(info)).ConfigureAwait(false);
    }

    private async Task<Result<RepositoryInfo>> ParseAzureDevOpsUrlAsync(
        string repositoryUrl,
        CancellationToken ct = default)
    {
        // Format: https://dev.azure.com/org/project/_git/repo
        var uri = new Uri(repositoryUrl.TrimEnd('/'));
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < 4 || segments[2] != "_git")
        {
            return Result.Failure<RepositoryInfo>("Invalid Azure DevOps repository URL format");
        }

        var org = segments[0];
        var project = segments[1];
        var repo = segments[3];

        var info = new RepositoryInfo
        {
            Platform = RepositoryPlatform.AzureDevOps,
            Owner = org,
            Repository = repo,
            HttpsUrl = $"https://dev.azure.com/{org}/{project}/_git/{repo}",
            SshUrl = $"git@ssh.dev.azure.com:v3/{org}/{project}/{repo}",
            WebUrl = $"https://dev.azure.com/{org}/{project}/_git/{repo}",
            ApiBaseUrl = $"https://dev.azure.com/{org}/{project}/_apis"
        };

        return await Task.FromResult(Result.Success(info)).ConfigureAwait(false);
    }

    private async Task<Result<RepositoryInfo>> ParseGitLabUrlAsync(
        string repositoryUrl,
        CancellationToken ct = default)
    {
        // Format: https://gitlab.com/owner/repo.git
        var uri = new Uri(repositoryUrl.Replace("git@gitlab.com:", "https://gitlab.com/").TrimEnd('/'));
        var parts = uri.AbsolutePath.TrimStart('/').TrimEnd('/').Replace(".git", "").Split('/');

        if (parts.Length < 2)
        {
            return Result.Failure<RepositoryInfo>("Invalid GitLab repository URL format");
        }

        var owner = parts[0];
        var repo = parts[1];

        var info = new RepositoryInfo
        {
            Platform = RepositoryPlatform.GitLab,
            Owner = owner,
            Repository = repo,
            HttpsUrl = BuildGitLabHttpsUrl(owner, repo),
            SshUrl = BuildGitLabSshUrl(owner, repo),
            WebUrl = BuildGitLabWebUrl(owner, repo),
            ApiBaseUrl = GetGitLabApiBaseUrl()
        };

        return await Task.FromResult(Result.Success(info)).ConfigureAwait(false);
    }

    private static string BuildGitHubHttpsUrl(string owner, string repo) => $"{_gitHubBaseUrl}/{owner}/{repo}.git";
    private static string BuildGitHubSshUrl(string owner, string repo) => $"{_gitHubHost}:{owner}/{repo}.git";
    private static string BuildGitHubWebUrl(string owner, string repo) => $"{_gitHubBaseUrl}/{owner}/{repo}";
    private static string GetGitHubApiBaseUrl() => _gitHubApiUrl;
    private static string BuildGitLabHttpsUrl(string owner, string repo) => $"{_gitLabBaseUrl}/{owner}/{repo}.git";
    private static string BuildGitLabSshUrl(string owner, string repo) => $"{_gitLabHost}:{owner}/{repo}.git";
    private static string BuildGitLabWebUrl(string owner, string repo) => $"{_gitLabBaseUrl}/{owner}/{repo}";
    private static string GetGitLabApiBaseUrl() => _gitLabApiUrl;

#pragma warning disable S1075 // Hardcoded URLs are intentional configuration constants
    private const string _gitHubBaseUrl = "https://github.com";
    private const string _gitHubApiUrl = "https://api.github.com";
    private const string _gitHubHost = "git@github.com";
    private const string _gitLabBaseUrl = "https://gitlab.com";
    private const string _gitLabApiUrl = "https://gitlab.com/api/v4";
    private const string _gitLabHost = "git@gitlab.com";
#pragma warning restore S1075
}

#pragma warning restore CA1307, CA1308, CA1822, CA1054, S1172, S1481, MA0006, MA0011, MA0089, IL2026
