#pragma warning disable CA1054 // URI-like parameters should not be strings: a git remote is passed as configured.

using System.Globalization;
using System.Text;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Daedalus.Infrastructure.Services.GitHub;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Timeout;
using ZeroAlloc.Rest;
using ZeroAlloc.Rest.SystemTextJson;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Opens a pull request on Azure DevOps. The token is sent per call, never set on the shared client, and the create
///     is sent once (see <c>InfrastructureServiceExtensions.AddAzureDevOpsPullRequestFactory</c>). A failure's text is
///     built from the status alone, so it can be shown on a run.
/// </summary>
public sealed partial class AzureDevOpsPullRequestFactory(
    HttpClient httpClient,
    IRepositoryAuthenticationProvider authProvider,
    ILogger<AzureDevOpsPullRequestFactory> logger)
{
    /// <summary>The Azure DevOps REST API version every create is sent with.</summary>
    public const string ApiVersion = "7.0";

    private static readonly Uri ServiceRoot = new("https://dev.azure.com/", UriKind.Absolute);
    private static readonly SystemTextJsonSerializer Serializer = new(AzureDevOpsJsonContext.Default);

    // The concrete generated type: CA1859 asks for it.
    private readonly AzureDevOpsRestApiClient _api = new(Rooted(httpClient), Serializer);

    public async Task<Result<PullRequestResult>> CreatePullRequestAsync(
        string repositoryUrl,
        string featureBranch,
        string baseBranch,
        string title,
        string description,
        CancellationToken ct = default)
    {
        var token = await authProvider.GetAuthTokenAsync(RepositoryPlatform.AzureDevOps, ct).ConfigureAwait(false);
        if (token.IsFailure)
            return Result<PullRequestResult>.Failure(token.Error);

        if (!TryParse(repositoryUrl, out var organization, out var project, out var repository))
            return Result<PullRequestResult>.Failure("Invalid Azure DevOps URL format");

        // Per call, never on the shared client's DefaultRequestHeaders: two runs opening pull requests at once must each
        // send their own token.
        var authorization = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($":{token.Value}"));
        var body = new AzureDevOpsCreatePullRequestBody($"refs/heads/{featureBranch}", $"refs/heads/{baseBranch}", title, description, []);

        Result<AzureDevOpsPullRequestDto, HttpError> created;
        try
        {
            // A path segment of a parsed URL is still escaped, and the route escapes again, so each one is unescaped first.
            created = await _api.CreatePullRequestAsync(
                Uri.UnescapeDataString(organization), Uri.UnescapeDataString(project), Uri.UnescapeDataString(repository),
                ApiVersion, body, authorization, ct).ConfigureAwait(false);
        }
        catch (TimeoutRejectedException)
        {
            return Result<PullRequestResult>.Failure(MayHaveLanded("Azure DevOps API error: the request timed out"));
        }
        catch (ExecutionRejectedException ex)
        {
            LogRejected(logger, ex.GetType().Name);
            return Result<PullRequestResult>.Failure("Azure DevOps API error: the request was not sent");
        }

        if (created.IsFailure)
        {
            LogCreateFailed(logger, created.Error.Kind, (int)created.Error.StatusCode);
            return Result<PullRequestResult>.Failure(Describe(created.Error));
        }

        if (created.Value is not { PullRequestId: { } id, Url: { } url })
            return Result<PullRequestResult>.Failure(MayHaveLanded("Azure DevOps API error: the answer was not in the expected shape"));

        var result = new PullRequestResult
        {
            PullRequestId = id.ToString(CultureInfo.InvariantCulture),
            PullRequestUrl = url,
            WebUrl = $"https://dev.azure.com/{organization}/{project}/_git/{repository}/pullrequest/{id}",
            Status = PullRequestStatus.Open,
        };

        LogCreated(logger, result.WebUrl);
        return Result<PullRequestResult>.Success(result);
    }

    /// <summary>Sets the base address on the typed client's own new instance, before anything is sent.</summary>
    private static HttpClient Rooted(HttpClient http)
    {
        http.BaseAddress = ServiceRoot;
        return http;
    }

    /// <summary>The organization, project and repository of <c>https://dev.azure.com/{org}/{project}/_git/{repo}</c>, still escaped.</summary>
    private static bool TryParse(string repositoryUrl, out string organization, out string project, out string repository)
    {
        organization = project = repository = string.Empty;
        if (!Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri))
            return false;

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || !string.Equals(segments[2], "_git", StringComparison.Ordinal))
            return false;

        (organization, project, repository) = (segments[0], segments[1], segments[3]);
        return true;
    }

    /// <summary>
    ///     The run-visible text of a failure: the status name, never the body. A timeout, a 5xx, a transport failure and an
    ///     unreadable answer can each follow a pull request that was created, so they end with the sentence GitHub's write
    ///     errors use. Nothing looks for an existing Azure DevOps pull request before a retry, so this text is the warning.
    /// </summary>
    private static string Describe(HttpError error) => error.Kind switch
    {
        HttpErrorKind.Status when (int)error.StatusCode >= 500 => MayHaveLanded($"Azure DevOps API error: {error.StatusCode}"),
        HttpErrorKind.Status => $"Azure DevOps API error: {error.StatusCode}",
        HttpErrorKind.Timeout => MayHaveLanded("Azure DevOps API error: the request timed out"),
        HttpErrorKind.Transport => MayHaveLanded("Azure DevOps API error: Azure DevOps could not be reached"),
        _ => MayHaveLanded("Azure DevOps API error: the answer could not be read"),
    };

    private static string MayHaveLanded(string text) => $"{text}. {GitHubError.MayHaveLanded}";

    [LoggerMessage(EventId = 2330, Level = LogLevel.Error, Message = "Azure DevOps refused to create a pull request: {Kind} {StatusCode}")]
    private static partial void LogCreateFailed(ILogger logger, HttpErrorKind kind, int statusCode);

    [LoggerMessage(EventId = 2331, Level = LogLevel.Information, Message = "Created Azure DevOps PR: {PrUrl}")]
    private static partial void LogCreated(ILogger logger, string prUrl);

    [LoggerMessage(EventId = 2332, Level = LogLevel.Warning, Message = "The resilience pipeline refused an Azure DevOps request: {Rejection}")]
    private static partial void LogRejected(ILogger logger, string rejection);
}
