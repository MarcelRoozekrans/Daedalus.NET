using CSharpFunctionalExtensions;

namespace Daedalus.Application.Abstractions;

/// <summary>
///     Loads workspace context files (specs, plan, agent instructions)
///     for injection into Ralph loop prompts.
/// </summary>
public interface IWorkspaceContextProvider
{
    /// <summary>
    ///     Loads all workspace context files from the specified directory.
    ///     Includes target repo coding standards (copilot-instructions.md, .editorconfig)
    ///     to ensure LLM output adheres to the repo's style while maintaining the Ralph loop philosophy.
    /// </summary>
    /// <param name="workspacePath">Root directory of the workspace.</param>
    /// <param name="specsGlob">Glob pattern for specification files (default: "specs/**/*.md").</param>
    /// <param name="planFileName">Name of the plan file (default: "fix_plan.md").</param>
    /// <param name="agentFileName">Name of the agent instructions file (default: "AGENT.md").</param>
    /// <param name="copilotInstructionsPaths">Paths to search for copilot instructions, in priority order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Loaded workspace context or failure.</returns>
    Task<Result<WorkspaceContext>> LoadWorkspaceContextAsync(
        string workspacePath,
        string specsGlob = "specs/**/*.md",
        string planFileName = "fix_plan.md",
        string agentFileName = "AGENT.md",
        IReadOnlyList<string>? copilotInstructionsPaths = null,
        CancellationToken ct = default);
}
