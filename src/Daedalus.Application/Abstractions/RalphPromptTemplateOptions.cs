using Daedalus.Domain.Entities;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Configuration for how the Ralph prompt template is assembled.
/// </summary>
public record RalphPromptTemplateOptions
{
    /// <summary>The task description / core prompt.</summary>
    public string TaskPrompt { get; set; } = string.Empty;

    /// <summary>The completion promise string that signals success.</summary>
    public string CompletionPromise { get; set; } = string.Empty;

    /// <summary>Workspace context (specs, plan, AGENT.md).</summary>
    public WorkspaceContext? WorkspaceContext { get; set; }

    /// <summary>Maximum parallel subagents to instruct (0 = no subagent instructions).</summary>
    public int MaxParallelSubagents { get; set; }

    /// <summary>
    ///     Maximum number of items to implement per iteration (1 = focused depth, 10 = breadth).
    ///     Aligns with the article's "choose the most important N things" pattern.
    /// </summary>
    public int? MaxItemsPerIteration { get; set; } = 1;

    /// <summary>
    ///     Use article-style prompt enhancements ("Think hard", "use the oracle").
    ///     When true, adds explicit reasoning prompts at critical decision points.
    /// </summary>
    public bool UseArticleStylePrompts { get; set; } = true;

    /// <summary>Whether to include git workflow instructions (commit/push/tag).</summary>
    public bool IncludeGitWorkflow { get; set; } = true;

    /// <summary>Whether to include test-first instructions.</summary>
    public bool IncludeTestInstructions { get; set; } = true;

    /// <summary>Whether to include quality guard instructions (no placeholders).</summary>
    public bool IncludeQualityGuards { get; set; } = true;

    /// <summary>Whether to include self-improvement instructions (update AGENT.md, plan).</summary>
    public bool IncludeSelfImprovement { get; set; } = true;

    /// <summary>Whether to include target repo coding standards (copilot-instructions.md, .editorconfig).</summary>
    public bool IncludeCodingStandards { get; set; } = true;

    /// <summary>Whether to include logging/debugging instructions.</summary>
    public bool IncludeLoggingInstructions { get; set; } = true;

    /// <summary>Additional custom sections to include.</summary>
    public IReadOnlyList<PromptSection> CustomSections { get; set; } = [];

    /// <summary>Categories to exclude from the assembled prompt.</summary>
    public IReadOnlyList<PromptSectionCategory> ExcludedCategories { get; set; } = [];
}
