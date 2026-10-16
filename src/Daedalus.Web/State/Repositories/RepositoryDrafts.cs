namespace Daedalus.Web.State.Repositories;

/// <summary>
///     The copies the repository form edits, and the requests built from them. The form binds to a mutable DTO, so it
///     never gets the state's own: a cancelled edit leaves the card as it was.
/// </summary>
public static class RepositoryDrafts
{
    /// <summary>A new repository's draft: on GitHub and main, with an id of its own, as the Add form always started.</summary>
    public static RepositoryConfigurationDto New() =>
        new() { Id = Guid.NewGuid(), Platform = "GitHub", DefaultBranch = "main" };

    /// <summary>A copy of <paramref name="repository"/>, from the state, for the Edit form.</summary>
    public static RepositoryConfigurationDto For(RepositoryConfigurationDto repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return repository with { };
    }

    /// <summary>The create request for <paramref name="draft"/>.</summary>
    public static CreateRepositoryConfigurationDto ToCreate(RepositoryConfigurationDto draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new CreateRepositoryConfigurationDto
        {
            Name = draft.Name,
            Url = draft.Url,
            Platform = draft.Platform,
            DefaultBranch = draft.DefaultBranch,
            AuthenticationMethod = draft.AuthenticationMethod,
            CredentialIdentifier = draft.CredentialIdentifier,
            Description = draft.Description,
        };
    }

    /// <summary>The update request for <paramref name="draft"/>.</summary>
    public static UpdateRepositoryConfigurationDto ToUpdate(RepositoryConfigurationDto draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new UpdateRepositoryConfigurationDto
        {
            Name = draft.Name,
            Url = draft.Url,
            DefaultBranch = draft.DefaultBranch,
            AuthenticationMethod = draft.AuthenticationMethod,
            CredentialIdentifier = draft.CredentialIdentifier,
            IsActive = draft.IsActive,
            Description = draft.Description,
        };
    }
}
