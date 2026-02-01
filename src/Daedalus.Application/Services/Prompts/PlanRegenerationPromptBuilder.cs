namespace Daedalus.Application.Services.Prompts;

/// <summary>
///     Builds prompts for plan regeneration when Ralph gets off track.
///     Based on the explicit planning mode prompts from the Ralph Wiggum article.
///     The article pattern: When Ralph goes "completely off track," delete the plan
///     and run a planning loop with explicit instructions to survey the codebase,
///     find TODOs/placeholders/minimal implementations, and regenerate the plan.
/// </summary>
public static class PlanRegenerationPromptBuilder
{
    /// <summary>
    ///     Builds a planning-mode prompt that instructs Ralph to regenerate the plan
    ///     by surveying the codebase and specifications.
    ///     This is the "escape hatch" when the normal execution loop stalls or goes off track.
    /// </summary>
    /// <param name="specsPath">Path pattern for specification files (default: "specs/*").</param>
    /// <param name="sourcePath">Path to source code directory (default: "src/").</param>
    /// <param name="examplesPath">Path to examples/tests directory (default: "examples/*").</param>
    /// <returns>The complete planning prompt following the article's pattern.</returns>
    public static string BuildPlanningPrompt(
        string specsPath = "specs/*",
        string sourcePath = "src/",
        string examplesPath = "examples/*")
    {
        return
            $@"study {specsPath} to learn about the project specifications and fix_plan.md to understand the plan so far.

The source code is in {sourcePath}. Study it.

The examples and tests are in {examplesPath}. Study them.

First task: Study @fix_plan.md (it may be incorrect). Use up to 500 subagents to study existing source code in {sourcePath} and compare it against specifications. Create/update @fix_plan.md as a bullet point list sorted in priority of items yet to be implemented. Think extra hard and use the oracle to plan. Consider searching for TODO, minimal implementations, and placeholders. Study @fix_plan.md to determine starting point and keep it up to date with items considered complete/incomplete using subagents.

Second task: Use up to 500 subagents to study existing examples and tests in {examplesPath}, then compare against specifications. Update fix_plan.md with missing functionality. Think extra hard and use the oracle to plan.

ULTIMATE GOAL: Complete implementation with full test coverage. Consider missing modules and plan. If specs are missing, create them at specs/COMPONENT.md (search before creating to avoid duplicates). Document the implementation plan in @fix_plan.md.

When @fix_plan.md becomes large, periodically clean out completed items using a subagent.

If you find inconsistencies in {specsPath}, use the oracle and update the specs.";
    }

    /// <summary>
    ///     Builds a compact planning prompt for quick plan updates (not full regeneration).
    ///     Use this when the plan just needs a refresh, not a complete rewrite.
    /// </summary>
    /// <param name="planPath">Path to the plan file (default: "fix_plan.md").</param>
    /// <param name="maxSubagents">Maximum subagents for the survey (default: 100).</param>
    /// <returns>A concise prompt for plan updates.</returns>
    public static string BuildQuickPlanUpdatePrompt(
        string planPath = "fix_plan.md",
        int maxSubagents = 100)
    {
        return $@"Study @{planPath} and use up to {maxSubagents} subagents to:
1. Survey the source code for completed items and remove them from the plan
2. Search for TODO comments, placeholders, and minimal implementations
3. Add any newly discovered issues or missing functionality to the plan
4. Re-sort the plan by priority (most important items first)

Keep the plan concise and actionable. Think hard about what's truly important vs. nice-to-have.";
    }

    /// <summary>
    ///     Builds a specialized prompt for detecting and documenting stalled iterations.
    ///     Use this when Ralph is making similar responses without progress.
    /// </summary>
    /// <param name="stallIterations">Number of iterations that were similar.</param>
    /// <param name="taskId">The task ID that's stalled.</param>
    /// <returns>A prompt instructing Ralph to analyze the stall and suggest alternative approaches.</returns>
    public static string BuildStallAnalysisPrompt(int stallIterations, string taskId)
    {
        return
            $@"STALL DETECTED: Task {taskId} had {stallIterations} consecutive incomplete iterations with similar response patterns.

Your task: Analyze why the loop is stalled and suggest alternative approaches.

1. Review the last {stallIterations} iteration attempts
2. Identify the common failure pattern (what's Ralph trying repeatedly?)
3. Consider alternative implementation strategies
4. Check if the task needs to be broken down into smaller subtasks
5. Verify the completion promise is achievable
6. Update @fix_plan.md with findings and suggested approach changes

Think extra hard about what's blocking progress. Use the oracle to reason about the root cause.";
    }
}
