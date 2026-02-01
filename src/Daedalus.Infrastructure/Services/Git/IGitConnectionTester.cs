#pragma warning disable CA1054 // URI-like parameters should not be strings

using CSharpFunctionalExtensions;

namespace Daedalus.Infrastructure.Services.Git;

/// <summary>
///     Interface for testing git repository connections.
/// </summary>
public interface IGitConnectionTester
{
    /// <summary>
    ///     Tests connection to a git repository URL with optional credentials.
    /// </summary>
    Task<Result<GitConnectionResult>> TestConnectionAsync(
        string repositoryUrl,
        string? authenticationMethod = null,
        (string username, string password)? credentials = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Tests SSH key authentication to a repository.
    /// </summary>
    Task<Result<GitConnectionResult>> TestSshConnectionAsync(
        string repositoryUrl,
        string sshKeyPath,
        string? passphrase = null,
        CancellationToken ct = default);
}
