#pragma warning disable CA1819 // Use byte[] instead of property returning array (EF Core concurrency token standard pattern)

using CSharpFunctionalExtensions;
using Daedalus.Domain.Entities;

namespace Daedalus.Domain.CodeAnalysis;

#pragma warning disable CA1056 // Uri properties should not be strings
#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable S1144 // Unused private setters

/// <summary>
///     Represents a code analysis request for Ralph Loop processing.
///     This is an aggregate root that owns AnalysisIteration entities and manages the analysis lifecycle.
/// </summary>
public sealed class CodeAnalysisRequest : AggregateRoot<Guid>
{
    private readonly List<AnalysisIteration> _iterations = [];

    // Request Metadata
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;

    // Git Repository Configuration (Value Object)
    public RepositoryReference Repository { get; private set; } = null!;

    // Code Location in Repository (Value Object)
    public CodeLocation Location { get; private set; } = null!;

    // Analysis Configuration
    public AnalysisType Type { get; private set; }
    public int Priority { get; private set; }

    // Requirements and References
    public string Requirements { get; private set; } = string.Empty; // JSON array
    public string? ReferencedDocuments { get; private set; } // JSON array
    public string? ExternalReferences { get; private set; } // JSON array

    // Git Operations Tracking
    public string? LocalWorkTreePath { get; private set; }
    public string? FeatureBranchName { get; private set; }

    // Status & Progress
    public AnalysisStatus Status { get; private set; }
    public int CurrentIteration { get; private set; }
    public int MaxIterations { get; private set; }
    public string? CompletionPromise { get; private set; }

    // Ralph Loop Content
    public string? LastPromptSent { get; private set; }
    public string? LastAiResponse { get; private set; }

    // Results & Artifacts (Value Object)
    public AnalysisOutcome Outcome { get; private set; } = null!;

    // Timestamps & Tracking
    public DateTime CreatedAt { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    public string? FailureReason { get; private set; }

    // Audit
    public Guid? CreatedBy { get; private set; }
    public Guid? AssignedTo { get; private set; }

    // Concurrency
    public byte[]? RowVersion { get; private set; }

    // Navigation - read-only collection
    public IReadOnlyList<AnalysisIteration> Iterations => _iterations.AsReadOnly();

    // Backward-compatible convenience properties (read-through to value objects)
    public string RepositoryUrl => Repository.Url;
    public string? RepositoryBranch => Repository.Branch;
    public string? CommitSha => Repository.CommitSha;
    public RepositoryPlatform Platform => Repository.Platform;
    public string FilePath => Location.FilePath;
    public int? StartLine => Location.StartLine;
    public int? EndLine => Location.EndLine;
    public string? PullRequestUrl => Outcome.PullRequestUrl;
    public string? CommitShaFinal => Outcome.CommitShaFinal;
    public string? ValidationResult => Outcome.ValidationResult;
    public bool HasFailedValidation => Outcome.HasFailedValidation;

    /// <summary>
    ///     Factory method to create a new CodeAnalysisRequest with validation.
    /// </summary>
    public static Result<CodeAnalysisRequest> Create(
        Guid id,
        string title,
        string description,
        string repositoryUrl,
        int maxIterations,
        AnalysisType analysisType = AnalysisType.CodeReview,
        string? filePath = null,
        string? requirements = null,
        string? repositoryBranch = null,
        string? commitSha = null,
        string? analysisGoals = null)
    {
        // Validation
        if (string.IsNullOrWhiteSpace(title))
        {
            return Result.Failure<CodeAnalysisRequest>("Title cannot be empty");
        }

        if (title.Length > 256)
        {
            return Result.Failure<CodeAnalysisRequest>("Title cannot exceed 256 characters");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return Result.Failure<CodeAnalysisRequest>("Description cannot be empty");
        }

        if (description.Length > 2000)
        {
            return Result.Failure<CodeAnalysisRequest>("Description cannot exceed 2000 characters");
        }

        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            return Result.Failure<CodeAnalysisRequest>("Repository URL cannot be empty");
        }

        if (maxIterations < 1 || maxIterations > 100)
        {
            return Result.Failure<CodeAnalysisRequest>("Max iterations must be between 1 and 100");
        }

        // Create value objects
        var repoResult = RepositoryReference.Create(repositoryUrl, repositoryBranch, commitSha);
        if (repoResult.IsFailure)
        {
            return Result.Failure<CodeAnalysisRequest>(repoResult.Error);
        }

        var locationResult = CodeLocation.Create(filePath);
        if (locationResult.IsFailure)
        {
            return Result.Failure<CodeAnalysisRequest>(locationResult.Error);
        }

        return Result.Success(new CodeAnalysisRequest
        {
            Id = id,
            Title = title.Trim(),
            Description = description.Trim(),
            Repository = repoResult.Value,
            Location = locationResult.Value,
            Outcome = AnalysisOutcome.Empty(),
            MaxIterations = maxIterations,
            Type = analysisType,
            Status = AnalysisStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            CurrentIteration = 0,
            CompletionPromise = analysisGoals,
            Requirements = requirements?.Trim() ?? string.Empty
        });
    }

