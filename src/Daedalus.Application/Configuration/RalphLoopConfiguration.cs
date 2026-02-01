namespace Daedalus.Application.Configuration;

/// <summary>
///     Configuration for Ralph Loop orchestration engine
/// </summary>
public sealed class RalphLoopConfiguration
{
    public const string SectionName = "RalphLoop";

    /// <summary>
    ///     Delay between iterations in milliseconds
    /// </summary>
    public int IterationDelayMs { get; set; } = 100;

    /// <summary>
    ///     Maximum consecutive failures before stopping
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 5;

    /// <summary>
    ///     Maximum number of iterations (0 = unlimited)
    /// </summary>
#pragma warning disable CA1805 // Do not initialize unnecessarily - intentional default documentation
    public int MaxIterations { get; set; } = 0;
#pragma warning restore CA1805

    /// <summary>
    ///     Request timeout in seconds
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 300;

    /// <summary>
    ///     Whether to enable detailed logging
    /// </summary>
#pragma warning disable CA1805 // Do not initialize unnecessarily - intentional default documentation
    public bool EnableDetailedLogging { get; set; } = false;
#pragma warning restore CA1805

    /// <summary>
    ///     Whether to enable git checkpoint commits after successful iterations.
    ///     The article pattern: "When the tests pass... git add -A... git commit... git push"
    /// </summary>
    public bool EnableGitCheckpoints { get; set; } = true;

    /// <summary>
    ///     Whether to enable loop-back evaluation (build/test after each iteration).
    ///     The article: "Always look for opportunities to loop Ralph back on itself."
    /// </summary>
    public bool EnableLoopbackEvaluation { get; set; } = true;

    /// <summary>
    ///     Whether to delegate expensive work to subagents when the LLM supports them.
    ///     The article: "Your primary context window should operate as a scheduler."
    /// </summary>
    public bool EnableSubagentDelegation { get; set; } = true;

    /// <summary>
    ///     Number of consecutive similar incomplete responses before triggering plan regeneration.
    ///     The article: "Eventually, Ralph will run out of things to do in the task list.
    ///     Or, it goes completely off track."
    /// </summary>
    public int PlanRegenerationThreshold { get; set; } = 3;

    /// <summary>
    ///     Workspace path for git operations, plan file management, and loop-back evaluation.
    ///     If empty, these features are disabled.
    /// </summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>
    ///     Use article-style prompt enhancements ("Think hard", "use the oracle", etc.).
    ///     The article emphasizes explicit reasoning prompts at critical decision points.
    /// </summary>
    public bool UseArticleStylePrompts { get; set; } = true;

    /// <summary>
    ///     Maximum number of items to implement per iteration.
    ///     1 = deep focus on single item (article default), 10 = breadth across multiple items.
    ///     The article: "One item per loop" for quality, but allows tuning to "10 things" for speed.
    /// </summary>
    public int MaxItemsPerIteration { get; set; } = 1;

    /// <summary>
    ///     Maximum subagents for search operations (article uses 500).
    ///     Allows scaling parallel subagent usage for codebase research and analysis.
    /// </summary>
    public int MaxSearchSubagents { get; set; } = 500;

    /// <summary>
    ///     Maximum subagents for build/test operations (article uses 1).
    ///     Prevents concurrent build/test conflicts by limiting to single execution.
    /// </summary>
    public int MaxBuildTestSubagents { get; set; } = 1;
}
