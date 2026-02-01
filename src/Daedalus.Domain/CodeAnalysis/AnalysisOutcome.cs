using CSharpFunctionalExtensions;

namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Value object representing the outcome/results of a code analysis.
///     Groups pull request URL, final commit SHA, validation results, and failure indicators.
/// </summary>
#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable CA1056 // Uri properties should not be strings
public sealed class AnalysisOutcome : ValueObject
{
    public string? PullRequestUrl { get; private set; }
    public string? CommitShaFinal { get; private set; }
    public string? ValidationResult { get; private set; }
    public bool HasFailedValidation { get; private set; }

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

    protected override IEnumerable<IComparable> GetEqualityComponents()
    {
        yield return PullRequestUrl ?? string.Empty;
        yield return CommitShaFinal ?? string.Empty;
        yield return ValidationResult ?? string.Empty;
        yield return HasFailedValidation;
    }
}
