namespace Daedalus.Infrastructure.Configuration;

/// <summary>
///     Configuration for Context7 documentation service
/// </summary>
public sealed class Context7Configuration
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Base URL of Context7 API
    /// </summary>
#pragma warning disable CA1056 // URI-type properties should not be strings - used for configuration binding
    public string ApiUrl { get; set; } = "https://context7.com/api";
#pragma warning restore CA1056

    /// <summary>
    ///     Request timeout in milliseconds
    /// </summary>
    public int Timeout { get; set; } = 10000;

    /// <summary>
    ///     Optional API key for authentication
    /// </summary>
    public string? ApiKey { get; set; }
}
