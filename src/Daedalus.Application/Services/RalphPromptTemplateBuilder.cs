using System.Globalization;
using System.Text;
using CSharpFunctionalExtensions;
using Daedalus.Application.Abstractions;
using Daedalus.Domain.Entities;
using Microsoft.Extensions.Logging;
using ZLinq;
using Task = System.Threading.Tasks.Task;

namespace Daedalus.Application.Services;

/// <summary>
///     Builds structured Ralph loop prompts using the numbered priority convention
///     from the Ralph Wiggum technique. Each section has a priority number and
///     sections are assembled in order to form the complete prompt.
///     Priority convention (from the RLP technique):
///     - 0a, 0b, 0c: Setup / context loading (specs, plan, source code study)
///     - 1: Core task (what to implement)
///     - 2: Testing and verification
///     - 3: Git workflow (commit, push, tag)
///     - 999: Documentation
///     - 9999: Quality enforcement (single source of truth)
///     - 999999: Git tagging
///     - 999999999: Logging
///     - 9999999999: Plan maintenance
///     - 99999999999: Agent self-improvement
///     - Higher numbers: Additional guardrails and constraints
/// </summary>
public sealed partial class RalphPromptTemplateBuilder(
    ILogger<RalphPromptTemplateBuilder> logger) : IRalphPromptTemplateBuilder
{
    public Task<Result<string>> BuildPromptAsync(
        RalphPromptTemplateOptions options,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.TaskPrompt))
        {
            return Task.FromResult(Result.Failure<string>("Task prompt cannot be empty"));
        }

        if (string.IsNullOrWhiteSpace(options.CompletionPromise))
        {
            return Task.FromResult(Result.Failure<string>("Completion promise cannot be empty"));
        }

        var sectionsResult = GetDefaultSections(options);
        if (sectionsResult.IsFailure)
        {
            return Task.FromResult(Result.Failure<string>(sectionsResult.Error));
        }

        var sections = sectionsResult.Value;
        var prompt = AssembleSections(sections);

        LogPromptAssembled(logger, sections.Count, prompt.Length);

        return Task.FromResult(Result.Success(prompt));
    }

    public Result<IReadOnlyList<PromptSection>> GetDefaultSections(RalphPromptTemplateOptions options)
    {
        var sections = new List<PromptSection>();

        // === Setup sections (0a, 0b, 0c) ===
        AddSetupSections(sections, options);

        // === Coding standards section (0d) — target repo style guide ===
        if (options.IncludeCodingStandards)
        {
            AddCodingStandardsSections(sections, options);
        }

        // === Search discipline section (0f) — always included, core Ralph principle ===
        AddSearchDisciplineSection(sections, options.UseArticleStylePrompts);

        // === Core task section (1) ===
        AddTaskSection(sections, options);

        // === Testing section (2) ===
        if (options.IncludeTestInstructions)
        {
            AddTestingSections(sections, options.UseArticleStylePrompts);
        }

        // === Plan management sections (2b) ===
        AddPlanManagementSections(sections);

        // === Git workflow section (3) ===
        if (options.IncludeGitWorkflow)
        {
            AddGitWorkflowSections(sections);
        }

        // === Documentation section (999) ===
        AddDocumentationSection(sections);

        // === Quality enforcement section (9999) ===
        if (options.IncludeQualityGuards)
        {
            AddQualityGuardSections(sections, options.UseArticleStylePrompts);
        }

        // === Logging section (999999999) ===
        if (options.IncludeLoggingInstructions)
        {
            AddLoggingSection(sections);
        }

        // === AGENT.md maintenance section ===
        AddAgentMdMaintenanceSection(sections, options);

        // === Self-improvement sections ===
        if (options.IncludeSelfImprovement)
        {
            AddSelfImprovementSections(sections);
        }

        // === Subagent sections ===
        if (options.MaxParallelSubagents > 0)
        {
            AddSubagentSections(sections, options.MaxParallelSubagents);
        }

        // === No-placeholder enforcement ===
        if (options.IncludeQualityGuards)
        {
            AddNoPlaceholderSection(sections, options.UseArticleStylePrompts);
            AddOneTaskFocusSection(sections);
        }

        // === Custom sections ===
        foreach (var custom in options.CustomSections)
        {
            sections.Add(custom);
        }

        // Filter by excluded categories
        var excluded = options.ExcludedCategories;
        var filtered = sections
            .AsValueEnumerable()
            .Where(s => s.IsEnabled && !excluded.Contains(s.Category))
            .OrderBy(s => s.Priority)
            .ToList();

        return Result.Success<IReadOnlyList<PromptSection>>(filtered.AsReadOnly());
    }

    /// <summary>
    ///     Assembles ordered sections into the final prompt string.
    /// </summary>
    private static string AssembleSections(
        IReadOnlyList<PromptSection> sections)
    {
        var sb = new StringBuilder();

        foreach (var section in sections)
        {
            sb.Append(section.PriorityLabel);
            sb.Append(". ");
            sb.AppendLine(section.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    // ============== Section builders ==============

    /// <summary>
    ///     Setup sections: Load specs, plan, and source code context.
    ///     Priority: 0a, 0b, 0c (highest priority — loaded first every iteration).
    /// </summary>
    private static void AddSetupSections(List<PromptSection> sections, RalphPromptTemplateOptions options)
    {
        var workspace = options.WorkspaceContext;

        // 0a: Study specifications
        if (workspace?.Specifications.Count > 0)
        {
            var specsList = string.Join(", ", workspace.Specifications.Keys.Select(k => $"@{k}"));
            var specsContent =
                $"Study the following specifications to understand the project requirements: {specsList}";
            AddSection(sections, 0, "0a", specsContent, PromptSectionCategory.Setup);
        }
        else
        {
            AddSection(sections, 0, "0a",
                "Study the project specifications and documentation to understand the requirements",
                PromptSectionCategory.Setup);
        }

        // 0b: Source code context
        AddSection(sections, 1, "0b",
            "The source code is in src/. Study the existing implementation before making changes",
            PromptSectionCategory.Setup);

        // 0c: Study the plan
        if (!string.IsNullOrEmpty(workspace?.PlanFilePath))
        {
            AddSection(sections, 2, "0c",
                $"Study @{workspace.PlanFilePath} to understand the current plan and priorities",
                PromptSectionCategory.Setup);
        }
        else
        {
            AddSection(sections, 2, "0c",
                "Study the project plan to understand current priorities and what needs to be done",
                PromptSectionCategory.Setup);
        }
    }

    /// <summary>
    ///     Coding standards sections: Target repo style guide and formatting rules.
    ///     Priority: 0d (after setup, before task — ensures LLM reads style guide before implementation).
    /// </summary>
    private static void AddCodingStandardsSections(List<PromptSection> sections, RalphPromptTemplateOptions options)
    {
        var workspace = options.WorkspaceContext;
        if (workspace is null)
        {
            return;
        }

        // Inject copilot-instructions.md as the authoritative coding standards
        if (!string.IsNullOrWhiteSpace(workspace.CopilotInstructions))
        {
            var sourceNote = !string.IsNullOrWhiteSpace(workspace.CopilotInstructionsPath)
                ? $" (loaded from {workspace.CopilotInstructionsPath})"
                : string.Empty;

            AddSection(sections, 3, "0d",
                $"""
                 ## CODING STANDARDS{sourceNote}

                 The target repository has the following coding standards. You MUST follow these conventions
                 in all code you generate, modify, or review. These are non-negotiable project rules:

                 {workspace.CopilotInstructions}
                 """,
                PromptSectionCategory.CodingStandards);
        }

        // Inject .editorconfig as supplementary formatting rules
        if (!string.IsNullOrWhiteSpace(workspace.EditorConfig))
        {
            AddSection(sections, 4, "0e",
                $"""
                 ## EDITOR CONFIGURATION (.editorconfig)

                 The target repository uses the following .editorconfig formatting rules.
                 Ensure all generated code adheres to these formatting conventions:

                 ```editorconfig
                 {workspace.EditorConfig}
                 ```
                 """,
                PromptSectionCategory.CodingStandards);
        }
    }

    /// <summary>
    ///     Search discipline section: Enforce search-before-implement pattern.
    ///     Priority: 9 (after coding standards, just before task, ensures LLM searches first).
    /// </summary>
    private static void AddSearchDisciplineSection(List<PromptSection> sections, bool useArticleStyle)
    {
        var thinkHard = useArticleStyle ? " Think hard" : string.Empty;
        AddSection(sections, 9, "0f",
            "CRITICAL: Before making any changes, search the codebase using subagents to confirm " +
            "the functionality is not already implemented. Do NOT assume something is missing just " +
            "because one search didn't find it. Code search is non-deterministic. " +
            $"Try multiple search strategies (class name, method name, file patterns).{thinkHard}",
            PromptSectionCategory.Task);
    }

    /// <summary>
    ///     Core task section: What to implement.
    ///     Priority: 1 (the main directive).
    /// </summary>
    private static void AddTaskSection(List<PromptSection> sections, RalphPromptTemplateOptions options)
    {
        var taskContent = new StringBuilder();
        taskContent.Append(options.TaskPrompt);
        var thinkHard = options.UseArticleStylePrompts ? " Think hard" : string.Empty;

        // Add plan-driven guidance with configurable item count
        if (!string.IsNullOrEmpty(options.WorkspaceContext?.PlanContent))
        {
            var taskCount = options.MaxItemsPerIteration ?? 1;
            var itemText = taskCount == 1 ? "thing" : $"{taskCount} things";

            taskContent.Append(". Follow the plan and choose the most important ");
            taskContent.Append(itemText);
            taskContent.Append(" to implement. ");
            taskContent.Append(
                "Before making changes, search the codebase using subagents (don't assume not implemented).");
            taskContent.Append(thinkHard);
        }
        else
        {
            taskContent.Append(". Choose the most important thing to implement based on the project requirements.");
            taskContent.Append(thinkHard);
        }

        if (options.MaxParallelSubagents > 0)
        {
            taskContent.Append(CultureInfo.InvariantCulture,
                $" You may use up to {options.MaxParallelSubagents} parallel subagents for operations");
        }

        AddSection(sections, 10, "1", taskContent.ToString(), PromptSectionCategory.Task);
    }

    /// <summary>
    ///     Testing sections: Run tests after implementation.
    ///     Priority: 20-29.
    /// </summary>
    private static void AddTestingSections(List<PromptSection> sections, bool useArticleStyle)
    {
        var thinkHard = useArticleStyle ? " Think hard" : string.Empty;
        AddSection(sections, 20, "2",
            "After implementing functionality or resolving problems, run the tests for that unit of code. " +
            $"If functionality is missing then it's your job to add it as per the project specifications.{thinkHard}",
            PromptSectionCategory.Testing);

        AddSection(sections, 21, "2b",
            "When you discover a bug or issue, immediately update the plan with your findings. " +
            "When the issue is resolved, update the plan and remove the item",
            PromptSectionCategory.PlanManagement);
    }

    /// <summary>
    ///     Plan management sections.
    ///     Priority: 21-29.
    /// </summary>
    private static void AddPlanManagementSections(List<PromptSection> sections)
    {
        AddSection(sections, 100, "9999999999",
            "ALWAYS keep the plan (@fix_plan.md) up to date with your learnings. " +
            "When you discover bugs or missing functionality, document them in @fix_plan.md using a subagent. " +
            "When items are completed, remove them from the plan. " +
            "Especially important: update after wrapping up or finishing your turn",
            PromptSectionCategory.PlanManagement);

        AddSection(sections, 110, "99999999999999999999999",
            "When the plan becomes large, periodically clean out the items that are completed using a subagent",
            PromptSectionCategory.PlanManagement);
    }

    /// <summary>
    ///     Git workflow sections: Commit, push, tag.
    ///     Priority: 30-39.
    /// </summary>
    private static void AddGitWorkflowSections(List<PromptSection> sections)
    {
        AddSection(sections, 30, "3",
            "When the tests pass, update the plan, then add changed code with \"git add -A\" " +
            "then do a \"git commit\" with a message that describes the changes. " +
            "After the commit do a \"git push\" to push the changes to the remote repository",
            PromptSectionCategory.GitWorkflow);

        AddSection(sections, 60, "999999",
            "As soon as there are no build or test errors, create a git tag. " +
            "If there are no git tags start at 0.0.0 and increment patch by 1",
            PromptSectionCategory.GitWorkflow);
    }

    /// <summary>
    ///     Documentation section.
    ///     Priority: 50.
    /// </summary>
    private static void AddDocumentationSection(List<PromptSection> sections)
    {
        AddSection(sections, 50, "999",
            "When authoring documentation, capture why the tests and the backing implementation are important",
            PromptSectionCategory.Documentation);
    }

    /// <summary>
    ///     Quality enforcement sections.
    ///     Priority: 55-65.
    /// </summary>
    private static void AddQualityGuardSections(List<PromptSection> sections, bool useArticleStyle)
    {
        var thinkHard = useArticleStyle ? " Think hard" : string.Empty;
        AddSection(sections, 55, "9999",
            "We want single sources of truth, no migrations or adapters. " +
            "If tests unrelated to your work fail then it's your job to resolve these tests " +
            $"as part of the increment of change.{thinkHard}",
            PromptSectionCategory.QualityGuard);
    }

    /// <summary>
    ///     AGENT.md maintenance section: Self-improving build/run instructions.
    ///     Priority: 65 (after quality guards, before self-improvement).
    ///     Always added when workspace context exists, encourages creating AGENT.md if missing.
    /// </summary>
    private static void AddAgentMdMaintenanceSection(List<PromptSection> sections, RalphPromptTemplateOptions options)
    {
        if (options.WorkspaceContext == null)
        {
            return;
        }

        AddSection(sections, 65, "99999999999",
            "When you learn something new about how to run the compiler, build the project, " +
            "or execute tests, update or create @AGENT.md using a subagent but keep it brief. " +
            "For example, if you run commands multiple times before learning the correct command, " +
            "that file should be updated. " +
            "IMPORTANT: DO NOT PLACE STATUS REPORT UPDATES INTO @AGENT.md — only build/run/test instructions",
            PromptSectionCategory.PlanManagement);
    }

    /// <summary>
    ///     Logging instructions section.
    ///     Priority: 70.
    /// </summary>
    private static void AddLoggingSection(List<PromptSection> sections)
    {
        AddSection(sections, 70, "999999999",
            "You may add extra logging if required to be able to debug the issues",
            PromptSectionCategory.Logging);
    }

    /// <summary>
    ///     Self-improvement sections: Update AGENT.md and plan with learnings.
    ///     Priority: 80-95.
    /// </summary>
    private static void AddSelfImprovementSections(List<PromptSection> sections)
    {
        AddSection(sections, 80, "99999999999",
            "When you learn something new about how to run the project or build process, " +
            "update the agent instructions file to keep it brief and accurate. " +
            "For example, if you run commands multiple times before learning the correct command, " +
            "that file should be updated",
            PromptSectionCategory.SelfImprovement);

        AddSection(sections, 90, "999999999999999999999",
            "For any bugs you notice, it's important to resolve them or document them in the plan " +
            "to be resolved, even if it is unrelated to the current piece of work",
            PromptSectionCategory.SelfImprovement);

        AddSection(sections, 95, "9999999999999999999999999999999",
            "IMPORTANT: DO NOT PLACE STATUS REPORT UPDATES INTO THE AGENT INSTRUCTIONS FILE",
            PromptSectionCategory.SelfImprovement);
    }

    /// <summary>
    ///     Subagent delegation sections.
    ///     Priority: 85.
    /// </summary>
    private static void AddSubagentSections(List<PromptSection> sections, int maxSubagents)
    {
        AddSection(sections, 85, "99999999999999",
            $"Use up to {maxSubagents} parallel subagents for research, analysis, and implementation. " +
            "Use only 1 subagent for build and test operations to avoid conflicts. " +
            "The primary context should act as a scheduler, delegating expensive work to subagents",
            PromptSectionCategory.SubagentDelegation);
    }

    /// <summary>
    ///     No-placeholder enforcement section (highest priority guardrail).
    ///     Priority: 120.
    /// </summary>
    private static void AddNoPlaceholderSection(List<PromptSection> sections, bool useArticleStyle)
    {
        var thinkHard = useArticleStyle ? " Think hard" : string.Empty;
        AddSection(sections, 120, "9999999999999999999999999999",
            "DO NOT IMPLEMENT PLACEHOLDER OR SIMPLE IMPLEMENTATIONS. " +
            "WE WANT FULL IMPLEMENTATIONS. DO IT OR I WILL YELL AT YOU. " +
            "If functionality is missing then implement it fully — no stubs, no TODO comments, " +
            $"no 'will implement later'. Every function must have real logic.{thinkHard}",
            PromptSectionCategory.QualityGuard);
    }

    /// <summary>
    ///     One-task-per-loop enforcement section.
    ///     The article emphasizes: "One item per loop. I need to repeat myself here — one item per loop."
    ///     Priority: int.MaxValue - 1000 (extreme priority to be read last, using article's pattern).
    /// </summary>
    private static void AddOneTaskFocusSection(List<PromptSection> sections)
    {
        AddSection(sections, int.MaxValue - 1000, "99999999999999999999999999999",
            "FOCUS ON ONE THING PER ITERATION. Do not try to implement multiple features or fix " +
            "multiple unrelated issues in a single pass. Pick the single most important item from " +
            "the plan, implement it fully, test it, commit it, then stop. " +
            "Breadth kills quality — depth on one item produces better outcomes",
            PromptSectionCategory.QualityGuard);
    }

    /// <summary>
    ///     Helper to add a section with validation.
    /// </summary>
    private static void AddSection(
        List<PromptSection> sections,
        int priority,
        string label,
        string content,
        PromptSectionCategory category)
    {
        var result = PromptSection.Create(priority, label, content, category);
        if (result.IsSuccess)
        {
            sections.Add(result.Value);
        }
    }

    // ============== Logging Methods ==============

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Ralph prompt assembled: {SectionCount} sections, {PromptLength} characters")]
    private static partial void LogPromptAssembled(ILogger logger, int sectionCount, int promptLength);
}
