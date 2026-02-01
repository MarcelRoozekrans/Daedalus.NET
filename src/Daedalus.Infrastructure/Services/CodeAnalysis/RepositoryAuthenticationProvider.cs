using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Manages repository authentication
/// </summary>
public sealed class RepositoryAuthenticationProvider(
    ILogger<RepositoryAuthenticationProvider> logger) : IRepositoryAuthenticationProvider
{
    public async Task<Result<string>> GetAuthTokenAsync(
        RepositoryPlatform platform,
        CancellationToken ct = default)
    {
        try
        {
            var token = platform switch
            {
                RepositoryPlatform.GitHub => Environment.GetEnvironmentVariable("GITHUB_TOKEN"),
                RepositoryPlatform.AzureDevOps => Environment.GetEnvironmentVariable("AZURE_DEVOPS_TOKEN"),
                RepositoryPlatform.GitLab => Environment.GetEnvironmentVariable("GITLAB_TOKEN"),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(token))
            {
                logger.LogWarning("No authentication token found for platform {Platform}", platform);
                return Result.Failure<string>($"No authentication token configured for {platform}");
            }

            return await Task.FromResult(Result.Success(token)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting authentication token for platform {Platform}", platform);
            return Result.Failure<string>($"Error getting auth token: {ex.Message}");
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
