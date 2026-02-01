namespace Daedalus.Application.Abstractions;

/// <summary>
///     Represents loaded workspace context files (specs, plan, agent instructions).
///     Used by the prompt template builder to ground the LLM in workspace state.
/// </summary>
public record WorkspaceContext
{
    /// <summary>The root directory of the workspace.</summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>Contents of specification files (specs/*), keyed by relative path.</summary>
    public IReadOnlyDictionary<string, string> Specifications { get; set; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Contents of the plan file (fix_plan.md or equivalent).</summary>
    public string? PlanContent { get; set; }

    /// <summary>Path to the plan file relative to workspace root.</summary>
    public string? PlanFilePath { get; set; }

    /// <summary>Contents of the agent instruction file (AGENT.md or equivalent).</summary>
    public string? AgentInstructions { get; set; }

    /// <summary>Path to the agent instruction file relative to workspace root.</summary>
    public string? AgentInstructionsPath { get; set; }

    /// <summary>Contents of the target repo's copilot instructions (.github/copilot-instructions.md or equivalent).</summary>
    public string? CopilotInstructions { get; set; }

    /// <summary>Path to the copilot instructions file relative to workspace root.</summary>
    public string? CopilotInstructionsPath { get; set; }

    /// <summary>Contents of the target repo's .editorconfig file for formatting rules.</summary>
    public string? EditorConfig { get; set; }

    /// <summary>When this context was loaded.</summary>
    public DateTime LoadedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Total characters loaded across all files.</summary>
    public int TotalCharactersLoaded =>
        Specifications.Values.Sum(v => v.Length)
        + (PlanContent?.Length ?? 0)
        + (AgentInstructions?.Length ?? 0)
        + (CopilotInstructions?.Length ?? 0)
        + (EditorConfig?.Length ?? 0);
}
