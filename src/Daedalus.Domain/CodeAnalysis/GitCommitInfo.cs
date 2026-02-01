namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object for git commit information
/// </summary>
public record GitCommitInfo
{
    public string CommitSha { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<string> FilesChanged { get; init; } = Array.Empty<string>();
}
