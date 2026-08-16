namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Configuration for repository platforms
/// </summary>
public sealed class RepositoryPlatformsConfiguration
{
    /// <summary>
    ///     GitHub platform configuration
    /// </summary>
    public RepositoryPlatformConfiguration GitHub { get; set; } = new()
    {
#pragma warning disable S1075 // Public platform URLs, intentional defaults
        BaseUrl = "https://github.com",
        ApiUrl = "https://api.github.com",
#pragma warning restore S1075
        SshHost = "git@github.com",
        Enabled = true
    };

    /// <summary>
    ///     GitLab platform configuration
    /// </summary>
    public RepositoryPlatformConfiguration GitLab { get; set; } = new()
    {
#pragma warning disable S1075 // Public platform URLs, intentional defaults
        BaseUrl = "https://gitlab.com",
        ApiUrl = "https://gitlab.com/api/v4",
#pragma warning restore S1075
        SshHost = "git@gitlab.com",
        Enabled = true
    };

    /// <summary>
    ///     Azure DevOps platform configuration
    /// </summary>
    public RepositoryPlatformConfiguration AzureDevOps { get; set; } = new()
    {
#pragma warning disable S1075 // Public platform URLs, intentional defaults
        BaseUrl = "https://dev.azure.com",
        ApiUrl = "https://dev.azure.com",
#pragma warning restore S1075
        SshHost = "git@ssh.dev.azure.com",
        Enabled = true
    };

    /// <summary>
    ///     Custom repository platforms
    /// </summary>
#pragma warning disable CA2227 // Collection properties should be read-only - configuration pattern requires settable
#pragma warning disable MA0016 // Prefer using collection abstraction instead of implementation - configuration pattern requires Dictionary
    public Dictionary<string, RepositoryPlatformConfiguration> Custom { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore MA0016
#pragma warning restore CA2227
}
