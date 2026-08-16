#pragma warning disable CA1054 // URI-like parameters should not be strings
#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute

using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Factory for creating pull requests on GitHub
/// </summary>
public sealed class GitHubPullRequestFactory(
    HttpClient httpClient,
    IRepositoryAuthenticationProvider authProvider,
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
            var token = await authProvider.GetAuthTokenAsync(RepositoryPlatform.GitHub, ct).ConfigureAwait(false);
            if (token.IsFailure)
            {
                return Result.Failure<PullRequestResult>(token.Error);
            }

            // Parse owner/repo from URL
            var urlParts = repositoryUrl
                .Replace("https://github.com/", "", StringComparison.OrdinalIgnoreCase)
                .Replace("git@github.com:", "", StringComparison.OrdinalIgnoreCase)
                .Replace(".git", "", StringComparison.OrdinalIgnoreCase)
                .Split('/');

            if (urlParts.Length < 2)
            {
                return Result.Failure<PullRequestResult>("Invalid GitHub URL format");
            }

            var owner = urlParts[0];
            var repo = urlParts[1];

            var request = new
            {
                title,
                body = description,
                head = featureBranch,
                @base = baseBranch,
                draft = false
            };

            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {token.Value}");
            httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

            var response = await httpClient.PostAsJsonAsync(
                    $"https://api.github.com/repos/{owner}/{repo}/pulls",
                    request,
                    ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                logger.LogError("GitHub API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return Result.Failure<PullRequestResult>($"GitHub API error: {response.StatusCode}");
            }

            var jsonString = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(jsonString);
            var content = doc.RootElement;

            var prResult = new PullRequestResult
            {
                PullRequestId = content.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture),
                PullRequestUrl = content.GetProperty("url").GetString() ?? string.Empty,
                WebUrl = content.GetProperty("html_url").GetString() ?? string.Empty,
                Status = PullRequestStatus.Open
            };

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Created GitHub PR: {PrUrl}", prResult.WebUrl);
            }

            return Result.Success(prResult);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating GitHub pull request");
            return Result.Failure<PullRequestResult>($"Error creating PR: {ex.Message}");
        }
    }
}
