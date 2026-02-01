using CSharpFunctionalExtensions;

namespace Daedalus.Domain.CodeAnalysis;

#pragma warning disable CA1054, CA1056 // Uri parameters/properties should not be strings

/// <summary>
///     Value object representing a git repository reference.
///     Groups repository URL, branch, commit SHA, and platform detection.
/// </summary>
public sealed class RepositoryReference : ValueObject
{
    public string Url { get; private set; } = string.Empty;
    public string? Branch { get; private set; }
    public string? CommitSha { get; private set; }
    public RepositoryPlatform Platform { get; private set; }

    // Required by EF Core for owned type materialization
    private RepositoryReference() { }

    public static Result<RepositoryReference> Create(string url, string? branch = null, string? commitSha = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Result.Failure<RepositoryReference>("Repository URL cannot be empty");

        return Result.Success(new RepositoryReference
        {
            Url = url.Trim(),
            Branch = branch?.Trim(),
            CommitSha = commitSha?.Trim(),
            Platform = DetectPlatform(url)
        });
    }

    private static RepositoryPlatform DetectPlatform(string url)
    {
        if (url.Contains("github.com", StringComparison.OrdinalIgnoreCase))
            return RepositoryPlatform.GitHub;
        if (url.Contains("dev.azure.com", StringComparison.OrdinalIgnoreCase) ||
            url.Contains("visualstudio.com", StringComparison.OrdinalIgnoreCase))
            return RepositoryPlatform.AzureDevOps;
        if (url.Contains("gitlab.com", StringComparison.OrdinalIgnoreCase))
            return RepositoryPlatform.GitLab;
        if (url.Contains("gitea", StringComparison.OrdinalIgnoreCase))
            return RepositoryPlatform.Gitea;
        return RepositoryPlatform.None;
    }

    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return Url;
        yield return Branch ?? string.Empty;
        yield return CommitSha ?? string.Empty;
        yield return Platform;
    }
}
