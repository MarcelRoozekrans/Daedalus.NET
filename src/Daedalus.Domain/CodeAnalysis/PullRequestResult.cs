namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object for pull request result
/// </summary>
#pragma warning disable CA1056 // URI properties should not be strings — values originate from JSON API responses
public record PullRequestResult
{
    public string PullRequestId { get; init; } = string.Empty;
    public string PullRequestUrl { get; init; } = string.Empty;
    public string WebUrl { get; init; } = string.Empty;
    public PullRequestStatus Status { get; init; }
}
