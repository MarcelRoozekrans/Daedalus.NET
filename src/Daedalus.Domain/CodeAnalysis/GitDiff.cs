namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object for git diff
/// </summary>
public record GitDiff
{
    public string FilePath { get; init; } = string.Empty;
    public string OriginalContent { get; init; } = string.Empty;
    public string NewContent { get; init; } = string.Empty;
    public int AddedLines { get; init; }
    public int RemovedLines { get; init; }
}
