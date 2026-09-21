using ZeroAlloc.ValueObjects;

namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object representing the outcome/results of a code analysis.
///     Groups pull request URL, final commit SHA, validation results, and failure indicators.
/// </summary>
#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable CA1056 // Uri properties should not be strings
[ValueObject]
public sealed partial class AnalysisOutcome
{
    public string? PullRequestUrl { get; private set; }
    public string? CommitShaFinal { get; private set; }
    public string? ValidationResult { get; private set; }
    public bool HasFailedValidation { get; private set; }

    // Equality-only members, in the same order as the former GetEqualityComponents():
    // PullRequestUrl, CommitShaFinal, ValidationResult, HasFailedValidation. The first three used
    // to coalesce a null string to string.Empty before comparing; these reproduce that exact
    // behaviour for ZeroAlloc's member-based equality. MUST be public: ZeroAlloc.ValueObjects 2.0.7
    // silently ignores [EqualityMember] on non-public members (verified empirically — see
    // task-9-report.md) rather than erroring, so a private/internal member here would silently drop
    // out of equality instead of narrowing it loudly.
    [EqualityMember] public string PullRequestUrlForEquality => PullRequestUrl ?? string.Empty;
    [EqualityMember] public string CommitShaFinalForEquality => CommitShaFinal ?? string.Empty;
    [EqualityMember] public string ValidationResultForEquality => ValidationResult ?? string.Empty;
    [EqualityMember] public bool HasFailedValidationForEquality => HasFailedValidation;

    // Required by EF Core for owned type materialization
    private AnalysisOutcome() { }

    public static AnalysisOutcome Empty() => new();

    public AnalysisOutcome WithValidation(string validationResult, bool hasFailed)
    {
        return new AnalysisOutcome
        {
            PullRequestUrl = PullRequestUrl,
            CommitShaFinal = CommitShaFinal,
            ValidationResult = validationResult,
            HasFailedValidation = hasFailed
        };
    }

    public AnalysisOutcome WithCompletion(string? prUrl, string? commitSha)
    {
        return new AnalysisOutcome
        {
            PullRequestUrl = prUrl,
            CommitShaFinal = commitSha,
            ValidationResult = ValidationResult,
            HasFailedValidation = HasFailedValidation
        };
    }
}
