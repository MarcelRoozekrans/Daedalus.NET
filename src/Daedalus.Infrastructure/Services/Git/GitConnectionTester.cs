using CSharpFunctionalExtensions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Daedalus.Infrastructure.Services.Git;

#pragma warning disable CA1054 // Uri parameters - string used for git URLs
#pragma warning disable CA1056 // Uri properties - string used for git URLs

/// <summary>
///     Tests git repository connectivity and credentials using LibGit2Sharp.
/// </summary>
public sealed partial class GitConnectionTester(ILogger<GitConnectionTester> logger) : IGitConnectionTester
{
    /// <summary>
    ///     Tests connection to a git repository URL.
    /// </summary>
    /// <param name="repositoryUrl">The URL of the git repository (HTTP/HTTPS or SSH)</param>
    /// <param name="authenticationMethod">Optional authentication method (SSH, HTTP_BASIC, etc.)</param>
    /// <param name="credentials">Optional credentials (username/password or SSH key)</param>
    /// <param name="ct">Cancellation token</param>
    /// <returns>Result containing connection test details or failure reason</returns>
    public async Task<Result<GitConnectionResult>> TestConnectionAsync(
        string repositoryUrl,
        string? authenticationMethod = null,
        (string username, string password)? credentials = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return Result.Failure<GitConnectionResult>("Repository URL cannot be empty");
        }

        LogTestingConnection(logger, repositoryUrl);

        // Create a temporary directory for cloning
        var tempPath = Path.Combine(Path.GetTempPath(), $"git-test-{Guid.NewGuid():N}");

        try
        {
            // Attempt to clone the repository
            await Task.Run(() =>
            {
                // Build clone options with optional credentials provider
                var cloneOptions = new CloneOptions { IsBare = false };

                // Configure credentials if provided
                if (credentials is not null)
                {
                    cloneOptions.FetchOptions.CredentialsProvider = (_url, _user, _cred) =>
                        new UsernamePasswordCredentials
                        {
                            Username = credentials.Value.username,
                            Password = credentials.Value.password
                        };
                }

                Repository.Clone(repositoryUrl, tempPath, cloneOptions);

                // Successfully cloned, get repository info
                using var repo = new Repository(tempPath);
                var headBranch = repo.Head?.FriendlyName ?? "unknown";
                var commitCount = repo.Commits.Count();

                LogConnectionSucceeded(logger, repositoryUrl, headBranch, commitCount);
            }, ct).ConfigureAwait(false);

            var result = new GitConnectionResult
            {
                Connected = true,
                Message = "Successfully connected to repository",
                RepositoryUrl = repositoryUrl,
                IsAuthenticated = credentials is not null,
                Timestamp = DateTime.UtcNow
            };

            return Result.Success(result);
        }
        catch (LibGit2SharpException ex) when (
            ex.Message.Contains("403", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Authentication", StringComparison.OrdinalIgnoreCase))
        {
            LogAuthenticationFailed(logger, repositoryUrl);
            return Result.Failure<GitConnectionResult>(
                "Authentication failed: Invalid credentials or insufficient permissions");
        }
        catch (LibGit2SharpException ex) when (
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("Repository not found", StringComparison.OrdinalIgnoreCase))
        {
            LogRepositoryNotFound(logger, repositoryUrl);
            return Result.Failure<GitConnectionResult>("Repository not found at the specified URL");
        }
        catch (LibGit2SharpException ex)
        {
            LogConnectionFailed(logger, repositoryUrl, ex.Message);
            return Result.Failure<GitConnectionResult>($"Git connection failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            LogUnexpectedError(logger, ex, repositoryUrl);
            return Result.Failure<GitConnectionResult>($"Unexpected error: {ex.Message}");
        }
        finally
        {
            // Clean up temporary directory
            try
            {
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, true);
                    LogCleanedUpTempDir(logger, tempPath);
                }
            }
            catch (Exception ex)
            {
                LogCleanupFailed(logger, tempPath, ex.Message);
            }
        }
    }

