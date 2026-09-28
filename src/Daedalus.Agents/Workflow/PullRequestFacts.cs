using Thalos.Git;

namespace Daedalus.Agents.Workflow;

/// <summary>
///     Everything <see cref="PullRequestBody.Render"/> writes into a manufacture run's pull request, each read by host
///     code from where the host recorded it. None of it is a node-writable variable except
///     <see cref="AgentSummary"/>, which the body renders under its own label.
/// </summary>
/// <param name="WorkIntent">What was asked, from the run manifest's pinned <c>work_intent</c> document.</param>
/// <param name="Changes">The branch's changed files, from <c>IRunWorkspaceGit.DiffStatAsync</c>.</param>
/// <param name="Checked">
///     Per review lens, what the approving review visit recorded as checked, from the run's
///     <c>review-evidence</c> records.
/// </param>
/// <param name="ApprovedBy">Who resumed the gate, from <c>WorkflowRun.LastResume</c>; null when no resume is recorded.</param>
/// <param name="ApprovedAt">When the gate was resumed, from <c>WorkflowRun.LastResume</c>; null when no resume is recorded.</param>
/// <param name="RunId">The run's id.</param>
/// <param name="Process">The process the run executes.</param>
/// <param name="ProcessVersion">The process version the run executes.</param>
/// <param name="AgentSummary">The implement agent's own summary, from the run's <c>summary</c> variable; null when none.</param>
internal sealed record PullRequestFacts(
    string WorkIntent,
    IReadOnlyList<GitFileChange> Changes,
    IReadOnlyList<(string Lens, IReadOnlyList<string> Checked)> Checked,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    Guid RunId,
    string Process,
    int ProcessVersion,
    string? AgentSummary);
