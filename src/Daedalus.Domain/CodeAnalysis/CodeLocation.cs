using ZeroAlloc.Results;
using ZeroAlloc.ValueObjects;

namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object representing a code location within a repository.
///     Groups file path with optional line range for targeted analysis.
/// </summary>
[ValueObject]
public sealed partial class CodeLocation
{
    [EqualityMember] public string FilePath { get; private set; } = string.Empty;
    public int? StartLine { get; private set; }
    public int? EndLine { get; private set; }

    // Equality-only normalisation: GetEqualityComponents() used to coalesce a null line number to 0
    // before comparing. These reproduce that exact behaviour for ZeroAlloc's member-based equality.
    // MUST be public: ZeroAlloc.ValueObjects 2.0.7 silently ignores [EqualityMember] on non-public
    // members (verified empirically — see task-9-report.md) rather than erroring, so a private
    // member here would silently drop out of equality instead of narrowing it loudly.
    [EqualityMember] public int StartLineForEquality => StartLine ?? 0;
    [EqualityMember] public int EndLineForEquality => EndLine ?? 0;

    // Required by EF Core for owned type materialization
    private CodeLocation() { }

    public static Result<CodeLocation> Create(string? filePath, int? startLine = null, int? endLine = null)
    {
        if (startLine.HasValue && endLine.HasValue && startLine > endLine)
            return Result<CodeLocation>.Failure("StartLine cannot be greater than EndLine");

        return Result<CodeLocation>.Success(new CodeLocation
        {
            FilePath = filePath?.Trim() ?? string.Empty,
            StartLine = startLine,
            EndLine = endLine
        });
    }
}
