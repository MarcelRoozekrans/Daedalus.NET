using ZeroAlloc.Results;

namespace Daedalus.Domain.CodeAnalysis;

/// <summary>
///     Represents a single iteration of iterative code analysis.
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

    private AnalysisIteration() { }

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
            return Result<AnalysisIteration>.Failure("CodeAnalysisRequestId cannot be empty");
        }

        if (iterationNumber < 1)
        {
            return Result<AnalysisIteration>.Failure("Iteration number must be at least 1");
        }

        if (string.IsNullOrWhiteSpace(promptSent))
        {
            return Result<AnalysisIteration>.Failure("Prompt cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(aiResponse))
        {
            return Result<AnalysisIteration>.Failure("AI response cannot be empty");
        }

        return Result<AnalysisIteration>.Success(new AnalysisIteration
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
    ///     Rebuilds a stored iteration, for persistence only: every value is taken as stored and nothing is validated.
    /// </summary>
    /// <param name="id">The iteration id.</param>
    /// <param name="codeAnalysisRequestId">The parent request id.</param>
    /// <param name="iterationNumber">The iteration number.</param>
    /// <param name="branchName">The branch name, or null.</param>
    /// <param name="commitSha">The commit SHA, or null.</param>
    /// <param name="promptSent">The prompt sent.</param>
    /// <param name="aiResponse">The AI response.</param>
    /// <param name="compilationSucceeded">Whether compilation succeeded, or null.</param>
    /// <param name="testsPassed">Whether the tests passed, or null.</param>
    /// <param name="validationErrors">The validation errors JSON document, or null.</param>
    /// <param name="inputTokens">The input tokens used.</param>
    /// <param name="outputTokens">The output tokens used.</param>
    /// <param name="modelId">The model id, or null.</param>
    /// <param name="createdAt">When the iteration was recorded.</param>
    /// <returns>The iteration as stored.</returns>
    public static AnalysisIteration Rehydrate(
        Guid id,
        Guid codeAnalysisRequestId,
        int iterationNumber,
        string? branchName,
        string? commitSha,
        string promptSent,
        string aiResponse,
        bool? compilationSucceeded,
        bool? testsPassed,
        string? validationErrors,
        int inputTokens,
        int outputTokens,
        string? modelId,
        DateTime createdAt) =>
        new()
        {
            Id = id,
            CodeAnalysisRequestId = codeAnalysisRequestId,
            IterationNumber = iterationNumber,
            BranchName = branchName,
            CommitSha = commitSha,
            PromptSent = promptSent,
            AiResponse = aiResponse,
            CompilationSucceeded = compilationSucceeded,
            TestsPassed = testsPassed,
            ValidationErrors = validationErrors,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ModelId = modelId,
            CreatedAt = createdAt
        };

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
