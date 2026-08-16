namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Enumeration for repository platform types (GitHub, Azure DevOps, etc.)
/// </summary>
public enum RepositoryPlatform
{
    None = 0,
    GitHub = 1,
    AzureDevOps = 2,
    Gitea = 3,
    GitLab = 4
}
