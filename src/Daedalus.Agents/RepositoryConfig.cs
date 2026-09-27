namespace Daedalus.Agents;

/// <summary>
///     One entry of <c>Thalos:Workflow:Repositories</c>: a repository a manufacture run may target. Phase 2.5 gives
///     every run a git worktree of <see cref="Remote"/> under <see cref="WorkflowConfig.DataRoot"/>, so this list is
///     the whole allow-list of what a run can check out and push to. A start naming anything else is refused.
/// </summary>
public sealed class RepositoryConfig
{
    /// <summary>
    ///     The name a run start refers to the repository by. Must match <c>^[a-z0-9][a-z0-9-]{0,63}$</c> and be unique
    ///     across <see cref="WorkflowConfig.Repositories"/>: it becomes a path segment under the data root.
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>The git remote URL the worktree is cloned from and pushed to. Required.</summary>
    public string Remote { get; set; } = "";

    /// <summary>The branch a run's <c>manufacture/&lt;run-id&gt;</c> branch starts from and targets.</summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>
    ///     The solution file, relative to the repository root, the run's Roslyn server loads. Must be relative and must
    ///     not contain <c>..</c>. <see langword="null"/> when the repository has no solution to load.
    /// </summary>
    public string? Solution { get; set; }
}
