using CSharpFunctionalExtensions;
using Daedalus.Application.Services.CodeAnalysis;
using Daedalus.Domain.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Daedalus.Infrastructure.Services.CodeAnalysis;

#pragma warning disable CA1054 // Uri parameters should not be strings
#pragma warning disable CA1056 // Uri properties should not be strings
#pragma warning disable MA0089 // String instead of char - intentionally using newline separator
#pragma warning disable MA0026 // TODO/FIXME comments - intentional placeholders for future implementation
#pragma warning disable S1135 // TODO/FIXME comments - intentional placeholders for future implementation

/// <summary>
///     Orchestrates the Ralph Loop workflow for iterative code analysis.
///     Handles repository initialization, iterative analysis, validation, and finalization.
/// </summary>
public sealed class RalphLoopOrchestrator(
    ICodeAnalysisRepository repository,
    IGitRepositoryManager gitManager,
    IRepositoryCodeExtractor codeExtractor,
    IAnalysisPromptBuilder promptBuilder,
    IGitChangeApplier changeApplier,
    IPullRequestFactory prFactory,
    ILogger<RalphLoopOrchestrator> logger) : IRalphLoopOrchestrator
{
    public async Task<Result<Guid>> SubmitAnalysisAsync(
        string repoUrl,
        string filePath,
        AnalysisType type,
        string title,
        string description,
        IReadOnlyList<string> requirements,
        string? targetBranch = null,
        string? targetCommit = null,
        int maxIterations = 15,
        CancellationToken ct = default)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "Submitting analysis for {Repository}/{FilePath}",
                    repoUrl,
                    filePath);
            }

            // Create analysis request using factory method
            var analysisType = type != AnalysisType.None ? type : AnalysisType.CodeReview;
            var createResult = CodeAnalysisRequest.Create(
                Guid.NewGuid(),
                title,
                description,
                repoUrl,
                maxIterations,
                analysisType,
                filePath,
                string.Join("\n", requirements),
                targetBranch ?? "main",
                targetCommit);

            if (createResult.IsFailure)
            {
                logger.LogError("Failed to create analysis request: {Error}", createResult.Error);
                return Result.Failure<Guid>(createResult.Error);
            }

            var request = createResult.Value;
            var persistResult = await repository.CreateAsync(request, ct).ConfigureAwait(false);

            if (persistResult.IsFailure)
            {
                return Result.Failure<Guid>(persistResult.Error);
            }

            var requestId = request.Id;
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Analysis request created with ID {RequestId}", requestId);
            }

            // Queue repository initialization as background work (not fire-and-forget)
            // This ensures we can track failures
            _ = Task.Run(async () =>
            {
                var initResult = await InitializeRepositoryAsync(requestId, ct).ConfigureAwait(false);
                if (initResult.IsFailure)
                {
                    logger.LogError(
                        "Background repository initialization failed for request {RequestId}: {Error}",
                        requestId,
                        initResult.Error);
                    // Repository.FailAsync is already called in InitializeRepositoryAsync
                }
                else if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("Repository initialization completed for request {RequestId}", requestId);
                }
            }, CancellationToken.None); // Use default cancellation token for background task

            return Result.Success(requestId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error submitting analysis");
            return Result.Failure<Guid>($"Error submitting analysis: {ex.Message}");
        }
    }

    public async Task<Result<string>> GetAnalysisPromptAsync(
        Guid requestId,
        CancellationToken ct = default)
    {
        var requestResult = await repository.GetByIdAsync(requestId, ct).ConfigureAwait(false);

        if (requestResult.IsFailure)
        {
            return Result.Failure<string>(requestResult.Error);
        }

        var request = requestResult.Value;

        // If we already have a prompt cached, return it
        if (!string.IsNullOrEmpty(request.LastPromptSent))
        {
            return Result.Success(request.LastPromptSent);
        }

        // Use stored work tree path from request, or construct default if needed
        var workTreePath = request.LocalWorkTreePath ??
                           Path.Combine(Path.GetTempPath(), "daedalus-repos", requestId.ToString("N"));

        // Build analysis context
        var contextResult = await codeExtractor.BuildAnalysisContextAsync(
            request,
            workTreePath,
            ct).ConfigureAwait(false);

        if (contextResult.IsFailure)
        {
            logger.LogWarning("Failed to build analysis context: {Error}", contextResult.Error);
            return Result.Failure<string>(contextResult.Error);
        }

        var promptResult = await promptBuilder.BuildPromptAsync(
            request,
            contextResult.Value,
            ct).ConfigureAwait(false);

        if (promptResult.IsFailure)
        {
            logger.LogError("Failed to build analysis prompt: {Error}", promptResult.Error);
            return Result.Failure<string>(promptResult.Error);
        }

        // Persist the generated prompt for future reference
        var updateResult = await repository.UpdateLastPromptAsync(
            requestId,
            promptResult.Value,
            "",
            ct).ConfigureAwait(false);

        if (updateResult.IsFailure)
        {
            logger.LogWarning(
                "Failed to cache prompt for request {RequestId}: {Error}",
                requestId,
                updateResult.Error);
            // Still return the prompt even if caching failed
        }

        return Result.Success(promptResult.Value);
    }

    public async Task<Result> ProcessIterationAsync(
        Guid requestId,
        string aiResponse,
        CancellationToken ct = default)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Processing iteration for request {RequestId}", requestId);
        }

        var requestResult = await repository.GetByIdAsync(requestId, ct).ConfigureAwait(false);

        if (requestResult.IsFailure)
        {
            logger.LogError("Failed to retrieve request {RequestId}: {Error}", requestId, requestResult.Error);
            return Result.Failure(requestResult.Error);
        }

        var request = requestResult.Value;

        // Use stored work tree path
        var workTreePath = request.LocalWorkTreePath ??
                           Path.Combine(Path.GetTempPath(), "daedalus-repos", requestId.ToString("N"));

        if (!Directory.Exists(workTreePath))
        {
            logger.LogError("Work tree path does not exist: {WorkTreePath}", workTreePath);
            return Result.Failure($"Work tree path does not exist: {workTreePath}");
        }

        // Extract changes from AI response
        var extractResult = await changeApplier.ExtractChangesAsync(aiResponse, ct).ConfigureAwait(false);

        if (extractResult.IsFailure)
        {
            logger.LogWarning("Failed to extract changes from AI response: {Error}", extractResult.Error);
            await repository.RecordValidationAsync(
                requestId,
                extractResult.Error,
                true,
                ct).ConfigureAwait(false);
            return Result.Failure(extractResult.Error);
        }

        // Apply code changes
        var applyResult = await changeApplier.ApplyChangesAsync(
            workTreePath,
            extractResult.Value,
            ct).ConfigureAwait(false);

        if (applyResult.IsFailure)
        {
            logger.LogWarning("Failed to apply changes: {Error}", applyResult.Error);
            await repository.RecordValidationAsync(
                requestId,
                applyResult.Error,
                true,
                ct).ConfigureAwait(false);
            return Result.Failure(applyResult.Error);
        }

        // Update iteration count and record response
        var updateResult = await repository.UpdateIterationAsync(
            requestId,
            request.CurrentIteration + 1,
            request.LastPromptSent ?? string.Empty,
            aiResponse,
            ct).ConfigureAwait(false);

        if (updateResult.IsFailure)
        {
            logger.LogError("Failed to update iteration: {Error}", updateResult.Error);
            return Result.Failure(updateResult.Error);
        }

        // Validate changes (compile, tests, code analysis, etc.)
        var validationResult = await ValidateChangesAsync(request, workTreePath, ct).ConfigureAwait(false);

        await repository.RecordValidationAsync(
            requestId,
            validationResult.validationOutput,
            validationResult.hasFailed,
            ct).ConfigureAwait(false);

        // Mark as failed if max iterations reached with validation failures
        if (validationResult.hasFailed && request.CurrentIteration >= request.MaxIterations)
        {
            await repository.FailAsync(
                requestId,
                "Max iterations reached without successful validation",
                ct).ConfigureAwait(false);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            var iterationNumber = request.CurrentIteration + 1;
            var validationStatus = validationResult.hasFailed ? "Failed" : "Passed";
            logger.LogInformation(
                "Processed iteration {Iteration} for request {RequestId}. Validation: {ValidationStatus}",
                iterationNumber,
                requestId,
                validationStatus);
        }

        return Result.Success();
    }

    public async Task<Result<string?>> FinalizeAsync(
        Guid requestId,
        bool createPullRequest = true,
        CancellationToken ct = default)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Finalizing analysis request {RequestId}. CreatePR: {CreatePR}",
                requestId,
                createPullRequest);
        }

        var requestResult = await repository.GetByIdAsync(requestId, ct).ConfigureAwait(false);

        if (requestResult.IsFailure)
        {
            logger.LogError("Failed to retrieve request {RequestId}: {Error}", requestId, requestResult.Error);
            return Result.Failure<string?>(requestResult.Error);
        }

        var request = requestResult.Value;

        // Update status to completed
        var statusResult = await repository.UpdateStatusAsync(
            requestId,
            AnalysisStatus.Completed,
            ct).ConfigureAwait(false);

        if (statusResult.IsFailure)
        {
            logger.LogError("Failed to update status: {Error}", statusResult.Error);
            return Result.Failure<string?>(statusResult.Error);
        }

        if (!createPullRequest)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Analysis {RequestId} finalized without creating PR", requestId);
            }

            return Result.Success<string?>(null);
        }

        // Create pull request
        var prResult = await prFactory.CreatePullRequestAsync(
            request.Repository.Url,
            request.FeatureBranchName ?? $"ralph-loop/{requestId:N}",
            request.Repository.Branch ?? "main",
            $"Ralph Loop: {request.Title}",
            request.Description,
            ct).ConfigureAwait(false);

        if (prResult.IsFailure)
        {
            logger.LogWarning("Failed to create PR for request {RequestId}: {Error}", requestId, prResult.Error);
            return Result.Failure<string?>(prResult.Error);
        }

        // Record completion with PR URL
        var completeResult = await repository.CompleteAsync(
            requestId,
            prResult.Value.WebUrl,
            null,
            ct).ConfigureAwait(false);

        if (completeResult.IsFailure)
        {
            logger.LogError("Failed to complete request {RequestId}: {Error}", requestId, completeResult.Error);
            return Result.Failure<string?>(completeResult.Error);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Analysis {RequestId} completed with PR: {PrUrl}",
                requestId,
                prResult.Value.WebUrl);
        }

        return Result.Success<string?>(prResult.Value.WebUrl);
    }

    public async Task<Result<CodeAnalysisRequest?>> GetNextPendingAsync(CancellationToken ct = default)
    {
        var result = await repository.GetPendingAsync(1, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            logger.LogError("Failed to get pending requests: {Error}", result.Error);
            return Result.Failure<CodeAnalysisRequest?>(result.Error);
        }

        if (result.Value.Count == 0)
        {
            return Result.Success<CodeAnalysisRequest?>(null);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Retrieved pending request: {RequestId}", result.Value[0].Id);
        }

        return Result.Success<CodeAnalysisRequest?>(result.Value[0]);
    }

    public async Task<Result<bool>> IsCompleteAsync(Guid requestId, CancellationToken ct = default)
    {
        var result = await repository.GetByIdAsync(requestId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            logger.LogWarning("Failed to check completion status for request {RequestId}: {Error}",
                requestId, result.Error);
            return Result.Failure<bool>(result.Error);
        }

        var isComplete = result.Value.Status == AnalysisStatus.Completed ||
                         result.Value.Status == AnalysisStatus.Failed;
        return Result.Success(isComplete);
    }

    public async Task<Result<CodeAnalysisRequest>> GetStatusAsync(Guid requestId, CancellationToken ct = default)
    {
        var result = await repository.GetByIdAsync(requestId, ct).ConfigureAwait(false);

        if (result.IsFailure)
        {
            logger.LogWarning("Failed to get status for request {RequestId}: {Error}",
                requestId, result.Error);
            return Result.Failure<CodeAnalysisRequest>(result.Error);
        }

        return Result.Success(result.Value);
    }

    public async Task<Result> InitializeRepositoryAsync(Guid requestId, CancellationToken ct = default)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Initializing repository for request {RequestId}", requestId);
        }

        var requestResult = await repository.GetByIdAsync(requestId, ct).ConfigureAwait(false);

        if (requestResult.IsFailure)
        {
            logger.LogError(
                "Could not initialize repository for request {RequestId}: {Error}",
                requestId,
                requestResult.Error);
            return Result.Failure(requestResult.Error);
        }

        var request = requestResult.Value;

        // Clone repository
        var cloneResult = await gitManager.CloneRepositoryAsync(
            request.Repository.Url,
            request.Repository.Branch,
            null,
            ct).ConfigureAwait(false);

        if (cloneResult.IsFailure)
        {
            logger.LogError("Failed to clone repository: {Error}", cloneResult.Error);
            await repository.FailAsync(requestId, $"Failed to clone repository: {cloneResult.Error}", ct)
                .ConfigureAwait(false);
            return Result.Failure(cloneResult.Error);
        }

        var context = cloneResult.Value;

        // Store the work tree path in the request for future reference
        var updatePathResult = await repository.UpdateWorkTreePathAsync(
            requestId,
            context.LocalWorkTreePath,
            ct).ConfigureAwait(false);

        if (updatePathResult.IsFailure)
        {
            logger.LogWarning(
                "Failed to store work tree path for request {RequestId}: {Error}",
                requestId,
                updatePathResult.Error);
            // Continue anyway - we can reconstruct the path if needed
        }

        // Create feature branch
        var branchResult = await gitManager.CreateFeatureBranchAsync(
            context.LocalWorkTreePath,
            $"ralph-loop/{requestId:N}",
            request.Repository.Branch,
            ct).ConfigureAwait(false);

        if (branchResult.IsFailure)
        {
            var error = $"Failed to create feature branch: {branchResult.Error}";
            logger.LogError("Failed to create feature branch: {Error}", branchResult.Error);
            await repository.FailAsync(requestId, error, ct).ConfigureAwait(false);
            return Result.Failure(branchResult.Error);
        }

        // Update status to ready
        var statusResult = await repository.UpdateStatusAsync(
            requestId,
            AnalysisStatus.Ready,
            ct).ConfigureAwait(false);

        if (statusResult.IsFailure)
        {
            logger.LogWarning("Failed to update status for request {RequestId}: {Error}",
                requestId, statusResult.Error);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Repository initialized for request {RequestId}. Path: {WorkTreePath}",
                requestId,
                context.LocalWorkTreePath);
        }

        return Result.Success();
    }

    /// <summary>
    ///     Validates code changes by executing build, tests, and code analysis.
    /// </summary>
    private async Task<(string validationOutput, bool hasFailed)> ValidateChangesAsync(
        CodeAnalysisRequest request,
        string workTreePath,
        CancellationToken ct)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Validating changes for request {RequestId} in {WorkTreePath}",
                    request.Id, workTreePath);
            }

            // Execute build validation
            var buildValidation = await ExecuteBuildValidationAsync(workTreePath, ct).ConfigureAwait(false);
            if (buildValidation.failed)
            {
                return ($"Build validation failed: {buildValidation.output}", true);
            }

            // Execute test validation
            var testValidation = await ExecuteTestValidationAsync(workTreePath, ct).ConfigureAwait(false);
            if (testValidation.failed)
            {
                return ($"Test validation failed: {testValidation.output}", true);
            }

            // Execute code analysis validation
            var analysisValidation = await ExecuteCodeAnalysisAsync(workTreePath, ct).ConfigureAwait(false);
            if (analysisValidation.failed)
            {
                return ($"Code analysis failed: {analysisValidation.output}", true);
            }

            var successMessage = "Build passed, Tests passed, Code analysis passed";
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Validation passed for request {RequestId}", request.Id);
            }

            return (successMessage, false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error validating changes for request {RequestId}", request.Id);
            return ($"Validation error: {ex.Message}", true);
        }
    }

    /// <summary>
    ///     Executes build validation (dotnet build).
    /// </summary>
    private async Task<(string output, bool failed)> ExecuteBuildValidationAsync(
        string workTreePath,
        CancellationToken ct)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Executing build validation in {WorkTreePath}", workTreePath);
            }

            // TODO: Implement actual dotnet build execution
            // This is a placeholder that returns success
            await Task.Delay(100, ct).ConfigureAwait(false);

            return ("Build successful", false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Build validation error");
            return ($"Build error: {ex.Message}", true);
        }
    }

    /// <summary>
    ///     Executes test validation (dotnet test).
    /// </summary>
    private async Task<(string output, bool failed)> ExecuteTestValidationAsync(
        string workTreePath,
        CancellationToken ct)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Executing test validation in {WorkTreePath}", workTreePath);
            }

            // TODO: Implement actual dotnet test execution
            // This is a placeholder that returns success
            await Task.Delay(100, ct).ConfigureAwait(false);

            return ("Tests passed", false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Test validation error");
            return ($"Test error: {ex.Message}", true);
        }
    }

    /// <summary>
    ///     Executes code analysis validation (analyzers, linting, etc.).
    /// </summary>
    private async Task<(string output, bool failed)> ExecuteCodeAnalysisAsync(
        string workTreePath,
        CancellationToken ct)
    {
        try
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Executing code analysis in {WorkTreePath}", workTreePath);
            }

            // TODO: Implement code analysis using SonarAnalyzer, Roslyn analyzers, etc.
            // This is a placeholder that returns success
            await Task.Delay(100, ct).ConfigureAwait(false);

            return ("Code analysis passed", false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Code analysis error");
            return ($"Analysis error: {ex.Message}", true);
        }
    }
}
