namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object for git operation context
/// </summary>
#pragma warning disable CA1056 // URI properties should not be strings — used as data carrier for git URLs
public record GitOperationContext
{
    public string RepositoryUrl { get; init; } = string.Empty;
    public string LocalWorkTreePath { get; init; } = string.Empty;
    public string FeatureBranchName { get; init; } = string.Empty;
    public string BaseBranch { get; init; } = string.Empty;
    public string CurrentCommit { get; init; } = string.Empty;
    public string CurrentBranch { get; init; } = string.Empty;
    public bool HasUncommittedChanges { get; init; }
}
