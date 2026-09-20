using ZeroAlloc.Results;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;

namespace Daedalus.Tests.Playwright.Browser;

internal class StubPullRequestFactory : IPullRequestFactory
{
    public Task<Result<PullRequestResult>> CreatePullRequestAsync(
        string repositoryUrl, string featureBranch, string baseBranch,
        string title, string description, CancellationToken ct = default) =>
        Task.FromResult(Result<PullRequestResult>.Failure("Pull request service not available in test environment"));
}
