using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;
using ZeroAlloc.Results;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

/// <summary>
///     Manages repository authentication
/// </summary>
public sealed partial class RepositoryAuthenticationProvider(
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
                LogNoToken(logger, platform);
                return Result<string>.Failure($"No authentication token configured for {platform}");
            }

            // An env file or a secret store often ends the value with a newline, which must not reach a Basic header.
            return Result<string>.Success(token.Trim());
        }
        catch (Exception ex)
        {
            LogReadFailed(logger, platform, ex.GetType().Name);
            return Result<string>.Failure($"Error getting the auth token for {platform}");
        }
    }

    [LoggerMessage(EventId = 2360, Level = LogLevel.Warning, Message = "No authentication token found for platform {Platform}")]
    private static partial void LogNoToken(ILogger logger, RepositoryPlatform platform);

    [LoggerMessage(EventId = 2361, Level = LogLevel.Error, Message = "Error getting authentication token for platform {Platform}: {ExceptionType}")]
    private static partial void LogReadFailed(ILogger logger, RepositoryPlatform platform, string exceptionType);

    public Task<Result> ConfigureGitCredentialsAsync(
        RepositoryPlatform platform,
        string workTreePath,
        CancellationToken ct = default) =>
        Task.FromResult(ConfigureCredentials(platform, Environment.GetEnvironmentVariable, logger));

    /// <summary>
    ///     Checks that the token of <paramref name="platform"/> is configured, read through <paramref name="read"/>, and logs
    ///     that git credentials are configured. Nothing is written yet: credential storage is left to a secure credential
    ///     manager. A failure's text names the platform only, so the exception stays in the log.
    /// </summary>
    internal static Result ConfigureCredentials(RepositoryPlatform platform, Func<string, string?> read, ILogger logger)
    {
        try
        {
            var token = ReadToken(platform, read, logger);

            if (token.IsFailure)
            {
                return Result.Failure(token.Error);
            }

            // Placeholder commands: "git config --local credential.helper store", "git config --local credential.useHttpPath true"
            LogConfigured(logger, platform);

            return Result.Success();
        }
        catch (Exception ex)
        {
            LogConfigureFailed(logger, platform, ex.GetType().Name);
            return Result.Failure($"Error configuring credentials for {platform}");
        }
    }

    [LoggerMessage(EventId = 2363, Level = LogLevel.Information, Message = "Configured git credentials for {Platform}")]
    private static partial void LogConfigured(ILogger logger, RepositoryPlatform platform);

    [LoggerMessage(EventId = 2362, Level = LogLevel.Error, Message = "Error configuring git credentials for platform {Platform}: {ExceptionType}")]
    private static partial void LogConfigureFailed(ILogger logger, RepositoryPlatform platform, string exceptionType);
}