    /// <summary>
    ///     Initializes the repository with working tree and feature branch information.
    /// </summary>
    public Result InitializeRepository(string workTreePath, string featureBranch)
    {
        if (Status != AnalysisStatus.Pending && Status != AnalysisStatus.ClonePending &&
            Status != AnalysisStatus.CloneInProgress)
        {
            return Result.Failure($"Cannot initialize repository when status is {Status}");
        }

        if (string.IsNullOrWhiteSpace(workTreePath))
        {
            return Result.Failure("Work tree path cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(featureBranch))
        {
            return Result.Failure("Feature branch name cannot be empty");
        }

        LocalWorkTreePath = workTreePath.Trim();
        FeatureBranchName = featureBranch.Trim();
        Status = AnalysisStatus.Ready;

        return Result.Success();
    }

    /// <summary>
    ///     Updates the work tree path for the repository.
    /// </summary>
    public Result SetWorkTreePath(string workTreePath)
    {
        if (string.IsNullOrWhiteSpace(workTreePath))
        {
            return Result.Failure("Work tree path cannot be empty");
        }

        LocalWorkTreePath = workTreePath.Trim();
        return Result.Success();
    }

    /// <summary>
    ///     Records the last prompt and response from the AI.
    /// </summary>
    public Result RecordPromptAndResponse(string prompt, string response)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Result.Failure("Prompt cannot be empty");
        }

        if (string.IsNullOrWhiteSpace(response))
        {
            return Result.Failure("Response cannot be empty");
        }

        LastPromptSent = prompt.Trim();
        LastAiResponse = response.Trim();
        return Result.Success();
    }

    /// <summary>
    ///     Starts the analysis process, transitioning from Ready to AnalysisInProgress.
    /// </summary>
    public Result StartAnalysis()
    {
        if (Status != AnalysisStatus.Ready)
        {
            return Result.Failure($"Cannot start analysis when status is {Status}. Expected: Ready");
        }

        Status = AnalysisStatus.AnalysisInProgress;
        StartedAt = DateTime.UtcNow;

        return Result.Success();
    }

    /// <summary>
    ///     Records a new analysis iteration.
    /// </summary>
    public Result RecordIteration(AnalysisIteration iteration)
    {
        if (Status != AnalysisStatus.AnalysisInProgress)
        {
            return Result.Failure($"Cannot record iteration when status is {Status}. Expected: AnalysisInProgress");
        }

        if (CurrentIteration >= MaxIterations)
        {
            return Result.Failure($"Cannot record iteration: max iterations ({MaxIterations}) reached");
        }

        if (iteration == null)
        {
            return Result.Failure("Iteration cannot be null");
        }

        _iterations.Add(iteration);
        CurrentIteration++;

        return Result.Success();
    }

    /// <summary>
    ///     Records validation results for the current analysis.
    /// </summary>
    public Result RecordValidation(string validationResult, bool hasFailedValidation)
    {
        if (Status != AnalysisStatus.AnalysisInProgress)
        {
            return Result.Failure($"Cannot record validation when status is {Status}");
        }

        Outcome = Outcome.WithValidation(validationResult, hasFailedValidation);
        Status = hasFailedValidation ? AnalysisStatus.ValidationFailed : AnalysisStatus.AwaitingValidation;

        return Result.Success();
    }

    /// <summary>
    ///     Marks the analysis as completed successfully.
    /// </summary>
    public Result Complete(string? prUrl = null, string? commitSha = null)
    {
        if (Status != AnalysisStatus.AnalysisInProgress && Status != AnalysisStatus.AwaitingValidation)
        {
            return Result.Failure($"Cannot complete analysis when status is {Status}");
        }

        if (Status == AnalysisStatus.AwaitingValidation && HasFailedValidation)
        {
            return Result.Failure("Cannot complete analysis with failed validation");
        }

        Outcome = Outcome.WithCompletion(prUrl, commitSha);
        Status = AnalysisStatus.Completed;
        CompletedAt = DateTime.UtcNow;

        return Result.Success();
    }

    /// <summary>
    ///     Marks the analysis as failed.
    /// </summary>
    public Result Fail(string reason)
    {
        if (Status == AnalysisStatus.Completed || Status == AnalysisStatus.Failed || Status == AnalysisStatus.Cancelled)
        {
            return Result.Failure($"Cannot fail analysis when status is {Status}");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return Result.Failure("Failure reason cannot be empty");
        }

        FailureReason = reason.Trim();
        Status = AnalysisStatus.Failed;
        CompletedAt = DateTime.UtcNow;

        return Result.Success();
    }

    /// <summary>
    ///     Cancels the analysis.
    /// </summary>
    public Result Cancel()
    {
        if (Status == AnalysisStatus.Completed || Status == AnalysisStatus.Failed || Status == AnalysisStatus.Cancelled)
        {
            return Result.Failure($"Cannot cancel analysis when status is {Status}");
        }

        Status = AnalysisStatus.Cancelled;
        CancelledAt = DateTime.UtcNow;

        return Result.Success();
    }
}

#pragma warning restore CA1819
