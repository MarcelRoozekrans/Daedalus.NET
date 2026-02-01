namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Configuration for a single repository platform
/// </summary>
public sealed class RepositoryPlatformConfiguration
{
    /// <summary>
    ///     SSH host for git operations
    /// </summary>
    public string SshHost { get; set; } = string.Empty;

    /// <summary>
    ///     Whether this platform is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Optional authentication token
    /// </summary>
    public string? AuthToken { get; set; }

    /// <summary>
    ///     Web URL base (e.g., https://github.com)
    /// </summary>
#pragma warning disable CA1056 // URI-type properties should not be strings - used for configuration binding
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    ///     API URL base
    /// </summary>
    public string ApiUrl { get; set; } = string.Empty;
#pragma warning restore CA1056
}
