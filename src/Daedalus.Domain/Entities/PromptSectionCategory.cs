namespace Daedalus.Domain.Entities;

/// <summary>
///     Categories of prompt sections that map to Ralph loop behavioral patterns.
/// </summary>
public enum PromptSectionCategory
{
    /// <summary>Initial context loading (specs, plan, source code study).</summary>
    Setup = 0,

    /// <summary>Target repo coding standards and style guides (copilot-instructions.md, .editorconfig).</summary>
    CodingStandards = 11,

    /// <summary>Core task instructions (what to implement).</summary>
    Task = 1,

    /// <summary>Test-first and verification instructions.</summary>
    Testing = 2,

    /// <summary>Plan management (fix_plan.md maintenance).</summary>
    PlanManagement = 3,

    /// <summary>Git workflow (commit, push, tag).</summary>
    GitWorkflow = 4,

    /// <summary>Quality enforcement (no placeholders, full implementations).</summary>
    QualityGuard = 5,

    /// <summary>Self-improvement (update AGENT.md, learnings).</summary>
    SelfImprovement = 6,

    /// <summary>Logging and debugging instructions.</summary>
    Logging = 7,

    /// <summary>Subagent delegation instructions.</summary>
    SubagentDelegation = 8,

    /// <summary>Documentation and spec maintenance.</summary>
    Documentation = 9,

    /// <summary>Custom user-defined instructions.</summary>
    Custom = 10
}
