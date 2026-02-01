using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Application.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daedalus.Application.Commands.RegeneratePlan;

/// <summary>
///     Handles RegeneratePlanCommand by deleting the existing fix_plan.md,
///     invoking the LLM with a plan-generation prompt, and writing the new plan.
///     The article: "Ask Ralph to regenerate the task list from scratch."
/// </summary>
public sealed partial class RegeneratePlanCommandHandler(
    ITaskRepository taskRepository,
    ILlmService llmService,
    IOptions<RalphLoopConfiguration> configurationOptions,
    ILogger<RegeneratePlanCommandHandler> logger)
    : ICommandHandler<RegeneratePlanCommand, Result<RegeneratePlanResult>>
{
    private const string PlanFileName = "fix_plan.md";
    private const string BackupSuffix = ".bak";

    [LoggerMessage(EventId = 60, Level = LogLevel.Information,
        Message = "Regenerating plan for task {TaskId} in workspace {WorkspacePath}")]
    private static partial void LogRegeneratingPlan(ILogger logger, Guid taskId, string workspacePath);

    [LoggerMessage(EventId = 61, Level = LogLevel.Information,
        Message = "Previous plan backed up to {BackupPath}")]
    private static partial void LogPlanBackedUp(ILogger logger, string backupPath);

    [LoggerMessage(EventId = 62, Level = LogLevel.Information,
        Message = "Plan regenerated successfully for task {TaskId}: {PlanLength} characters")]
    private static partial void LogPlanRegenerated(ILogger logger, Guid taskId, int planLength);

    [LoggerMessage(EventId = 63, Level = LogLevel.Error,
        Message = "Failed to regenerate plan for task {TaskId}: {Error}")]
    private static partial void LogPlanRegenerationFailed(ILogger logger, Guid taskId, string error);

    public async Task<Result<RegeneratePlanResult>> Handle(
        RegeneratePlanCommand command,
        CancellationToken cancellationToken)
    {
        // Validate command
        var validation = command.Validate();
        if (validation.IsFailure)
            return Result.Failure<RegeneratePlanResult>(validation.Error);

        // Resolve workspace path
        var workspacePath = command.WorkspacePath ?? configurationOptions.Value.WorkspacePath;
        if (string.IsNullOrEmpty(workspacePath))
            return Result.Failure<RegeneratePlanResult>(
                "WorkspacePath must be provided either in the command or via RalphLoop configuration");

        LogRegeneratingPlan(logger, command.TaskId, workspacePath);

        // Fetch task for context
        var taskResult = await taskRepository.GetByIdAsync(command.TaskId, cancellationToken);
        if (taskResult.IsFailure)
            return Result.Failure<RegeneratePlanResult>($"Task not found: {taskResult.Error}");

        var task = taskResult.Value;

        // Back up existing plan if present
        var planFilePath = Path.Combine(workspacePath, PlanFileName);
        var previousPlanBackedUp = false;

        if (File.Exists(planFilePath))
        {
            var backupPath = planFilePath + BackupSuffix;
            File.Copy(planFilePath, backupPath, overwrite: true);
            previousPlanBackedUp = true;
            LogPlanBackedUp(logger, backupPath);
        }

        // Build plan-generation prompt from task context
        var planPrompt = BuildPlanGenerationPrompt(task);

        // Invoke LLM to generate new plan
        var llmResult = await llmService.InvokeAsync(planPrompt, cancellationToken);
        if (llmResult.IsFailure)
        {
            LogPlanRegenerationFailed(logger, command.TaskId, llmResult.Error);
            return Result.Failure<RegeneratePlanResult>($"LLM plan generation failed: {llmResult.Error}");
        }

        var planContent = llmResult.Value;

        // Write new plan to fix_plan.md
        await File.WriteAllTextAsync(planFilePath, planContent, cancellationToken);

        LogPlanRegenerated(logger, command.TaskId, planContent.Length);

        return Result.Success(new RegeneratePlanResult(
            planFilePath,
            planContent,
            previousPlanBackedUp));
    }

    private static string BuildPlanGenerationPrompt(Domain.Entities.Task task)
    {
        var prompt = $"""
            Generate a detailed implementation plan (fix_plan.md) for the following task.
            The plan should be a markdown checklist that can be executed one item at a time.

            ## Task Details
            - **Title**: {task.Title}
            - **Description**: {task.Description}
            - **Completion Promise**: {task.CompletionPromise}
            - **Phase**: {task.Phase}
            - **Iterations Used**: {task.IterationCount}/{task.MaxIterations}

            ## Requirements
            {task.Prompt}

            ## Previous Learnings
            {task.Learnings ?? "None yet."}

            ## Plan Format
            Create a markdown checklist with:
            1. Each item should be a single, atomic, verifiable step
            2. Steps should be ordered by dependency (prerequisites first)
            3. Each step should include acceptance criteria
            4. Group related steps under phase headers
            5. Include a "Verification" section at the end with build/test commands

            Output ONLY the plan content in markdown format.
            """;

        return prompt;
    }
}