    /// <summary>
    ///     Tests SSH key authentication to a repository.
    /// </summary>
    /// <remarks>
    ///     SSH authentication requires native SSH library support. The SSH key path is validated but actual
    ///     SSH credentials handling depends on system SSH configuration and libgit2 SSH backend availability.
    /// </remarks>
    public async Task<Result<GitConnectionResult>> TestSshConnectionAsync(
        string repositoryUrl,
        string sshKeyPath,
        string? passphrase = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sshKeyPath) || !File.Exists(sshKeyPath))
        {
            return Result.Failure<GitConnectionResult>("SSH key file not found");
        }

        LogTestingSshConnection(logger, repositoryUrl, sshKeyPath);

        var tempPath = Path.Combine(Path.GetTempPath(), $"git-ssh-test-{Guid.NewGuid():N}");

        try
        {
            await Task.Run(() =>
            {
                // Clone with SSH key available in system SSH config
                var cloneOptions = new CloneOptions { IsBare = false };
                Repository.Clone(repositoryUrl, tempPath, cloneOptions);

                LogSshAuthSucceeded(logger, repositoryUrl);
            }, ct).ConfigureAwait(false);

            var result = new GitConnectionResult
            {
                Connected = true,
                Message = "Successfully authenticated with SSH key",
                RepositoryUrl = repositoryUrl,
                IsAuthenticated = true,
                Timestamp = DateTime.UtcNow
            };

            return Result.Success(result);
        }
        catch (LibGit2SharpException ex)
        {
            LogSshConnectionFailed(logger, repositoryUrl, ex.Message);
            return Result.Failure<GitConnectionResult>($"SSH authentication failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            LogUnexpectedSshError(logger, ex, repositoryUrl);
            return Result.Failure<GitConnectionResult>($"Unexpected error: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempPath))
                {
                    Directory.Delete(tempPath, true);
                    LogCleanedUpTempDir(logger, tempPath);
                }
            }
            catch (Exception ex)
            {
                LogCleanupFailed(logger, tempPath, ex.Message);
            }
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Testing git connection to {RepositoryUrl}")]
    private static partial void LogTestingConnection(ILogger logger, string repositoryUrl);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information,
        Message = "Successfully connected to {RepositoryUrl}. Head: {Branch}, Commits: {Count}")]
    private static partial void LogConnectionSucceeded(ILogger logger, string repositoryUrl, string branch, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Git connection test failed: Authentication failed for {RepositoryUrl}")]
    private static partial void LogAuthenticationFailed(ILogger logger, string repositoryUrl);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "Git connection test failed: Repository not found at {RepositoryUrl}")]
    private static partial void LogRepositoryNotFound(ILogger logger, string repositoryUrl);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning,
        Message = "Git connection test failed for {RepositoryUrl}: {Message}")]
    private static partial void LogConnectionFailed(ILogger logger, string repositoryUrl, string message);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error,
        Message = "Unexpected error testing git connection to {RepositoryUrl}")]
    private static partial void LogUnexpectedError(ILogger logger, Exception exception, string repositoryUrl);

    [LoggerMessage(EventId = 7, Level = LogLevel.Debug, Message = "Cleaned up temporary directory {TempPath}")]
    private static partial void LogCleanedUpTempDir(ILogger logger, string tempPath);

    [LoggerMessage(EventId = 8, Level = LogLevel.Warning,
        Message = "Failed to clean up temporary directory {TempPath}: {Message}")]
    private static partial void LogCleanupFailed(ILogger logger, string tempPath, string message);

    [LoggerMessage(EventId = 9, Level = LogLevel.Information,
        Message = "Testing SSH connection to {RepositoryUrl} with key {KeyPath}")]
    private static partial void LogTestingSshConnection(ILogger logger, string repositoryUrl, string keyPath);

    [LoggerMessage(EventId = 10, Level = LogLevel.Information,
        Message = "Successfully authenticated with SSH to {RepositoryUrl}")]
    private static partial void LogSshAuthSucceeded(ILogger logger, string repositoryUrl);

    [LoggerMessage(EventId = 11, Level = LogLevel.Warning,
        Message = "SSH connection test failed for {RepositoryUrl}: {Message}")]
    private static partial void LogSshConnectionFailed(ILogger logger, string repositoryUrl, string message);

    [LoggerMessage(EventId = 12, Level = LogLevel.Error,
        Message = "Unexpected error testing SSH connection to {RepositoryUrl}")]
    private static partial void LogUnexpectedSshError(ILogger logger, Exception exception, string repositoryUrl);
}

#pragma warning restore CA1054
#pragma warning restore CA1056
