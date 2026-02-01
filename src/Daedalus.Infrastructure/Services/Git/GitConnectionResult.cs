#pragma warning disable CA1056 // URI-like properties should not be strings

namespace Daedalus.Infrastructure.Services.Git;

/// <summary>
///     Result of a git connection test.
/// </summary>
public sealed class GitConnectionResult
{
    /// <summary>
    ///     Whether the connection was successful.
    /// </summary>
    public bool Connected { get; init; }

    /// <summary>
    ///     Details about the connection result or error message.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    ///     The repository URL that was tested.
    /// </summary>
    public string RepositoryUrl { get; init; } = string.Empty;

    /// <summary>
    ///     Whether authentication was used successfully.
    /// </summary>
    public bool IsAuthenticated { get; init; }

    /// <summary>
    ///     Timestamp when the test was performed.
    /// </summary>
    public DateTime Timestamp { get; init; }
}
