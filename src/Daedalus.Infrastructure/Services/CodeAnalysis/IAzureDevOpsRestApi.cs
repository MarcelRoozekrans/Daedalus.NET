using System.Text.Json.Serialization;
using ZeroAlloc.Rest;
using ZeroAlloc.Rest.Attributes;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Azure DevOps's pull request create. ZeroAlloc.Rest generates <c>AzureDevOpsRestApiClient</c>; only
///     <see cref="AzureDevOpsPullRequestFactory"/> uses it. The token goes per call, so no call ever changes the shared
///     <see cref="System.Net.Http.HttpClient"/>. No error body is read.
/// </summary>
[ZeroAllocRestClient(MaxErrorBodyBytes = 0)]
internal interface IAzureDevOpsRestApi
{
    [Post("{organization}/{project}/_apis/git/repositories/{repository}/pullrequests")]
    Task<Result<AzureDevOpsPullRequestDto, HttpError>> CreatePullRequestAsync(
        string organization,
        string project,
        string repository,
        [Query(Name = "api-version")] string apiVersion,
        [Body] AzureDevOpsCreatePullRequestBody body,
        [Header("Authorization")] string authorization,
        CancellationToken ct);
}

internal sealed record AzureDevOpsCreatePullRequestBody(
    string SourceRefName, string TargetRefName, string Title, string Description, IReadOnlyList<string> Reviewers);

/// <summary>The fields of a created pull request Daedalus reads; both are required.</summary>
internal sealed record AzureDevOpsPullRequestDto(int? PullRequestId, string? Url);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AzureDevOpsCreatePullRequestBody))]
[JsonSerializable(typeof(AzureDevOpsPullRequestDto))]
internal sealed partial class AzureDevOpsJsonContext : JsonSerializerContext;
