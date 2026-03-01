using CSharpFunctionalExtensions;

namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Represents a single iteration of Ralph Loop analysis.
///     This is a child entity owned by CodeAnalysisRequest aggregate root.
/// </summary>
public sealed class AnalysisIteration : Entities.Entity<Guid>
{
    public Guid CodeAnalysisRequestId { get; private set; }
    public int IterationNumber { get; private set; }

    // Git state
    public string? BranchName { get; private set; }
    public string? CommitSha { get; private set; }

    // Content
    public string PromptSent { get; private set; } = string.Empty;
    public string AiResponse { get; private set; } = string.Empty;

    // Validation
    public bool? CompilationSucceeded { get; private set; }
    public bool? TestsPassed { get; private set; }
    public string? ValidationErrors { get; private set; } // JSON

    // Token usage
    public int InputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public string? ModelId { get; private set; }

    // Timestamps
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    /// <summary>
    ///     Factory method to create a new AnalysisIteration with validation.
    /// </summary>
    public static Result<AnalysisIteration> Create(
        Guid id,
        Guid codeAnalysisRequestId,
        int iterationNumber,
        string promptSent,
        string aiResponse,
        string? branchName = null,
        string? commitSha = null,
        int inputTokens = 0,
        int outputTokens = 0,
        string? modelId = null)
    {
        // Validation
        if (codeAnalysisRequestId == Guid.Empty)
        {
            return Result.Failure<AnalysisIteration>("CodeAnalysisRequestId cannot be empty");
        }

        if (iterationNumber < 1)
        {
            return Result.Failure<AnalysisIteration>("Iteration number must be at least 1");
        }

        if (string.IsNullOrWhiteSpace(promptSent))
        {
            return Result.Failure<AnalysisIteration>("Prompt cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(aiResponse))
        {
            return Result.Failure<AnalysisIteration>("AI response cannot be empty");
        }

        return Result.Success(new AnalysisIteration
        {
            Id = id,
            CodeAnalysisRequestId = codeAnalysisRequestId,
            IterationNumber = iterationNumber,
            PromptSent = promptSent.Trim(),
            AiResponse = aiResponse.Trim(),
            BranchName = branchName?.Trim(),
            CommitSha = commitSha?.Trim(),
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ModelId = modelId?.Trim(),
            CreatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    ///     Records compilation and test validation results.
    /// </summary>
    public Result RecordValidationResults(bool? compilationSucceeded, bool? testsPassed,
        string? validationErrors = null)
    {
        CompilationSucceeded = compilationSucceeded;
        TestsPassed = testsPassed;
        ValidationErrors = validationErrors;

        return Result.Success();
    }
}
