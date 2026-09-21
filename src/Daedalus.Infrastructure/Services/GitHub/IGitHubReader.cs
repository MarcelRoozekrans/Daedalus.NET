using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.GitHub;

/// <summary>Reads repository activity from GitHub for the digest.</summary>
public interface IGitHubReader
{
    Task<RepoActivity> GetActivityAsync(RepoRef repo, DateTime sinceUtc, CancellationToken ct = default);

    Task<Result<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken ct = default);
}
