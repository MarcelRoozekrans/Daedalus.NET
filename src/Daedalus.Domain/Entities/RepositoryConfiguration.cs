#pragma warning disable CA1819 // Use byte[] instead of property returning array (EF Core concurrency token standard pattern)
#pragma warning disable CA1054 // URI parameters should not be strings
#pragma warning disable CA1056 // URI properties should not be strings
#pragma warning disable S1144 // EF Core sets RowVersion via reflection (unused private setter is required)

using CSharpFunctionalExtensions;

namespace Daedalus.Domain.Entities;

/// <summary>
///     Aggregate root representing a git repository configuration
///     for code analysis and Ralph Loop processing.
/// </summary>
public sealed class RepositoryConfiguration : AggregateRoot<Guid>
{
    private static readonly string[] ValidPlatforms =
        ["GitHub", "GitLab", "AzureDevOps", "Bitbucket", "Gitea"];

    /// <summary>Gets the human-readable repository name.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Gets the repository URL.</summary>
    public string Url { get; private set; } = string.Empty;

    /// <summary>Gets the hosting platform (GitHub, GitLab, AzureDevOps, Bitbucket, Gitea).</summary>
    public string Platform { get; private set; } = "GitHub";

    /// <summary>Gets the default branch name.</summary>
    public string DefaultBranch { get; private set; } = "main";

    /// <summary>Gets the authentication method (e.g., PAT, SSH, OAuth).</summary>
    public string? AuthenticationMethod { get; private set; }

    /// <summary>Gets the credential identifier for resolving secrets.</summary>
    public string? CredentialIdentifier { get; private set; }

    /// <summary>Gets whether this repository configuration is active.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>Gets the optional description.</summary>
    public string? Description { get; private set; }

    /// <summary>Gets when the configuration was created.</summary>
    public DateTime CreatedAt { get; private set; }

    /// <summary>Gets when the configuration was last modified.</summary>
    public DateTime? ModifiedAt { get; private set; }

    /// <summary>Gets when the repository was last used for an operation.</summary>
    public DateTime? LastUsedAt { get; private set; }

    /// <summary>
    ///     Gets the concurrency token (row version) for optimistic locking.
    ///     Prevents lost updates in concurrent scenarios.
    /// </summary>
    public byte[]? RowVersion { get; private set; }

    /// <summary>
    ///     Factory method to create a new RepositoryConfiguration with validation.
    /// </summary>
    public static Result<RepositoryConfiguration> Create(
        Guid id,
        string name,
        string url,
        string platform = "GitHub",
        string defaultBranch = "main",
        string? authenticationMethod = null,
        string? credentialIdentifier = null,
        string? description = null,
        DateTime? createdAt = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure<RepositoryConfiguration>("Name cannot be empty");
        }

        if (name.Length > 256)
        {
            return Result.Failure<RepositoryConfiguration>("Name cannot exceed 256 characters");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return Result.Failure<RepositoryConfiguration>("URL cannot be empty");
        }

        if (url.Length > 1024)
        {
            return Result.Failure<RepositoryConfiguration>("URL cannot exceed 1024 characters");
        }

        if (string.IsNullOrWhiteSpace(platform))
        {
            return Result.Failure<RepositoryConfiguration>("Platform cannot be empty");
        }

        if (!Array.Exists(ValidPlatforms, p => string.Equals(p, platform, StringComparison.OrdinalIgnoreCase)))
        {
            return Result.Failure<RepositoryConfiguration>(
                $"Platform must be one of: {string.Join(", ", ValidPlatforms)}");
        }

        if (description is { Length: > 2000 })
        {
            return Result.Failure<RepositoryConfiguration>("Description cannot exceed 2000 characters");
        }

        return Result.Success(new RepositoryConfiguration
        {
            Id = id,
            Name = name.Trim(),
            Url = url.Trim(),
            Platform = platform,
            DefaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch.Trim(),
            AuthenticationMethod = authenticationMethod?.Trim(),
            CredentialIdentifier = credentialIdentifier?.Trim(),
            Description = description?.Trim(),
            IsActive = true,
            CreatedAt = createdAt ?? DateTime.UtcNow
        });
    }

    /// <summary>
    ///     Updates the repository configuration metadata.
    /// </summary>
    public Result Update(
        string name,
        string url,
        string defaultBranch,
        string? authenticationMethod,
        string? credentialIdentifier,
        bool isActive,
        string? description)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Result.Failure("Name cannot be empty");
        }

        if (name.Length > 256)
        {
            return Result.Failure("Name cannot exceed 256 characters");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return Result.Failure("URL cannot be empty");
        }

        if (url.Length > 1024)
        {
            return Result.Failure("URL cannot exceed 1024 characters");
        }

        if (description is { Length: > 2000 })
        {
            return Result.Failure("Description cannot exceed 2000 characters");
        }

        Name = name.Trim();
        Url = url.Trim();
        DefaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch.Trim();
        AuthenticationMethod = authenticationMethod?.Trim();
        CredentialIdentifier = credentialIdentifier?.Trim();
        IsActive = isActive;
        Description = description?.Trim();
        ModifiedAt = DateTime.UtcNow;

        return Result.Success();
    }

    /// <summary>
    ///     Marks the repository as having been used for an operation.
    /// </summary>
    public void RecordUsage()
    {
        LastUsedAt = DateTime.UtcNow;
    }

    /// <summary>
    ///     Deactivates the repository configuration.
    /// </summary>
    public Result Deactivate()
    {
        if (!IsActive)
        {
            return Result.Failure("Repository is already inactive");
        }

        IsActive = false;
        ModifiedAt = DateTime.UtcNow;
        return Result.Success();
    }
}

#pragma warning restore S1144
#pragma warning restore CA1819
