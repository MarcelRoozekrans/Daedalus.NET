namespace Daedalus.Web.State.Repositories;

/// <summary>Which write a <see cref="RepositorySave"/> is the outcome of.</summary>
public enum RepositoryWrite
{
    Created,
    Updated,
    Deleted,
    Tested,
}

/// <summary>
///     The outcome of a repository write: saved when <paramref name="Refusal"/> is <see langword="null"/>, refused
///     otherwise, with the client's text: "The server answered {status}." or <see cref="RepositoriesApiClient.NoAnswer"/>.
///     The component that asked shows it, through <see cref="RepositoriesView.Notice"/>; the effect never does.
/// </summary>
public readonly record struct RepositorySave(RepositoryWrite Write, string? Refusal)
{
    /// <summary>Whether the write was saved, or the connection test passed.</summary>
    public bool Succeeded => Refusal is null;

    /// <summary>Whether the server answered at all: a write that got no answer is not a refusal it made.</summary>
    public bool Answered => !string.Equals(Refusal, RepositoriesApiClient.NoAnswer, StringComparison.Ordinal);
}
