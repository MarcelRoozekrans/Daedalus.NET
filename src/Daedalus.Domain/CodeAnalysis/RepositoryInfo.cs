namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object for repository information
/// </summary>
#pragma warning disable CA1056 // URI properties should not be strings — SshUrl uses git@ format which is not valid System.Uri
public record RepositoryInfo
{
    public RepositoryPlatform Platform { get; init; }
    public string Owner { get; init; } = string.Empty;
    public string Repository { get; init; } = string.Empty;
    public string HttpsUrl { get; init; } = string.Empty;
    public string SshUrl { get; init; } = string.Empty;
    public string WebUrl { get; init; } = string.Empty;
    public string ApiBaseUrl { get; init; } = string.Empty;
}
