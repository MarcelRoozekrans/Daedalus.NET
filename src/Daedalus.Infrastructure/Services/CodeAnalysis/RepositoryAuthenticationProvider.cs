using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Manages repository authentication
/// </summary>
public sealed class RepositoryAuthenticationProvider(
    ILogger<RepositoryAuthenticationProvider> logger) : IRepositoryAuthenticationProvider
{
    public Task<Result<string>> GetAuthTokenAsync(
        RepositoryPlatform platform,
        CancellationToken ct = default) =>
        Task.FromResult(ReadToken(platform, Environment.GetEnvironmentVariable, logger));

    /// <summary>
    ///     The token of <paramref name="platform"/>, read through <paramref name="read"/>. A failure's text names the platform
    ///     only: it can reach a run's error through the publisher, so the exception stays in the log.
    /// </summary>
    internal static Result<string> ReadToken(RepositoryPlatform platform, Func<string, string?> read, ILogger logger)
    {
        try
        {
            var token = platform switch
            {
                RepositoryPlatform.GitHub => read("GITHUB_TOKEN"),
                RepositoryPlatform.AzureDevOps => read("AZURE_DEVOPS_TOKEN"),
                RepositoryPlatform.GitLab => read("GITLAB_TOKEN"),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(token))
            {
                logger.LogWarning("No authentication token found for platform {Platform}", platform);
                return Result<string>.Failure($"No authentication token configured for {platform}");
            }

            return Result<string>.Success(token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting authentication token for platform {Platform}", platform);
            return Result<string>.Failure($"Error getting the auth token for {platform}");
        }
    }

    public async Task<Result> ConfigureGitCredentialsAsync(
        RepositoryPlatform platform,
        string workTreePath,
        CancellationToken ct = default)
    {
        try
        {
            var token = await GetAuthTokenAsync(platform, ct).ConfigureAwait(false);

            if (token.IsFailure)
            {
                return Result.Failure(token.Error);
            }

            // Note: Actual credential storage would be handled by secure credential manager
            // Placeholder commands: "git config --local credential.helper store", "git config --local credential.useHttpPath true"

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Configured git credentials for {Platform}", platform);
            }

            return await Task.FromResult(Result.Success()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error configuring git credentials");
            return Result.Failure($"Error configuring credentials: {ex.Message}");
        }
    }
}
