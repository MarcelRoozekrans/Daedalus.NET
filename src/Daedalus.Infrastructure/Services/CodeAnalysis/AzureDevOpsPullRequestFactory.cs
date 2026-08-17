#pragma warning disable CA1054 // URI-like parameters should not be strings
#pragma warning disable IL2026 // Members annotated with RequiresUnreferencedCodeAttribute

using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Factory for creating pull requests on Azure DevOps
/// </summary>
public sealed class AzureDevOpsPullRequestFactory(
    HttpClient httpClient,
    IRepositoryAuthenticationProvider authProvider,
    ILogger<AzureDevOpsPullRequestFactory> logger)
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
            var token = await authProvider.GetAuthTokenAsync(RepositoryPlatform.AzureDevOps, ct).ConfigureAwait(false);
            if (token.IsFailure)
            {
                return Result.Failure<PullRequestResult>(token.Error);
            }

            // Parse org/project/repo from URL
            var uri = new Uri(repositoryUrl);
            var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            if (segments.Length < 4 || !string.Equals(segments[2], "_git", StringComparison.Ordinal))
            {
                return Result.Failure<PullRequestResult>("Invalid Azure DevOps URL format");
            }

            var org = segments[0];
            var project = segments[1];
            var repo = segments[3];

            var request = new
            {
                sourceRefName = $"refs/heads/{featureBranch}",
                targetRefName = $"refs/heads/{baseBranch}",
                title,
                description,
                reviewers = Array.Empty<object>()
            };

            var auth = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));

            httpClient.DefaultRequestHeaders.Clear();
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {auth}");

            var response = await httpClient.PostAsJsonAsync(
                    $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}/pullrequests?api-version=7.0",
                    request,
                    ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                logger.LogError("Azure DevOps API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return Result.Failure<PullRequestResult>($"Azure DevOps API error: {response.StatusCode}");
            }

            var jsonString = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(jsonString);
            var content = doc.RootElement;

            var prId = content.GetProperty("pullRequestId").GetInt32();
            var prResult = new PullRequestResult
            {
                PullRequestId = prId.ToString(CultureInfo.InvariantCulture),
                PullRequestUrl = content.GetProperty("url").GetString() ?? string.Empty,
                WebUrl = $"https://dev.azure.com/{org}/{project}/_git/{repo}/pullrequest/{prId}",
                Status = PullRequestStatus.Open
            };

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Created Azure DevOps PR: {PrUrl}", prResult.WebUrl);
            }

            return Result.Success(prResult);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating Azure DevOps pull request");
            return Result.Failure<PullRequestResult>($"Error creating PR: {ex.Message}");
        }
    }
}
