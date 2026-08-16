namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object for a git repository file
/// </summary>
public record RepositoryFile
{
    public string FilePath { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public string LastCommitSha { get; init; } = string.Empty;
    public DateTime LastModified { get; init; }
    public string LastAuthor { get; init; } = string.Empty;
}
