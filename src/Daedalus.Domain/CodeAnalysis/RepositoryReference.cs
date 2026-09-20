using ZeroAlloc.Results;
using ZeroAlloc.ValueObjects;

namespace Daedalus.Domain.CodeAnalysis;

#pragma warning disable CA1054, CA1056 // Uri parameters/properties should not be strings

/// <summary>
///     Value object representing a git repository reference.
///     Groups repository URL, branch, commit SHA, and platform detection.
/// </summary>
[ValueObject]
public sealed partial class RepositoryReference
{
    [EqualityMember] public string Url { get; private set; } = string.Empty;
    public string? Branch { get; private set; }
    public string? CommitSha { get; private set; }

    // Equality-only normalisation, in the same order as the former GetEqualityComponents():
    // Url, Branch, CommitSha, Platform. Branch and CommitSha used to coalesce a null string to
    // string.Empty before comparing; these reproduce that exact behaviour for ZeroAlloc's
    // member-based equality. MUST be public: ZeroAlloc.ValueObjects 2.0.7 silently ignores
    // [EqualityMember] on non-public members (verified empirically — see task-9-report.md) rather
    // than erroring, so a private member here would silently drop out of equality instead of
    // narrowing it loudly.
    [EqualityMember] public string BranchForEquality => Branch ?? string.Empty;
    [EqualityMember] public string CommitShaForEquality => CommitSha ?? string.Empty;

    [EqualityMember] public RepositoryPlatform Platform { get; private set; }

    // Required by EF Core for owned type materialization
    private RepositoryReference() { }

    public static Result<RepositoryReference> Create(string url, string? branch = null, string? commitSha = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return Result<RepositoryReference>.Failure("Repository URL cannot be empty");

        return Result<RepositoryReference>.Success(new RepositoryReference
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
}
