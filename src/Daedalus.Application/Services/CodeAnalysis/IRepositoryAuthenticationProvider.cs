using Daedalus.Domain.CodeAnalysis;
using ZeroAlloc.Results;

namespace Daedalus.Application.Services.CodeAnalysis;

/// <summary>
///     Manages repository authentication
/// </summary>
public interface IRepositoryAuthenticationProvider
{
    Task<Result<string>> GetAuthTokenAsync(
        RepositoryPlatform platform,
        CancellationToken ct = default);

    Task<Result> ConfigureGitCredentialsAsync(
        RepositoryPlatform platform,
        string workTreePath,
        CancellationToken ct = default);
}
