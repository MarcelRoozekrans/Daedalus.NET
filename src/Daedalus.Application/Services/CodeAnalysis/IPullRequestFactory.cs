using Daedalus.Domain.CodeAnalysis;
using ZeroAlloc.Results;

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

    /// <summary>
    ///     The open pull request from <paramref name="featureBranch"/> on the repository hosted at
    ///     <paramref name="repositoryUrl"/>, or <see langword="null"/> when none is open. Dispatches on the hosting
    ///     URL alone, so a caller never needs a working tree to ask.
    /// </summary>
    Task<Result<PullRequestResult?>> FindOpenPullRequestAsync(string repositoryUrl, string featureBranch, CancellationToken ct = default);
}
