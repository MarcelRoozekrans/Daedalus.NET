using CSharpFunctionalExtensions;

namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object representing a code location within a repository.
///     Groups file path with optional line range for targeted analysis.
/// </summary>
public sealed class CodeLocation : ValueObject
{
    public string FilePath { get; private set; } = string.Empty;
    public int? StartLine { get; private set; }
    public int? EndLine { get; private set; }

    // Required by EF Core for owned type materialization
    private CodeLocation() { }

    public static Result<CodeLocation> Create(string? filePath, int? startLine = null, int? endLine = null)
    {
        if (startLine.HasValue && endLine.HasValue && startLine > endLine)
            return Result.Failure<CodeLocation>("StartLine cannot be greater than EndLine");

        return Result.Success(new CodeLocation
        {
            FilePath = filePath?.Trim() ?? string.Empty,
            StartLine = startLine,
            EndLine = endLine
        });
    }

    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return FilePath;
        yield return StartLine ?? 0;
        yield return EndLine ?? 0;
    }
}
