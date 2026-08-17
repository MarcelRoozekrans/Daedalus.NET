using CSharpFunctionalExtensions;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Application.Services.CodeAnalysis;

/// <summary>
///     Creates pull requests on various platforms
/// </summary>
#pragma warning disable CA1054 // Uri parameters should not be strings — URLs passed as strings to platform APIs
public interface IPullRequestFactory
{
    Task<Result<PullRequestResult>> CreatePullRequestAsync(
        string repositoryUrl,
        string featureBranch,
        string baseBranch,
        string title,
        string description,
        CancellationToken ct = default);
}
