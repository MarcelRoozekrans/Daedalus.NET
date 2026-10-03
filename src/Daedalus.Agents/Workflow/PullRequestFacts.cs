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
/// <param name="TestResult">
///     The run's last recorded sandbox test result, from its <c>test-result</c> records; null when the run recorded none.
///     It is reported by the run's sandbox, not verified, and the body says so.
/// </param>
internal sealed record PullRequestFacts(
    string WorkIntent,
    IReadOnlyList<GitFileChange> Changes,
    IReadOnlyList<(string Lens, IReadOnlyList<string> Checked)> Checked,
    string? ApprovedBy,
    DateTimeOffset? ApprovedAt,
    Guid RunId,
    string Process,
    int ProcessVersion,
    string? AgentSummary,
    TestResultFacts? TestResult = null);

/// <summary>
///     A sandbox test result as the run's <c>test-result</c> record holds it: what the run's sandbox reported, which ran
///     code from the change, so nothing here is verified by host code.
/// </summary>
/// <param name="Node">The process node the run was on when the call completed: <c>implement</c> or <c>review</c>.</param>
/// <param name="Tool">The sandbox tool called, <c>test</c> or <c>build</c>.</param>
/// <param name="Exit">The exit code the sandbox reported, <c>timed out ...</c>, or <c>error</c>.</param>
/// <param name="Summary">The one-line summary the sandbox reported.</param>
internal sealed record TestResultFacts(string Node, string Tool, string Exit, string Summary);
