namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object for code modification
/// </summary>
public record CodeModification
{
    public string FilePath { get; init; } = string.Empty;
    public int? StartLine { get; init; }
    public int? EndLine { get; init; }
    public string ModifiedCode { get; init; } = string.Empty;
    public string? ChangeDescription { get; init; }
}
